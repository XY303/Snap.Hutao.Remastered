// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Core.IO;
using Snap.Hutao.Remastered.Factory.Picker;
using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.IO;

namespace Snap.Hutao.Remastered.Service.Game.Locator;

[Service(ServiceLifetime.Transient, typeof(IGameLocator), Key = GameLocationSourceKind.Manual)]
public sealed partial class ManualGameLocator : IGameLocator
{
    private readonly IFileSystemPickerInteraction fileSystemPickerInteraction;
    private readonly GameServerCoexistenceService coexistenceService;

    [GeneratedConstructor]
    public partial ManualGameLocator(IServiceProvider serviceProvider);

    public async ValueTask<ValueResult<bool, string>> LocateSingleGamePathAsync()
    {
        (bool isPickerOk, ValueFile file) = fileSystemPickerInteraction.PickFile(
            SH.ServiceGameLocatorFileOpenPickerCommitText,
            SH.ServiceGameLocatorPickerFilterText,
            $"{GameConstants.YuanShenFileName};{GameConstants.GenshinImpactFileName};*.vhdx");

        if (isPickerOk)
        {
            string fileName = System.IO.Path.GetFileName(file);
            if (fileName.ToUpperInvariant() is GameConstants.YuanShenFileNameUpper or GameConstants.GenshinImpactFileNameUpper)
            {
                return new(true, file);
            }
            if (Path.GetExtension(file).Equals(".vhdx", StringComparison.OrdinalIgnoreCase))
            {
                GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.FromImagePath(file);
                string selected = await coexistenceService.ReopenLibraryAsync(disk.Container, XamlApplicationLifetime.ShutdownToken).ConfigureAwait(false);
                await coexistenceService.SelectLibraryAsync(disk.Container, selected).ConfigureAwait(false);
                return new(true, selected);
            }
        }

        return new(false, default!);
    }
}
