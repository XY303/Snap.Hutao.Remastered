// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Core.ExceptionService;
using Snap.Hutao.Remastered.Model.Intrinsic;
using Snap.Hutao.Remastered.Service.Game.Configuration;
using System.IO;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

[Service(ServiceLifetime.Singleton)]
public sealed partial class GameServerCoexistenceService
{
    private readonly LaunchOptions launchOptions;
    private readonly ITaskContext taskContext;

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

    public async ValueTask<(int Files, long Bytes)> ShareAsync(string firstExecutable, string secondExecutable, IProgress<string> progress, CancellationToken token)
    {
        await taskContext.SwitchToBackgroundAsync();
        using (AcquireExclusiveAccess())
        {
            await EnsureGameStoppedAsync().ConfigureAwait(false);
            GameInstallationInfo first = InspectInstallation(firstExecutable);
            GameInstallationInfo second = InspectInstallation(secondExecutable);
            if (first.IsOversea == second.IsOversea || first.InstalledVersion != second.InstalledVersion)
            {
                throw HutaoException.InvalidOperation(SH.GameCoexistenceInstallationInvalid);
            }

            return await GameServerCoexistenceStorage.ShareAsync(
                first.IsOversea ? secondExecutable : firstExecutable,
                first.IsOversea ? firstExecutable : secondExecutable,
                progress,
                token).ConfigureAwait(false);
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
}
