// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.UI.Xaml.Controls;
using Snap.Hutao.Remastered.Core.Property;
using Snap.Hutao.Remastered.Factory.ContentDialog;
using Snap.Hutao.Remastered.Service.Game;
using Snap.Hutao.Remastered.Service.Game.FileSystem;
using Snap.Hutao.Remastered.Service.Game.Coexistence;
using Snap.Hutao.Remastered.Service.Game.Configuration;
using Snap.Hutao.Remastered.Service.Game.Scheme;
using Snap.Hutao.Remastered.Service.Notification;
using System.IO;

namespace Snap.Hutao.Remastered.ViewModel.Game;

public sealed partial class LaunchGameViewModel
{
    private CancellationTokenSource? coexistenceCancellation;

    public bool IsCoexistenceBusy { get; private set => SetProperty(ref field, value); }
    public string CoexistenceStatus { get; private set => SetProperty(ref field, value); } = string.Empty;
    public string CoexistenceSpace { get; private set => SetProperty(ref field, value); } = "占用：未读取；估算优化比率：待扫描";
    public IReadOnlyList<GameVersionEntry> CoexistenceVersions { get; private set => SetProperty(ref field, value); } = [];
    public IReadOnlyList<GameVersionEntry> CoexistenceDeletableVersions { get; private set => SetProperty(ref field, value); } = [];
    public GameVersionEntry? SelectedCoexistenceDeletionVersion { get; set => SetProperty(ref field, value); }
    public GameVersionEntry? SelectedCoexistenceVersion { get; set => SetProperty(ref field, value); }

    public IReadOnlyObservableProperty<string> ConvertSchemeHeader { get => field ??= Property.Observe(LaunchOptions.CoexistenceEnabled, static enabled => enabled ? SH.GameCoexistenceAddServerHeader : SH.ViewPageLaunchGameSwitchSchemeHeader); }
    public IReadOnlyObservableProperty<string> ConvertSchemeDescription { get => field ??= Property.Observe(LaunchOptions.CoexistenceEnabled, static enabled => enabled ? SH.GameCoexistenceAddServerDescription : SH.ViewPageLaunchGameSwitchSchemeDescription); }
    public IReadOnlyObservableProperty<string> ConvertSchemeAction { get => field ??= Property.Observe(LaunchOptions.CoexistenceEnabled, static enabled => enabled ? SH.GameCoexistenceAddServerAction : SH.ViewPageLaunchGameSwitchSchemeAction); }

    internal async ValueTask RefreshCoexistenceStatusAsync(bool refreshSpace = true)
    {
        if (IsCoexistenceBusy) { return; }
        try
        {
            await taskContext.SwitchToBackgroundAsync();
            GameVirtualDiskInfo? disk = string.IsNullOrEmpty(LaunchOptions.CoexistenceContainer.Value) ? null : GameCoexistenceVirtualDisk.ReadDescriptor(LaunchOptions.CoexistenceContainer.Value);
            if (disk is null)
            {
                await taskContext.SwitchToMainThreadAsync();
                CoexistenceStatus = "共存已关闭；开启时请选择虚拟磁盘存放位置。";
                return;
            }

            GameVersionLibrary library = GameVersionLibrary.Read(disk);
            string usage = $"VHDX 宿主占用：{GameRefsBlockClone.GetFileAllocatedBytes(disk.ImagePath) / (1024D * 1024 * 1024):F2} GiB；估算优化比率：待扫描";
            await taskContext.SwitchToMainThreadAsync();
            CoexistenceVersions = library.GetSelectableEntries(disk, LaunchOptions.GamePathEntry.Value?.Path);
            CoexistenceDeletableVersions = library.Entries.ToList();
            SelectedCoexistenceDeletionVersion = null;
            SelectedCoexistenceVersion = CoexistenceVersions.FirstOrDefault(e => e.GetExecutable(disk).Equals(LaunchOptions.GamePathEntry.Value?.Path, StringComparison.OrdinalIgnoreCase));
            CoexistenceStatus = string.Empty;
            if (refreshSpace) { CoexistenceSpace = usage; }
        }
        catch (Exception ex)
        {
            messenger.Send(InfoBarMessage.Error(SH.GameCoexistenceFailed, ex.Message));
        }
    }

