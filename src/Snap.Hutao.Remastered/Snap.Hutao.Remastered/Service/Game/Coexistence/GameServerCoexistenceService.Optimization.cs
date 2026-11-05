// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed partial class GameServerCoexistenceService
{
    private readonly SemaphoreSlim optimizationGate = new(1, 1);
    private readonly object optimizationStateLock = new();
    private CancellationTokenSource? optimizationCancellation;
    private int optimizationPauseRequests;
    private int automaticOptimizationStarted;

    // Package operations cancel maintenance and wait for its file handles to close.
    public async Task<IDisposable> PauseOptimizationAsync(CancellationToken token)
    {
        Interlocked.Increment(ref optimizationPauseRequests);
        lock (optimizationStateLock) { optimizationCancellation?.Cancel(); }
        try { await optimizationGate.WaitAsync(token).ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref optimizationPauseRequests); throw; }
        return new OptimizationPause(this);
    }

    private sealed class OptimizationPause(GameServerCoexistenceService owner) : IDisposable
    {
        private GameServerCoexistenceService? service = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref service, null) is { } value)
            {
                Interlocked.Decrement(ref value.optimizationPauseRequests);
                value.optimizationGate.Release();
            }
        }
    }

    public void StartAutomaticOptimization()
    {
        if (Interlocked.Exchange(ref automaticOptimizationStarted, 1) != 0) { return; }
        _ = Task.Run(AutomaticOptimizationLoopAsync);
    }

    private async Task AutomaticOptimizationLoopAsync()
    {
        CancellationToken token = XamlApplicationLifetime.ShutdownToken;
        Dictionary<string, DateTimeOffset> nextRuns = new(StringComparer.OrdinalIgnoreCase);
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                string container = launchOptions.CoexistenceContainer.Value;
                try
                {
                    if (!launchOptions.CoexistenceEnabled.Value || !launchOptions.CoexistenceAutoOptimize.Value || string.IsNullOrEmpty(container)) { continue; }
                    if (nextRuns.TryGetValue(container, out DateTimeOffset next) && DateTimeOffset.UtcNow < next) { continue; }
                    bool running = await GameLifeCycle.IsGameRunningAsync(taskContext).ConfigureAwait(false);
                    if (!GameOptimizationWorker.ShouldOptimize(running, launchOptions.CoexistenceOptimizeDuringGame.Value, GetIdleMilliseconds())) { continue; }
                    if (Volatile.Read(ref optimizationPauseRequests) != 0 || !await optimizationGate.WaitAsync(0, token).ConfigureAwait(false)) { continue; }
                    try
                    {
                        // Do not mount an unused/offline library just for maintenance.
                        GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(container) ?? throw new IOException("共存描述文件丢失。");
                        GameCoexistenceVirtualDisk.AssertMounted(disk.ChineseExecutable);
                        GameSpaceScan scan = await RunOptimizationAsync(disk, true, null, token, automatic: true).ConfigureAwait(false);
                        nextRuns[container] = DateTimeOffset.UtcNow.AddHours(6);
                        Trace.TraceInformation("Background space optimization: {0}", scan.Description);
                    }
                    finally { optimizationGate.Release(); }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    nextRuns[container] = DateTimeOffset.UtcNow.AddMinutes(10);
                    Trace.TraceWarning("Background space optimization deferred: {0}", ex);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<GameSpaceScan> RunOptimizationAsync(GameVirtualDiskInfo disk, bool optimize, IProgress<string>? progress, CancellationToken token, bool automatic)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, XamlApplicationLifetime.ShutdownToken);
        lock (optimizationStateLock)
        {
            optimizationCancellation = cancellation;
            if (Volatile.Read(ref optimizationPauseRequests) != 0) { cancellation.Cancel(); }
        }
        try { return await RunOptimizationOnWorkerAsync(disk, optimize, progress, cancellation.Token, automatic).ConfigureAwait(false); }
        finally { lock (optimizationStateLock) { optimizationCancellation = null; } }
    }

    private Task<GameSpaceScan> RunOptimizationOnWorkerAsync(GameVirtualDiskInfo disk, bool optimize, IProgress<string>? progress, CancellationToken token, bool automatic)
        => GameOptimizationWorker.RunAsync(() =>
        {
            Service.Game.Package.Advanced.IGamePackageService packages = serviceProvider.GetRequiredService<Service.Game.Package.Advanced.IGamePackageService>();
            IDisposable AcquireAccess()
            {
                token.ThrowIfCancellationRequested();
                if (packages.IsOperationRunning) { throw new OperationCanceledException("优先执行游戏更新或修复。", token); }
                if (automatic && (!launchOptions.CoexistenceAutoOptimize.Value || !launchOptions.CoexistenceEnabled.Value
                    || !disk.Container.Equals(launchOptions.CoexistenceContainer.Value, StringComparison.OrdinalIgnoreCase)
                    || !GameOptimizationWorker.ShouldOptimize(LaunchOptions.IsGameRunning.Value, launchOptions.CoexistenceOptimizeDuringGame.Value, GetIdleMilliseconds())))
                {
                    throw new OperationCanceledException("自动空间优化已暂停。", token);
                }
                if (!launchOptions.GamePathLock.TryReaderLock("SpaceOptimization", out AsyncReaderWriterLock.Releaser lease))
                {
                    throw new OperationCanceledException("优先执行游戏目录操作。", token);
                }
                return lease;
            }

            GameSpaceScan scan = GameSpaceOptimizer.ScanAsync(disk, progress, token, background: true, AcquireAccess).GetAwaiter().GetResult();
            if (!optimize || scan.Duplicates.Count == 0) { return scan; }
            foreach (GameSpaceDuplicate duplicate in scan.Duplicates)
            {
                using IDisposable access = AcquireAccess();
                progress?.Report(duplicate.Target);
                try { GameRefsBlockClone.DeduplicateAsync(duplicate.Source, duplicate.Target, duplicate.Hash, token, background: true).GetAwaiter().GetResult(); }
                catch (IOException ex) { Trace.TraceInformation("Skip changing/in-use resource {0}: {1}", duplicate.Target, ex.Message); }
                catch (Exception ex) when (GameSpaceOptimizer.IsTransientFileError(ex)) { }
            }
            return GameSpaceOptimizer.ScanAsync(disk, progress, token, background: true, AcquireAccess).GetAwaiter().GetResult();
        });

    private static uint GetIdleMilliseconds()
    {
        LastInputInfo info = new() { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);
}
