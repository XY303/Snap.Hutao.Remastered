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

public sealed partial class GameServerCoexistenceService
{
    public async Task<string> EnableLibraryAsync(string executable, string container, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameInstallationInfo installation = InspectInstallation(executable);
            if (GameCoexistenceVirtualDisk.Find(executable) is not null) { throw new IOException("请先选择磁盘外的原始安装，或复用之前的版本库。"); }
            string sourceRoot = GameServerCoexistenceStorage.GetRoot(executable);
            if (Path.GetFullPath(container).StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new IOException("虚拟磁盘不能放入源游戏目录。"); }
            if (GameVersionLibrary.FindEntry(executable) is { Pending: true }) { throw new IOException("未完成的版本不能作为源安装。"); }
            IReadOnlyList<string> files = GameServerCoexistenceStorage.GetSourceFiles(executable);
            GameVirtualDiskInfo disk = await GameCoexistenceVirtualDisk.CreateAtAsync(container, files.Sum(f => new FileInfo(f).Length), token).ConfigureAwait(false);
            GameVersionEntry entry = new(Guid.NewGuid(), installation.IsOversea, (int)installation.Channel.Channel, (int)installation.Channel.SubChannel, installation.InstalledVersion!.ToString(), true);
            GameVersionLibrary library = new(executable, [entry]);
            library.Save(disk);
            string target = entry.GetExecutable(disk);
            await GameVersionLibrary.CopySnapshotAsync(executable, target, progress, token).ConfigureAwait(false);
            library.Entries[0] = entry with { Pending = false };
            library.Save(disk);
            return target;
        }
    }

    public async Task<string> SelectOrConvertAsync(string executable, LaunchScheme target, bool alwaysCreate, IProgress<string> progress, CancellationToken token)
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
            if (!alwaysCreate)
            {
                foreach (GameVersionEntry candidate in library.Entries.AsEnumerable().Reverse().Where(e => !e.Pending && Matches(e, target)).OrderByDescending(e => e.GetExecutable(disk).Equals(executable, StringComparison.OrdinalIgnoreCase)))
                {
                    string path = candidate.GetExecutable(disk);
                    if (File.Exists(path) && target.Equals(InspectInstallation(path).Channel)) { return path; }
                }
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

    public async Task<GameSpaceScan> ScanSpaceAsync(bool optimize, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
            GameSpaceScan scan = await GameSpaceOptimizer.ScanAsync(disk, progress, token).ConfigureAwait(false);
            if (optimize)
            {
                foreach (GameSpaceDuplicate duplicate in scan.Duplicates)
                {
                    progress.Report(duplicate.Target);
                    await GameRefsBlockClone.DeduplicateAsync(duplicate.Source, duplicate.Target, duplicate.Hash, token).ConfigureAwait(false);
                }

                scan = await GameSpaceOptimizer.ScanAsync(disk, progress, token).ConfigureAwait(false);
            }

            return scan;
        }
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

    public async Task<string> GetOriginalForDisableAsync(CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            token.ThrowIfCancellationRequested();
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(launchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
            string original = GameVersionLibrary.Read(disk).OriginalExecutable;
            if (!File.Exists(original)) { throw new IOException("原安装已不存在，请恢复原安装后再关闭共存，避免在版本库内进行原地转换。"); }
            return original;
        }
    }

    private static bool Matches(GameVersionEntry entry, LaunchScheme scheme) => entry.IsOversea == scheme.IsOversea && entry.Channel == (int)scheme.Channel && entry.SubChannel == (int)scheme.SubChannel;
}
