// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Service.Game.Coexistence;
using Snap.Hutao.Remastered.Service.Game.FileSystem;
using Snap.Hutao.Remastered.Service.Game.Package.Advanced.Model;
using Snap.Hutao.Remastered.Web.Hoyolab.Takumi.Downloader.Proto;
using System.IO;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Package;

public sealed partial class PackageConverter
{
    public async ValueTask<(int Files, long Bytes)> CreateCoexistingResourceAsync(
        PackageConverterContext context,
        string targetExecutable,
        Func<Task> finishInstallation,
        IProgress<string> progress,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(context.CurrentBranch);
        ArgumentNullException.ThrowIfNull(context.TargetBranch);
        token.ThrowIfCancellationRequested();
        context.ParallelOptions.CancellationToken = token;
        // The same official manifests and chunk reconstruction as server conversion,
        // but materialize a new view rather than moving/replacing the original client.
        SophonDecodedBuild current = await DecodeManifestsAsync(context, context.CurrentBranch, context.CurrentScheme, includeInstalledAudio: true).ConfigureAwait(false)
            ?? throw new InvalidDataException("Could not decode the current server manifest.");
        SophonDecodedBuild target = await DecodeManifestsAsync(context, context.TargetBranch, context.TargetScheme, includeInstalledAudio: true).ConfigureAwait(false)
            ?? throw new InvalidDataException("Could not decode the target server manifest.");
        if (current.Tag != context.CurrentBranch.Tag || target.Tag != context.TargetBranch.Tag || current.Tag != target.Tag)
        {
            throw new InvalidDataException("Server manifests must describe the same installed release.");
        }

        string sourceRoot = context.GameFileSystem.GameDirectory;
        string targetRoot = GameServerCoexistenceStorage.GetRoot(targetExecutable);
        // Do not validate/reuse local launcher inventories, even when both remote hashes
        // match. Excluding them from the source makes the converter download all target
        // chunks and also prevents the later common-file path from copying local metadata.
        foreach (SophonDecodedManifest manifest in current.Manifests)
        {
            foreach (AssetProperty inventory in manifest.Data.Assets.Where(asset => GameServerCoexistenceStorage.IsPackageInventory(asset.AssetName)).ToList())
            {
                manifest.Data.Assets.Remove(inventory);
            }
        }

        List<AssetProperty> currentAssets = current.Manifests.SelectMany(manifest => manifest.Data.Assets).ToList();
        List<AssetProperty> targetAssets = target.Manifests.SelectMany(manifest => manifest.Data.Assets).ToList();
        // Validate all remote paths before they can be used by the conversion cache.
        foreach (AssetProperty asset in currentAssets)
        {
            GameServerCoexistenceStorage.ResolveManifestPath(sourceRoot, asset.AssetName);
            GameServerCoexistenceStorage.ResolveManifestPath(context.ServerCacheBackupFolder, asset.AssetName);
        }

        foreach (AssetProperty asset in targetAssets)
        {
            GameServerCoexistenceStorage.ResolveManifestPath(targetRoot, asset.AssetName);
            GameServerCoexistenceStorage.ResolveManifestPath(context.ServerCacheTargetFolder, asset.AssetName);
            foreach (AssetChunk chunk in asset.AssetChunks)
            {
                if (chunk.ChunkName.Contains('/') || chunk.ChunkName.Contains('\\'))
                {
                    throw new InvalidDataException("Unsafe manifest chunk name.");
                }

                GameServerCoexistenceStorage.ResolveManifestPath(context.ServerCacheChunksFolder, chunk.ChunkName);
            }
        }

        Dictionary<string, AssetProperty> sourceByName = currentAssets.ToDictionary(asset => NormalizeAssetName(asset.AssetName), StringComparer.OrdinalIgnoreCase);
        List<PackageItemOperationForSophonChunks> operations = GetDiffOperations(current, target).ToList();
        operations.SortBy(operation => operation.Kind);
        // Diff reconstruction trusts old chunks; reject a corrupt local input first.
        foreach (PackageItemOperationForSophonChunks operation in operations.Where(operation => operation.Kind is PackageItemOperationKind.Replace))
        {
            token.ThrowIfCancellationRequested();
            AssetProperty asset = operation.OldAsset;
            string path = GameServerCoexistenceStorage.ResolveManifestPath(sourceRoot, asset.AssetName);
            progress.Report(asset.AssetName);
            using FileStream stream = File.OpenRead(path);
            if (stream.Length != asset.AssetSize || !Convert.ToHexString(await MD5.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(asset.AssetHashMd5, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Source resource is corrupt: {asset.AssetName}. Verify and repair before creating coexistence.");
            }
        }

        InitializeDuplicatedChunkNames(context, operations.SelectMany(operation => operation.DiffChunks.Select(chunk => chunk.AssetChunk)));
        using (token.Register(context.HttpClient.CancelPendingRequests))
        {
            await PrepareCacheFilesAsync(context, operations).ConfigureAwait(false);
        }

        List<GameCoexistenceAsset> assets = [];
        foreach (AssetProperty asset in targetAssets)
        {
            bool common = sourceByName.TryGetValue(NormalizeAssetName(asset.AssetName), out AssetProperty? old)
                && old.AssetSize == asset.AssetSize && old.AssetHashMd5.Equals(asset.AssetHashMd5, StringComparison.OrdinalIgnoreCase);
            string path = common
                ? GameServerCoexistenceStorage.ResolveManifestPath(sourceRoot, old!.AssetName)
                : GameServerCoexistenceStorage.ResolveManifestPath(context.ServerCacheTargetFolder, asset.AssetName);
            assets.Add(new(asset.AssetName, path, asset.AssetSize, asset.AssetHashMd5));
        }

        return await GameServerCoexistenceStorage.CreateClonedFromManifestAsync(context.GameFileSystem.GameFilePath, targetExecutable, assets, finishInstallation, progress, token).ConfigureAwait(false);
    }

    private static string NormalizeAssetName(string name)
    {
        string normalized = name.Replace('\\', '/');
        return normalized.StartsWith(GameConstants.YuanShenData + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(GameConstants.GenshinImpactData + "/", StringComparison.OrdinalIgnoreCase)
            ? "_Data/" + normalized[(normalized.IndexOf('/') + 1)..] : normalized;
    }
}
