// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.UI.Xaml.Controls;
using Snap.Hutao.Remastered.Factory.ContentDialog;
using Snap.Hutao.Remastered.Service.Game.Coexistence;
using Snap.Hutao.Remastered.Service.Notification;
using Snap.Hutao.Remastered.Service.Game.FileSystem;

namespace Snap.Hutao.Remastered.ViewModel.Game;

public sealed partial class LaunchGameViewModel
{
    private CancellationTokenSource? coexistenceCancellation;

    public bool IsCoexistenceBusy { get; private set => SetProperty(ref field, value); }

    public string CoexistenceStatus { get; private set => SetProperty(ref field, value); } = string.Empty;

    internal async ValueTask RefreshCoexistenceStatusAsync()
    {
        if (IsCoexistenceBusy)
        {
            return;
        }

        string status = SH.GameCoexistenceNotAssociated;
        try
        {
            string? executable = LaunchOptions.GamePathEntry.Value?.Path;
            if (executable is not null)
            {
                await taskContext.SwitchToBackgroundAsync();
                await GameCoexistenceVirtualDisk.EnsureMountedAsync(executable).ConfigureAwait(false);
                if (GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(executable)) is { } state)
                {
                    status = state.CreationPending ? SH.GameCoexistenceCreationPending : SH.FormatGameCoexistenceAssociated(state.ChineseExecutable, state.OverseaExecutable);
                }
            }
        }
        catch (Exception ex)
        {
            status = SH.GameCoexistenceFailed;
            messenger.Send(InfoBarMessage.Error(status, ex.Message));
        }

        await taskContext.SwitchToMainThreadAsync();
        if (!IsCoexistenceBusy)
        {
            CoexistenceStatus = status;
        }
    }

    [Command("CreateCoexistingServerCommand")]
    private Task CreateCoexistingServerAsync()
    {
        return RunCoexistenceOperationAsync(create: true);
    }

    [Command("UnpairGameServersCommand")]
    private Task UnpairGameServersAsync()
    {
        return RunCoexistenceOperationAsync(create: false);
    }

    [Command("CancelCoexistenceCommand")]
    private void CancelCoexistence()
    {
        try
        {
            coexistenceCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may have disposed the operation before the UI processes this click.
        }
    }

    [Command("MountCoexistenceDiskCommand")]
    private async Task MountCoexistenceDiskAsync()
    {
        if (IsCoexistenceBusy || LaunchOptions.GamePathEntry.Value?.Path is not { } executable)
        {
            return;
        }

        IsCoexistenceBusy = true;
        try
        {
            await taskContext.SwitchToBackgroundAsync();
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.Find(executable)
                ?? GameCoexistenceVirtualDisk.ReadDescriptor(GameCoexistenceVirtualDisk.GetContainer(executable))
                ?? throw new System.IO.IOException("没有找到当前安装对应的虚拟磁盘描述文件。请先创建虚拟磁盘共存。");
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, CancellationToken).ConfigureAwait(false);
            string selected = System.IO.Path.GetFileName(executable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase)
                ? disk.OverseaExecutable : disk.ChineseExecutable;
            await taskContext.SwitchToMainThreadAsync();
            LaunchOptions.PerformGamePathEntrySynchronization(selected);
        }
        catch (Exception ex)
        {
            messenger.Send(InfoBarMessage.Error(SH.GameCoexistenceFailed, ex.Message));
        }
        finally
        {
            await taskContext.SwitchToMainThreadAsync();
            IsCoexistenceBusy = false;
        }

        await RefreshCoexistenceStatusAsync();
    }

    private async Task RunCoexistenceOperationAsync(bool create)
    {
        await taskContext.SwitchToMainThreadAsync();
        if (IsCoexistenceBusy)
        {
            return;
        }

        IsCoexistenceBusy = true;
        CoexistenceStatus = string.Empty;
        try
        {
            string? executable = LaunchOptions.GamePathEntry.Value?.Path;
            if (string.IsNullOrEmpty(executable))
            {
                messenger.Send(InfoBarMessage.Warning(SH.ViewModelLaunchGameGamePathNullOrEmpty));
                return;
            }

            IContentDialogFactory dialogs = serviceProvider.GetRequiredService<IContentDialogFactory>();
            GameServerCoexistenceService coexistence = serviceProvider.GetRequiredService<GameServerCoexistenceService>();
            if (create)
            {
                await taskContext.SwitchToBackgroundAsync();
                GameVirtualDiskInfo layout = GameCoexistenceVirtualDisk.GetLayout(executable);
                string otherExecutable = System.IO.Path.GetFileName(executable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase) ? layout.ChineseExecutable : layout.OverseaExecutable;
                GameServerCoexistenceService.InspectInstallation(executable);
                if (await dialogs.CreateForConfirmCancelAsync(SH.GameCoexistenceRiskTitle, SH.GameCoexistenceRiskDescription).ConfigureAwait(false) is not ContentDialogResult.Primary)
                {
                    return;
                }

                // A separate, default-cancel confirmation is required for every association.
                if (await dialogs.CreateForConfirmCancelAsync(
                    SH.GameCoexistenceSecondConfirmationTitle,
                    SH.FormatGameCoexistenceSecondConfirmation(executable, otherExecutable)).ConfigureAwait(false) is not ContentDialogResult.Primary)
                {
                    return;
                }
            }
            else if (await dialogs.CreateForConfirmCancelAsync(SH.GameCoexistenceUnpairTitle, SH.GameCoexistenceUnpairDescription).ConfigureAwait(false) is not ContentDialogResult.Primary)
            {
                return;
            }

            await taskContext.SwitchToMainThreadAsync();
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            coexistenceCancellation = cancellation;
            Progress<string> progress = new(path => CoexistenceStatus = SH.FormatGameCoexistenceProcessing(path));
            CoexistenceStatus = SH.GameCoexistencePreparing;
            string completion;
            if (create)
            {
                (int files, long bytes) = await coexistence.CreateAsync(executable, progress, cancellation.Token).ConfigureAwait(false);
                GameVirtualDiskInfo layout = GameCoexistenceVirtualDisk.GetLayout(executable);
                string virtualSource = System.IO.Path.GetFileName(executable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase) ? layout.OverseaExecutable : layout.ChineseExecutable;
                await taskContext.SwitchToMainThreadAsync();
                LaunchOptions.PerformGamePathEntrySynchronization(virtualSource);
                completion = SH.FormatGameCoexistenceCompleted(files, (bytes / (1024D * 1024D * 1024D)).ToString("F2"));
            }
            else
            {
                await coexistence.UnpairAsync(executable, progress, cancellation.Token).ConfigureAwait(false);
                completion = SH.GameCoexistenceUnpaired;
            }

            await taskContext.SwitchToMainThreadAsync();
            CoexistenceStatus = completion;
            messenger.Send(InfoBarMessage.Success(completion));
        }
        catch (OperationCanceledException)
        {
            await taskContext.SwitchToMainThreadAsync();
            CoexistenceStatus = SH.GameCoexistenceInterrupted;
            messenger.Send(InfoBarMessage.Warning(CoexistenceStatus));
        }
        catch (Exception ex)
        {
            await taskContext.SwitchToMainThreadAsync();
            CoexistenceStatus = SH.GameCoexistenceFailed;
            messenger.Send(InfoBarMessage.Error(SH.GameCoexistenceFailed, ex.Message));
        }
        finally
        {
            await taskContext.SwitchToMainThreadAsync();
            coexistenceCancellation = null;
            IsCoexistenceBusy = false;
        }
    }
}