    public async Task SetCoexistenceEnabledAsync(bool enabled)
    {
        if (IsCoexistenceBusy || LaunchOptions.CoexistenceEnabled.Value == enabled) { return; }
        await RunLibraryOperationAsync(async (progress, token) =>
        {
            GameServerCoexistenceService service = serviceProvider.GetRequiredService<GameServerCoexistenceService>();
            if (!enabled)
            {
                string original = await service.GetOriginalForDisableAsync(token).ConfigureAwait(false);
                await taskContext.SwitchToMainThreadAsync();
                LaunchOptions.PerformGamePathEntrySynchronization(original);
                LaunchOptions.CoexistenceEnabled.Value = false;
                return;
            }

            string executable = LaunchOptions.GamePathEntry.Value?.Path ?? throw new IOException(SH.ViewModelLaunchGameGamePathNullOrEmpty);
            IContentDialogFactory dialogs = serviceProvider.GetRequiredService<IContentDialogFactory>();
            if (await dialogs.CreateForConfirmCancelAsync(SH.GameCoexistenceRiskTitle, SH.GameCoexistenceRiskDescription).ConfigureAwait(false) != ContentDialogResult.Primary) { return; }
            await taskContext.SwitchToMainThreadAsync();
            if (!fileSystemPickerInteraction.PickFolder("选择虚拟硬盘存放位置（将在其中创建独立目录）").TryGetValue(out string? folder)) { return; }
            string saved = LaunchOptions.CoexistenceContainer.Value;
            bool reuse = !string.IsNullOrEmpty(saved) && Path.GetFullPath(folder!).Equals(Path.GetDirectoryName(saved), StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(saved, "installations.json"));
            string container = reuse ? saved : Path.Combine(folder!, "HutaoGame-" + Guid.NewGuid().ToString("N")[..8] + ".hutao-vdisk");
            if (await dialogs.CreateForConfirmCancelAsync(SH.GameCoexistenceSecondConfirmationTitle,
                $"源安装：{executable}\n虚拟磁盘：{container}\n{(reuse ? "将复用已有版本库" : "将完整导入当前安装")}；以后切换优先复用已有区服，仅缺少时创建独立版本。需要管理员权限及足够宿主空间。ReFS、更新器与反作弊兼容性未经保证，可能产生无法预见的错误。确认继续？").ConfigureAwait(false) != ContentDialogResult.Primary) { return; }
            string selected = reuse
                ? await service.ReopenLibraryAsync(container, token).ConfigureAwait(false)
                : await service.EnableLibraryAsync(executable, container, progress, token).ConfigureAwait(false);
            await taskContext.SwitchToMainThreadAsync();
            LaunchOptions.CoexistenceContainer.Value = container;
            LaunchOptions.PerformGamePathEntrySynchronization(selected);
            LaunchOptions.CoexistenceEnabled.Value = true;
            CoexistenceSpace = "初次导入完成；占用和估算优化比率可通过扫描刷新。";
        });
    }

    [Command("ScanCoexistenceSpaceCommand")]
    private Task ScanCoexistenceSpaceAsync() => RunSpaceOperationAsync(false);

    [Command("OptimizeCoexistenceSpaceCommand")]
    private Task OptimizeCoexistenceSpaceAsync() => RunSpaceOperationAsync(true);

    private Task RunSpaceOperationAsync(bool optimize) => RunLibraryOperationAsync(async (progress, token) =>
    {
        GameSpaceScan scan = await serviceProvider.GetRequiredService<GameServerCoexistenceService>().ScanSpaceAsync(optimize, progress, token).ConfigureAwait(false);
        await taskContext.SwitchToMainThreadAsync();
        CoexistenceSpace = scan.Description;
    });

