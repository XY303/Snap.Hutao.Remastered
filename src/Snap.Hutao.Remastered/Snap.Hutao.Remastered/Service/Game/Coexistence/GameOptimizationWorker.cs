// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

internal static partial class GameOptimizationWorker
{
    internal static bool ShouldOptimize(bool gameRunning, bool allowDuringGame, uint idleMilliseconds)
        => gameRunning ? allowDuringGame : idleMilliseconds >= 120_000;

    // Keep hashing, cloning and their I/O on this dedicated background-mode thread.
    // Thread-pool continuations would lose Windows CPU/I/O/memory background priority.
    public static Task<T> RunAsync<T>(Func<T> operation)
    {
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            bool background = false;
            try
            {
                background = SetThreadPriority(GetCurrentThread(), 0x10000);
                if (!background) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                completion.SetResult(operation());
            }
            catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                if (background) { SetThreadPriority(GetCurrentThread(), 0x20000); }
            }
        }) { IsBackground = true, Name = "Hutao space optimization", Priority = ThreadPriority.Lowest }.Start();
        return completion.Task;
    }

    internal static Task<byte[]> HashAsync(Stream stream, bool background, CancellationToken token)
    {
        if (!background) { return SHA256.HashDataAsync(stream, token).AsTask(); }
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024];
        int read;
        do
        {
            token.ThrowIfCancellationRequested();
            read = stream.Read(buffer);
            hash.AppendData(buffer, 0, read);
        }
        while (read != 0);
        return Task.FromResult(hash.GetHashAndReset());
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(nint thread, int priority);
}
