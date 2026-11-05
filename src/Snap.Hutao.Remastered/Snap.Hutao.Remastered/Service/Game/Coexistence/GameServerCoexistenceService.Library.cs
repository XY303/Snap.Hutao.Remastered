// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Service.Game.Configuration;
using Snap.Hutao.Remastered.Service.Game.FileSystem;
using Snap.Hutao.Remastered.Service.Game.Package;
using Snap.Hutao.Remastered.Service.Game.Package.Advanced;
using Snap.Hutao.Remastered.Service.Game.Scheme;
using Snap.Hutao.Remastered.Web.Hoyolab.HoyoPlay.Connect.Branch;
using System.IO;
using System.Net.Http;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameLibraryImportResult(string Executable, bool OriginalDeleted, Exception? CleanupError);

public sealed partial class GameServerCoexistenceService
{
    public async Task SelectLibraryAsync(string container, string selected)
    {
        await taskContext.SwitchToMainThreadAsync();
        launchOptions.CoexistenceContainer.Value = container;
        launchOptions.CoexistenceEnabled.Value = true;
        launchOptions.PerformGamePathEntrySynchronization(selected);
        StartAutomaticOptimization();
    }

    public async Task<GameLibraryImportResult> EnableLibraryAsync(string executable, string container, IProgress<string> progress, CancellationToken token, bool deleteOriginal = false)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameInstallationInfo installation = InspectInstallation(executable);
            if (GameCoexistenceVirtualDisk.Find(executable) is not null) { throw new IOException("请先选择磁盘外的原始安装，或复用之前的版本库。"); }
            string sourceRoot = GameServerCoexistenceStorage.GetRoot(executable);
            if (Path.GetDirectoryName(sourceRoot) is null) { throw new IOException("不能将整个磁盘根目录作为待迁移的游戏安装目录。"); }
            if (Path.GetFullPath(container).StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new IOException("虚拟磁盘不能放入源游戏目录。"); }
            if (GameVersionLibrary.FindEntry(executable) is { Pending: true }) { throw new IOException("未完成的版本不能作为源安装。"); }
            GameVirtualDiskInfo disk = await GameCoexistenceVirtualDisk.CreateAtAsync(container, GameInstallationTransfer.GetRequiredBytes(executable), token).ConfigureAwait(false);
            GameVersionEntry entry = new(Guid.NewGuid(), installation.IsOversea, (int)installation.Channel.Channel, (int)installation.Channel.SubChannel, installation.InstalledVersion!.ToString(), true);
            GameVersionLibrary library = new(executable, [entry]);
            library.Save(disk);
            string target = entry.GetExecutable(disk);
            await GameInstallationTransfer.CopyAsync(executable, target, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            InspectInstallation(target);
            library.Entries[0] = entry with { Pending = false };
            library.Save(disk);
            if (!deleteOriginal) { return new(target, false, null); }
            try
            {
                await EnsureGameStoppedAsync().ConfigureAwait(false);
                library = library with { OriginalCleanupPending = true };
                library.Save(disk); // Never route back to a partially cleaned source after a crash.
                await GameInstallationTransfer.DeleteVerifiedSourceAsync(executable, target, progress, token).ConfigureAwait(false);
                return new(target, true, null);
            }
            catch (Exception ex)
            {
                // Import is already committed. Even cancelled/partial cleanup must
                // return the complete VHDX install so the UI can select it.
                return new(target, false, ex);
            }
        }
    }

    public async Task<string> SelectOrConvertAsync(string executable, LaunchScheme target, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            if (!target.IsNotCompatOnly || !KnownLaunchSchemes.Values.Contains(target)) { throw new IOException("不支持的区服方案。"); }
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value)
                ?? throw new IOException("虚拟磁盘描述文件丢失。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            foreach (GameVersionEntry candidate in library.GetReadyEntries(disk, executable).Where(e => Matches(e, target)))
            {
                string path = candidate.GetExecutable(disk);
                if (File.Exists(path) && target.Equals(InspectInstallation(path).Channel)) { return path; }
            }

            // Only a catalogued complete installation on this same disk can be a snapshot source.
            GameVersionEntry sourceEntry = library.Entries.SingleOrDefault(e => !e.Pending && e.GetExecutable(disk).Equals(executable, StringComparison.OrdinalIgnoreCase))
                ?? throw new IOException("请先在共存版本列表中选择完整的源安装。");
            GameInstallationInfo installation = InspectInstallation(executable);
            LaunchScheme current = KnownLaunchSchemes.Values.Single(s => s.Equals(installation.Channel));
            using IServiceScope scope = serviceProvider.CreateScope();
            IHoyoPlayService hoyoPlay = scope.ServiceProvider.GetRequiredService<IHoyoPlayService>();
            if (await hoyoPlay.TryGetBranchesAsync(current).ConfigureAwait(false) is not (true, { } currentBranches)
                || await hoyoPlay.TryGetBranchesAsync(target).ConfigureAwait(false) is not (true, { } targetBranches)
                || await hoyoPlay.TryGetChannelSDKsAsync(target).ConfigureAwait(false) is not (true, { } sdks)
                || await hoyoPlay.TryGetDeprecatedFileConfigurationsAsync(target).ConfigureAwait(false) is not (true, { } deprecated))
            {
                throw new IOException(SH.ViewModelLaunchGameEnsureGameResourceFail);
            }

            BranchWrapper currentBranch = currentBranches.GetMainBranch(current.GameId);
            BranchWrapper targetBranch = targetBranches.GetMainBranch(target.GameId);
            if (!Version.TryParse(currentBranch.Tag, out Version? version) || version != installation.InstalledVersion
                || !Version.TryParse(targetBranch.Tag, out Version? targetVersion) || targetVersion != version)
            {
                throw new IOException("转换要求源安装和目标区服均为当前正式版本，请先更新并校验游戏。");
            }

            GameVersionEntry entry = new(Guid.NewGuid(), target.IsOversea, (int)target.Channel, (int)target.SubChannel, targetVersion.ToString(), true);
            library.Entries.Add(entry);
            library.Save(disk); // Interrupted creations stay pending and are never launchable.
            string destination = entry.GetExecutable(disk);
            string snapshot = Path.Combine(Path.GetDirectoryName(destination)!, Path.GetFileName(executable));
            await GameVersionLibrary.CopySnapshotAsync(executable, snapshot, progress, token).ConfigureAwait(false);
            using HttpClient httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GamePackageService.HttpClientName);
            using CancellationTokenRegistration registration = token.Register(httpClient.CancelPendingRequests);
            IPackageConverter converter = scope.ServiceProvider.GetRequiredService<IPackageConverter>();
            if (current.IsOversea != target.IsOversea)
            {
                using IGameFileSystem fileSystem = GameFileSystem.CreateForPackageOperation(snapshot);
                PackageConverterContext context = new(current, target, fileSystem)
                {
                    HttpClient = httpClient,
                    CurrentBranch = currentBranch,
                    TargetBranch = targetBranch,
                    Progress = new ConversionProgress(progress),
                };
                if (!await converter.EnsureGameResourceAsync(context).ConfigureAwait(false)) { throw new IOException(SH.ViewModelLaunchGameEnsureGameResourceFail); }
            }

            token.ThrowIfCancellationRequested();
            using (IGameFileSystem targetFileSystem = GameFileSystem.CreateForPackageOperation(destination))
            {
                await converter.EnsureDeprecatedFilesAndSDKAsync(new(httpClient, targetFileSystem, sdks.GameChannelSDKs.SingleOrDefault(), deprecated.DeprecatedFileConfigurations.SingleOrDefault())).ConfigureAwait(false);
                GameConfiguration.Create(target, targetVersion.ToString(), targetFileSystem.GameConfigurationFilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetFileSystem.ScriptVersionFilePath)!);
                File.WriteAllText(targetFileSystem.ScriptVersionFilePath, targetVersion.ToString());
            }

            token.ThrowIfCancellationRequested();
            InspectInstallation(destination);
            library.Entries[^1] = entry with { Pending = false };
            library.Save(disk);
            return destination;
        }
    }

    public async Task DeleteVersionAsync(Guid id, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            GameVersionEntry entry = library.Entries.Single(e => e.Id == id);
            if (entry.GetExecutable(disk).Equals(launchOptions.GamePathEntry.Value?.Path, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("不能删除当前选中的安装，请先选择其他版本或关闭共存，再删除此版本。");
            }

            if (launchOptions.CoexistenceEnabled.Value && !entry.Pending && library.Entries.Count(e => !e.Pending) == 1)
            {
                throw new IOException("请先关闭共存，再删除最后一个完整版本。");
            }

            library.DeleteVersion(disk, id, progress, token);
        }
    }

    public async Task<GameSpaceScan> ScanSpaceAsync(bool optimize, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        if (!await optimizationGate.WaitAsync(0, token).ConfigureAwait(false)) { throw new IOException("空间优化正在后台运行，请稍后重试，或关闭自动优化后重试。"); }
        try
        {
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
            return await RunOptimizationAsync(disk, optimize, progress, token, automatic: false).ConfigureAwait(false);
        }
        finally { optimizationGate.Release(); }
    }

    public async Task<string> ReopenLibraryAsync(string container, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(container) ?? throw new IOException("找不到虚拟磁盘。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            GameVersionEntry entry = library.Entries.LastOrDefault(e => !e.Pending) ?? throw new IOException("版本库中没有完整安装。");
            string selected = entry.GetExecutable(disk);
            InspectInstallation(selected);
            return selected;
        }
    }

    public async Task<string?> GetOriginalForDisableAsync(CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            token.ThrowIfCancellationRequested();
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            string original = library.OriginalExecutable;
            return !library.OriginalCleanupPending && File.Exists(original) ? original : null;
        }
    }

    public async Task<string> ExportSelectedInstallationAsync(string destinationFolder, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            string selected = launchOptions.GamePathEntry.Value?.Path ?? throw new IOException("未选择共存版本。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(selected, token).ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.Find(selected) ?? throw new IOException("当前安装不在共存虚拟盘内。");
            if (GameVersionLibrary.FindEntry(selected) is not { Pending: false }) { throw new IOException("只能恢复完整的共存版本。"); }
            string folder = Path.GetFullPath(destinationFolder);
            string container = Path.TrimEndingDirectorySeparator(disk.Container);
            if (folder.Equals(container, StringComparison.OrdinalIgnoreCase) || folder.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || GameCoexistenceVirtualDisk.Find(Path.Combine(folder, "YuanShen.exe")) is not null) { throw new IOException("恢复目录必须位于共存虚拟盘之外。"); }
            string existing = folder;
            while (!Directory.Exists(existing)) { existing = Path.GetDirectoryName(existing) ?? throw new IOException("恢复目标磁盘不存在。"); }
            if (GameRefsBlockClone.GetVolume(existing).Equals(disk.VolumeName, StringComparison.OrdinalIgnoreCase)) { throw new IOException("恢复目标仍位于同一个虚拟盘内，请选择宿主磁盘上的目录。"); }
            string target = Path.Combine(folder, Path.GetFileName(selected));
            InspectInstallation(selected);
            await GameInstallationTransfer.CopyAsync(selected, target, progress, token).ConfigureAwait(false);
            InspectInstallation(target);
            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            (library with { OriginalExecutable = target, OriginalCleanupPending = false }).Save(disk);
            return target;
        }
    }


    private static bool Matches(GameVersionEntry entry, LaunchScheme scheme) => entry.IsOversea == scheme.IsOversea && entry.Channel == (int)scheme.Channel && entry.SubChannel == (int)scheme.SubChannel;
}
