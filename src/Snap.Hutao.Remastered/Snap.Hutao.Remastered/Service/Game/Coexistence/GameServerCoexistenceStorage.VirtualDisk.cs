// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public static partial class GameServerCoexistenceStorage
{
    public static IReadOnlyList<string> GetSourceFiles(string executable)
    {
        string root = GetRoot(executable);
        List<string> files = [];
        Visit(root);
        return files;

        void Visit(string directory)
        {
            EnsureOrdinaryPath(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Equals("chunks", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals("ScreenShot", StringComparison.OrdinalIgnoreCase)
                    || relative.Equals(StateFileName, StringComparison.OrdinalIgnoreCase)
                    || relative.Contains(".hutao-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnsureOrdinaryPath(path);
                if (Directory.Exists(path))
                {
                    Visit(path);
                }
                else
                {
                    files.Add(path);
                }
            }
        }
    }

    public static async Task CopyBaseToVirtualDiskAsync(string originalExecutable, GameVirtualDiskInfo disk, IReadOnlyList<string> files, IProgress<string>? progress, CancellationToken token)
    {
        string original = GetRoot(originalExecutable);
        bool oversea = Path.GetFileName(originalExecutable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase);
        string destination = GetRoot(oversea ? disk.OverseaExecutable : disk.ChineseExecutable);
        Directory.CreateDirectory(GetRoot(disk.ChineseExecutable));
        Directory.CreateDirectory(GetRoot(disk.OverseaExecutable));
        GameServerCoexistenceState state = new()
        {
            Format = 2,
            PairId = Guid.NewGuid(),
            ChineseExecutable = disk.ChineseExecutable,
            OverseaExecutable = disk.OverseaExecutable,
            VirtualDiskImage = disk.ImagePath,
            SharedFiles = [],
            CreationPending = true,
        };
        WriteState(GetRoot(disk.ChineseExecutable), state);
        WriteState(GetRoot(disk.OverseaExecutable), state);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(original, file);
            string target = ResolveManifestPath(destination, relative);
            progress?.Report(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream source = OpenStableRead(file);
            await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await source.CopyToAsync(output, token).ConfigureAwait(false);
            output.Flush(true);
        }
    }

    public static async Task<(int Files, long Bytes)> CreateClonedFromManifestAsync(
        string sourceExecutable,
        string targetExecutable,
        IReadOnlyList<GameCoexistenceAsset> assets,
        Func<Task> finishInstallation,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string sourceRoot = GetRoot(sourceExecutable);
            string targetRoot = GetRoot(targetExecutable);
            GameServerCoexistenceState state = ReadState(sourceRoot) ?? throw new InvalidDataException("Missing virtual disk creation journal.");
            GameServerCoexistenceState peer = ReadState(targetRoot) ?? throw new InvalidDataException("Missing target creation journal.");
            if (state.Format != 2 || !state.CreationPending || !peer.CreationPending || !SamePair(state, peer) || state.PairId != peer.PairId
                || sourceRoot.Equals(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Invalid virtual disk creation state.");
            }

            bool sourceOversea = Path.GetFileName(sourceExecutable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase);
            if (!targetExecutable.Equals(sourceOversea ? state.ChineseExecutable : state.OverseaExecutable, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The target must be the opposite server in this virtual disk.");
            }

            string sourceData = sourceOversea ? "GenshinImpact_Data/" : "YuanShen_Data/";
            string targetData = sourceOversea ? "YuanShen_Data/" : "GenshinImpact_Data/";
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (GameCoexistenceAsset asset in assets)
            {
                ResolveManifestPath(targetRoot, asset.Name);
                EnsureOrdinaryPath(asset.SourcePath);
                if (!names.Add(asset.Name.Replace('\\', '/')) || asset.Size < 0 || asset.Md5.Length != 32 || !asset.Md5.All(Uri.IsHexDigit)
                    || asset.Name.Equals(StateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Invalid or duplicate manifest asset.");
                }
            }

            if (!names.Contains(Path.GetFileName(targetExecutable)))
            {
                throw new InvalidDataException("The manifest has no target executable.");
            }

            long privateBytes = assets.Where(asset => !CanClone(asset)).Sum(asset => asset.Size);
            if (GameCoexistenceVirtualDisk.GetEffectiveAvailableBytes(targetRoot) < privateBytes + 1024L * 1024 * 1024)
            {
                throw new IOException("Insufficient virtual disk or host space for the other server.");
            }

            int files = 0;
            long bytes = 0;
            foreach (GameCoexistenceAsset asset in assets)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(asset.Name);
                token.ThrowIfCancellationRequested();
                string path = ResolveManifestPath(targetRoot, asset.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (FileStream source = OpenStableRead(asset.SourcePath))
                {
                    if (source.Length != asset.Size || !Convert.ToHexString(await MD5.HashDataAsync(source, token).ConfigureAwait(false)).Equals(asset.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Manifest checksum mismatch: {asset.Name}. Verify the original installation before retrying.");
                    }

                    if (!CanClone(asset))
                    {
                        source.Position = 0;
                        await using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                        await source.CopyToAsync(output, token).ConfigureAwait(false);
                        output.Flush(true);
                    }
                }

                if (CanClone(asset))
                {
                    long cloned = await GameRefsBlockClone.CloneAsync(asset.SourcePath, path, token).ConfigureAwait(false);
                    if (cloned > 0)
                    {
                        files++;
                        bytes = checked(bytes + cloned);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            await finishInstallation().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            WriteState(targetRoot, state with { CreationPending = false });
            WriteState(sourceRoot, state with { CreationPending = false });
            return (files, bytes);

            bool CanClone(GameCoexistenceAsset asset)
            {
                string name = asset.Name.Replace('\\', '/');
                return asset.Size >= 1024 * 1024 && name.StartsWith(targetData, StringComparison.OrdinalIgnoreCase)
                    && IsShareableRelativePath(name[targetData.Length..])
                    && Path.GetFullPath(asset.SourcePath).Equals(ResolveManifestPath(sourceRoot, sourceData + name[targetData.Length..]), StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void MarkVirtualInstallationRepaired(string root)
    {
        if (ReadState(root) is { Format: 2, CreationPending: true } state)
        {
            WriteState(root, state with { CreationPending = false });
        }
    }
}