    [Command("SelectCoexistenceVersionCommand")]
    private Task SelectCoexistenceVersionAsync() => RunLibraryOperationAsync(async (progress, token) =>
    {
        if (!LaunchOptions.CoexistenceEnabled.Value || SelectedCoexistenceVersion is not { } entry) { return; }
        if (await GameLifeCycle.IsGameRunningAsync(taskContext).ConfigureAwait(false)) { throw new IOException(SH.ServiceGameLaunchExecutionGameIsRunning); }
        GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(LaunchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
        await GameCoexistenceVirtualDisk.EnsureMountedAsync(disk, token).ConfigureAwait(false);
        string selected = entry.GetExecutable(disk);
        GameInstallationInfo installation = GameServerCoexistenceService.InspectInstallation(selected);
        // Finish older path refreshes before updating both the executable and target scheme.
        using (await EnterCriticalSectionAsync().ConfigureAwait(false))
        {
            await taskContext.SwitchToMainThreadAsync();
            LaunchOptions.PerformGamePathEntrySynchronization(selected);
            await TargetSchemeFilteredGameAccountsView.SetAsync(KnownLaunchSchemes.Values.Single(scheme => scheme.Equals(installation.Channel))).ConfigureAwait(false);
        }
    });

    [Command("DeleteCoexistenceVersionCommand")]
    private Task DeleteCoexistenceVersionAsync() => RunLibraryOperationAsync(async (progress, token) =>
    {
        if (SelectedCoexistenceDeletionVersion is not { } entry) { return; }
        GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.ReadDescriptor(LaunchOptions.CoexistenceContainer.Value) ?? throw new IOException("找不到虚拟磁盘。");
        string path = Path.GetDirectoryName(entry.GetExecutable(disk))!;
        IContentDialogFactory dialogs = serviceProvider.GetRequiredService<IContentDialogFactory>();
        if (await dialogs.CreateForConfirmCancelAsync("删除虚拟盘版本", $"版本：{entry.DisplayName}\n目录：{path}\n将永久删除此版本及其截图、配置等全部文件。其他版本保留；宿主 VHDX 不会自动缩小。确认删除？").ConfigureAwait(false) != ContentDialogResult.Primary) { return; }
        await serviceProvider.GetRequiredService<GameServerCoexistenceService>().DeleteVersionAsync(entry.Id, progress, token).ConfigureAwait(false);
        await taskContext.SwitchToMainThreadAsync();
        CoexistenceSpace = "版本已删除；占用和优化比率请重新扫描。";
    });

    [Command("CancelCoexistenceCommand")]
    private void CancelCoexistence()
    {
        try { coexistenceCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunLibraryOperationAsync(Func<IProgress<string>, CancellationToken, Task> operation)
    {
        await taskContext.SwitchToMainThreadAsync();
        if (IsCoexistenceBusy) { return; }
        IsCoexistenceBusy = true;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        coexistenceCancellation = cancellation;
        try
        {
            Progress<string> progress = new(value => CoexistenceStatus = SH.FormatGameCoexistenceProcessing(value));
            await operation(progress, cancellation.Token).ConfigureAwait(false);
            await taskContext.SwitchToMainThreadAsync();
            CoexistenceStatus = "操作完成。";
        }
        catch (OperationCanceledException) { messenger.Send(InfoBarMessage.Warning(SH.GameCoexistenceInterrupted)); }
        catch (Exception ex) { messenger.Send(InfoBarMessage.Error(SH.GameCoexistenceFailed, ex.Message)); }
        finally
        {
            await taskContext.SwitchToMainThreadAsync();
            coexistenceCancellation = null;
            IsCoexistenceBusy = false;
            await RefreshCoexistenceStatusAsync(refreshSpace: false);
        }
    }
}
