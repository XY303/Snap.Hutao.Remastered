// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Service.Game.Package.Advanced.Model;
using Snap.Hutao.Remastered.Web.Hoyolab.Takumi.Downloader.Proto;
using System.Diagnostics;
using System.IO;
using Snap.Hutao.Remastered.Service.Game.Coexistence;

namespace Snap.Hutao.Remastered.Service.Game.Package.Advanced.PackageOperation;

public abstract class GamePackageOperation : IGamePackageOperation
{
    public abstract ValueTask ExecuteAsync(GamePackageServiceContext context);

    protected static void InitializeDuplicatedChunkNames(GamePackageServiceContext context, IEnumerable<AssetChunk> chunks)
    {
        Debug.Assert(context.DuplicatedChunkNames.IsEmpty);
        IEnumerable<string> names = chunks
            .GroupBy(chunk => chunk.ChunkName)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key)
            .Distinct();

        foreach (string name in names)
        {
            context.DuplicatedChunkNames.TryAdd(name, default);
        }
    }

    protected static async ValueTask PrivateVerifyAndRepairAsync(GamePackageServiceContext context, SophonDecodedBuild build, long totalBytes, int totalBlockCount)
    {
        context.Progress.Report(new GamePackageOperationReport.Reset(SH.ServiceGamePackageAdvancedVerifyingIntegrity, 0, totalBlockCount, totalBytes));
        GamePackageIntegrityInfo info = await context.Operation.Asset.VerifyGamePackageIntegrityAsync(context, build).ConfigureAwait(false);

        if (info.NoConflict)
        {
            context.Progress.Report(new GamePackageOperationReport.Finish(context.Operation.Kind));
            return;
        }

        (int conflictedBlocks, long conflictedBytes) = info.GetConflictedBlockCountAndByteCount(context.Operation.GameChannelSDK);
        if (GameCoexistenceVirtualDisk.Find(context.Operation.GameFileSystem.GameFilePath) is not null
            && GameCoexistenceVirtualDisk.GetEffectiveAvailableBytes(context.Operation.EffectiveGameDirectory) < conflictedBytes + 1024L * 1024 * 1024)
        {
            throw new IOException("虚拟磁盘或宿主磁盘空间不足，无法完成独立资源修复。请释放空间后重试。");
        }

        context.Progress.Report(new GamePackageOperationReport.Reset(SH.ServiceGamePackageAdvancedRepairing, conflictedBlocks, conflictedBytes));

        await context.Operation.Asset.RepairGamePackageAsync(context, info).ConfigureAwait(false);

        if (Directory.Exists(context.Operation.EffectiveChunksDirectory))
        {
            Directory.Delete(context.Operation.EffectiveChunksDirectory, true);
        }

        context.Progress.Report(new GamePackageOperationReport.Finish(context.Operation.Kind, context.Operation.Kind is GamePackageOperationKind.Verify));
    }
}
