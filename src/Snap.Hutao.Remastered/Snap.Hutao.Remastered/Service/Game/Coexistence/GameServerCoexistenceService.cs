// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Core.ExceptionService;
using Snap.Hutao.Remastered.Model.Entity.Primitive;
using Snap.Hutao.Remastered.Model.Intrinsic;
using Snap.Hutao.Remastered.Service.Game.Configuration;
using Snap.Hutao.Remastered.Service.Game.FileSystem;
using Snap.Hutao.Remastered.Service.Game.Package;
using Snap.Hutao.Remastered.Service.Game.Package.Advanced;
using Snap.Hutao.Remastered.Service.Game.Scheme;
using Snap.Hutao.Remastered.Web.Hoyolab.HoyoPlay.Connect.Branch;
using System.IO;
using System.Net.Http;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

[Service(ServiceLifetime.Singleton)]
public sealed partial class GameServerCoexistenceService
{
    private readonly LaunchOptions launchOptions;
    private readonly ITaskContext taskContext;
    private readonly IServiceProvider serviceProvider;

    [GeneratedConstructor]
    public partial GameServerCoexistenceService(IServiceProvider serviceProvider);

    public static GameInstallationInfo InspectInstallation(string executable)
    {
        string root = GameServerCoexistenceStorage.GetRoot(executable);
        bool oversea = Path.GetFileName(executable).Equals(GameConstants.GenshinImpactFileName, StringComparison.OrdinalIgnoreCase);
        string data = oversea ? GameConstants.GenshinImpactData : GameConstants.YuanShenData;
        GameInstallationInfo installation = GameInstallationReader.Read(executable, Path.Combine(root, GameConstants.ConfigFileName), Path.Combine(root, data, "Persistent", "ScriptVersion"), oversea);
        if (!File.Exists(executable) || installation.HasVersionProblem || installation.Channel.Channel is not ChannelType.Official
            || installation.Channel.SubChannel is not SubChannelType.Default || !Directory.Exists(Path.Combine(root, data, "StreamingAssets")))
        {
            throw HutaoException.InvalidOperation(SH.GameCoexistenceInstallationInvalid);
        }

        return installation;
    }

    public async ValueTask<(int Files, long Bytes)> CreateAsync(string executable, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameInstallationInfo installation = InspectInstallation(executable);
            string root = GameServerCoexistenceStorage.GetRoot(executable);
            string targetExecutable = GameServerCoexistenceStorage.GetGeneratedExecutable(executable);
            string targetRoot = GameServerCoexistenceStorage.GetRoot(targetExecutable);
            if (GameServerCoexistenceStorage.ReadState(root) is not null || Directory.Exists(targetRoot))
            {
                throw HutaoException.InvalidOperation(SH.GameCoexistenceDestinationExists);
            }

            LaunchScheme current = KnownLaunchSchemes.GetDefaultForAccountType(installation.IsOversea ? SchemeType.Oversea : SchemeType.ChineseOfficial)!;
            LaunchScheme target = KnownLaunchSchemes.GetDefaultForAccountType(installation.IsOversea ? SchemeType.ChineseOfficial : SchemeType.Oversea)!;
            using IServiceScope scope = serviceProvider.CreateScope();
            IHoyoPlayService hoyoPlay = scope.ServiceProvider.GetRequiredService<IHoyoPlayService>();
            if (await hoyoPlay.TryGetBranchesAsync(current).ConfigureAwait(false) is not (true, { } currentBranches)
                || await hoyoPlay.TryGetBranchesAsync(target).ConfigureAwait(false) is not (true, { } targetBranches)
                || await hoyoPlay.TryGetChannelSDKsAsync(target).ConfigureAwait(false) is not (true, { } sdks)
                || await hoyoPlay.TryGetDeprecatedFileConfigurationsAsync(target).ConfigureAwait(false) is not (true, { } deprecated))
            {
                throw HutaoException.InvalidOperation(SH.ViewModelLaunchGameEnsureGameResourceFail);
            }

            BranchWrapper currentBranch = currentBranches.GetMainBranch(current.GameId);
            BranchWrapper targetBranch = targetBranches.GetMainBranch(target.GameId);
            if (!Version.TryParse(currentBranch.Tag, out Version? currentVersion) || !Version.TryParse(targetBranch.Tag, out Version? targetVersion)
                || currentVersion != installation.InstalledVersion || targetVersion != currentVersion)
            {
                throw HutaoException.InvalidOperation(SH.GameCoexistenceInstallationInvalid);
            }

            using HttpClient httpClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(GamePackageService.HttpClientName);
            using IGameFileSystem sourceFileSystem = GameFileSystem.CreateForPackageOperation(executable);
            using IGameFileSystem targetFileSystem = GameFileSystem.CreateForPackageOperation(targetExecutable);
            IPackageConverter converter = scope.ServiceProvider.GetRequiredService<IPackageConverter>();
            PackageConverterContext context = new(current, target, sourceFileSystem)
            {
                HttpClient = httpClient,
                CurrentBranch = currentBranch,
                TargetBranch = targetBranch,
                Progress = new ConversionProgress(progress),
            };
            using CancellationTokenRegistration registration = token.Register(httpClient.CancelPendingRequests);
            return await converter.CreateCoexistingResourceAsync(context, targetExecutable, async () =>
            {
                token.ThrowIfCancellationRequested();
                // Per-server configuration and runtime metadata never share writable files.
                string config = targetFileSystem.GameConfigurationFilePath;
                File.WriteAllText(config, $"[General]\r\nchannel=1\r\nsub_channel=0\r\ngame_version={targetVersion}\r\n");
                string script = targetFileSystem.ScriptVersionFilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(script)!);
                File.WriteAllText(script, targetVersion.ToString());
                PackageConverterDeprecationContext deprecation = new(httpClient, targetFileSystem, sdks.GameChannelSDKs.SingleOrDefault(), deprecated.DeprecatedFileConfigurations.SingleOrDefault(), preserveSharedFiles: true);
                await converter.EnsureDeprecatedFilesAndSDKAsync(deprecation).ConfigureAwait(false);
                InspectInstallation(targetExecutable);
            }, progress, token).ConfigureAwait(false);
        }
    }

    public async ValueTask UnpairAsync(string executable, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(executable), progress, token).ConfigureAwait(false);
        }
    }

    private AsyncReaderWriterLock.Releaser AcquireExclusiveAccess()
    {
        if (!launchOptions.GamePathLock.TryWriterLock(nameof(GameServerCoexistenceService), out AsyncReaderWriterLock.Releaser releaser))
        {
            throw HutaoException.InvalidOperation(SH.GameCoexistenceOperationBusy);
        }

        return releaser;
    }

    private async ValueTask EnsureGameStoppedAsync()
    {
        if (await GameLifeCycle.IsGameRunningAsync(taskContext).ConfigureAwait(false))
        {
            throw HutaoException.InvalidOperation(SH.ServiceGameLaunchExecutionGameIsRunning);
        }
    }

    private sealed class ConversionProgress(IProgress<string> progress) : IProgress<PackageConvertStatus>
    {
        public void Report(PackageConvertStatus value)
        {
            progress.Report(string.IsNullOrEmpty(value.Description) ? value.Name : $"{value.Name} {value.Description}");
        }
    }
}
