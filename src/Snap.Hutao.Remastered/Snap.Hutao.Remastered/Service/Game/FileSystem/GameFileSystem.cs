// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Service.Game.Package.Advanced;
using Snap.Hutao.Remastered.Service.Game.Coexistence;

namespace Snap.Hutao.Remastered.Service.Game.FileSystem;

public sealed partial class GameFileSystem : IGameFileSystem
{
    private readonly AsyncReaderWriterLock.Releaser releaser;

    private GameFileSystem(string gameFilePath, AsyncReaderWriterLock.Releaser releaser)
    {
        GameFilePath = gameFilePath;
        this.releaser = releaser;
    }

    public string GameFilePath { get; }

    [field: MaybeNull]
    public GameAudioInstallation Audio { get => field ??= new(this.GameDirectory); }

    public bool IsDisposed { get; private set; }

    public static IGameFileSystem Create(string gameFilePath, AsyncReaderWriterLock.Releaser releaser)
    {
        try
        {
            GameCoexistenceVirtualDisk.AssertMounted(gameFilePath);
            if (GameVersionLibrary.FindEntry(gameFilePath) is { Pending: true })
            {
                throw new System.IO.IOException("共存版本创建未完成，请选择完整版本。未完成目录可在关闭胡桃后手动清理。");
            }
        }
        catch
        {
            releaser.Dispose();
            throw;
        }

        return new GameFileSystem(gameFilePath, releaser);
    }

    public static IGameFileSystem CreateForPackageOperation(string gameFilePath, GameAudioInstallation? gameAudioSystem = default)
    {
        GameCoexistenceVirtualDisk.AssertMounted(gameFilePath);
        return new PackageOperationGameFileSystem(gameFilePath, gameAudioSystem);
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        releaser.Dispose();
        IsDisposed = true;
    }
}
