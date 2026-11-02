// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.UI.Xaml.Controls;
using Snap.Hutao.Remastered.Core.IO;
using Snap.Hutao.Remastered.Factory.ContentDialog;
using Snap.Hutao.Remastered.Factory.Picker;
using Snap.Hutao.Remastered.Service.Game.Coexistence;
using Snap.Hutao.Remastered.Service.Notification;

namespace Snap.Hutao.Remastered.ViewModel.Game;

public sealed partial class LaunchGameViewModel
{
    private CancellationTokenSource? coexistenceCancellation;

    public bool IsCoexistenceBusy { get; private set => SetProperty(ref field, value); }

    public string CoexistenceStatus { get; private set => SetProperty(ref field, value); } = string.Empty;

    private async ValueTask RefreshCoexistenceStatusAsync()
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
                if (GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(executable)) is { } state)
                {
                    status = SH.FormatGameCoexistenceAssociated(state.ChineseExecutable, state.OverseaExecutable);
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

    [Command("PairGameServersCommand")]
    private Task PairGameServersAsync()
    {
        return RunCoexistenceOperationAsync(pair: true);
    }

    [Command("UnpairGameServersCommand")]
    private Task UnpairGameServersAsync()
    {
        return RunCoexistenceOperationAsync(pair: false);
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

    private async Task RunCoexistenceOperationAsync(bool pair)
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
            string? otherExecutable = null;
            if (pair)
            {
                await taskContext.SwitchToBackgroundAsync();
                (bool picked, ValueFile file) = fileSystemPickerInteraction.PickFile(SH.GameCoexistenceSelectInstallation, "Game", "*.exe");
                if (!picked)
                {
                    return;
                }

                otherExecutable = file;
                GameServerCoexistenceService.InspectInstallation(executable);
                GameServerCoexistenceService.InspectInstallation(otherExecutable);
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
            if (pair)
            {
                (int files, long bytes) = await coexistence.ShareAsync(executable, otherExecutable!, progress, cancellation.Token).ConfigureAwait(false);
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
