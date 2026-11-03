// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameCoexistenceAsset(string Name, string SourcePath, long Size, string Md5);

public static partial class GameServerCoexistenceStorage
{
    public static string GetGeneratedExecutable(string executable)
    {
        string root = GetRoot(executable);
        bool sourceOversea = Path.GetFileName(executable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(root + (sourceOversea ? ".hutao-cn" : ".hutao-os"), sourceOversea ? "YuanShen.exe" : "GenshinImpact.exe");
    }

    // Manifest paths are untrusted. Validate every component before even preparing downloads.
    public static string ResolveManifestPath(string root, string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
        {
            throw new InvalidDataException("Unsafe manifest asset path.");
        }

        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Manifest asset escapes the installation.");
        }

        EnsureOrdinaryPath(result);
        return result;
    }

    public static async Task ExtractSdkAsync(Stream stream, string root)
    {
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string name = entry.FullName.TrimEnd('/', '\\');
            string target = ResolveManifestPath(root, name);
            if (directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".hutao-sdk-" + Guid.NewGuid().ToString("N");
            try
            {
                using (Stream source = entry.Open())
                {
                    await using FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                    await source.CopyToAsync(destination).ConfigureAwait(false);
                    destination.Flush(true);
                }

                // Replace the name instead of writing through a shared NTFS file.
                File.Move(temporary, target, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }

    public static async Task<(int Files, long Bytes)> CreateFromManifestAsync(
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
            ValidateRoots(sourceRoot, targetRoot);
            if (ReadState(sourceRoot) is not null || Directory.Exists(targetRoot))
            {
                throw new IOException("Restore independent files first, and use an empty destination for manifest creation.");
            }

            bool sourceOversea = Path.GetFileName(sourceExecutable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase);
            string sourceDataName = sourceOversea ? "GenshinImpact_Data" : "YuanShen_Data";
            string targetDataName = sourceOversea ? "YuanShen_Data" : "GenshinImpact_Data";
            if (!Path.GetFileName(targetExecutable).Equals(sourceOversea ? "YuanShen.exe" : "GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The manifest must create the opposite server executable.");
            }

            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
            List<string> shared = [];
            long requiredBytes = 0;
            foreach (GameCoexistenceAsset asset in assets)
            {
                ResolveManifestPath(targetRoot, asset.Name);
                EnsureOrdinaryPath(asset.SourcePath);
                if (!names.Add(asset.Name.Replace('\\', '/')) || asset.Size < 0 || asset.Md5.Length != 32 || !asset.Md5.All(Uri.IsHexDigit)
                    || asset.Name.Equals(StateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Invalid or duplicate manifest asset.");
                }

                if (GetSharedRelative(asset) is { } relative)
                {
                    shared.Add(relative);
                }
                else
                {
                    requiredBytes = checked(requiredBytes + asset.Size);
                }
            }

            if (!names.Contains(Path.GetFileName(targetExecutable)))
            {
                throw new InvalidDataException("Target manifest has no server executable.");
            }

            if (new DriveInfo(Path.GetPathRoot(targetRoot)!).AvailableFreeSpace < requiredBytes)
            {
                throw new IOException($"Insufficient free space for private server resources ({requiredBytes} bytes required).");
            }

            GameServerCoexistenceState state = new()
            {
                PairId = Guid.NewGuid(),
                ChineseExecutable = Path.GetFullPath(sourceOversea ? targetExecutable : sourceExecutable),
                OverseaExecutable = Path.GetFullPath(sourceOversea ? sourceExecutable : targetExecutable),
                SharedFiles = shared,
                CreationPending = true,
            };
            Directory.CreateDirectory(targetRoot);
            // Both recovery records precede the first shared link. Incomplete builds cannot launch.
            WriteState(sourceRoot, state);
            WriteState(targetRoot, state);
            int count = 0;
            long bytes = 0;
            foreach (GameCoexistenceAsset asset in assets)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(asset.Name);
                token.ThrowIfCancellationRequested();
                string target = ResolveManifestPath(targetRoot, asset.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool share = GetSharedRelative(asset) is not null;
                // Close hash handles before creating/replacing a directory entry on Windows.
                using (FileStream source = OpenStableRead(asset.SourcePath))
                {
                    if (source.Length != asset.Size || !Convert.ToHexString(await MD5.HashDataAsync(source, token).ConfigureAwait(false)).Equals(asset.Md5, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Manifest checksum mismatch: {asset.Name}. Verify the source installation before retrying.");
                    }

                    if (!share)
                    {
                        source.Position = 0;
                        await using FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                        await source.CopyToAsync(destination, token).ConfigureAwait(false);
                        destination.Flush(true);
                    }
                }

                if (share)
                {
                    if (!CreateHardLink(target, asset.SourcePath, 0))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }

                    count++;
                    bytes = checked(bytes + asset.Size);
                }
            }

            token.ThrowIfCancellationRequested();
            await finishInstallation().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // SDK/deprecation finalization may have replaced some planned shared names.
            count = 0;
            bytes = 0;
            foreach (string relative in shared)
            {
                string path = GetAssetPath(Path.Combine(targetRoot, targetDataName), relative);
                if (File.Exists(path) && GetLinkCount(path) > 1)
                {
                    count++;
                    bytes = checked(bytes + new FileInfo(path).Length);
                }
            }

            WriteState(targetRoot, state with { CreationPending = false });
            WriteState(sourceRoot, state with { CreationPending = false });
            return (count, bytes);

            string? GetSharedRelative(GameCoexistenceAsset asset)
            {
                string normalized = asset.Name.Replace('\\', '/');
                string prefix = targetDataName + "/";
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string relative = normalized[prefix.Length..];
                return IsShareableRelativePath(relative)
                    && Path.GetFullPath(asset.SourcePath).Equals(ResolveManifestPath(sourceRoot, sourceDataName + "/" + relative), StringComparison.OrdinalIgnoreCase)
                    ? relative : null;
            }
        }
        finally
        {
            Gate.Release();
        }
    }
    private static bool IsDeviceName(string part)
    {
        string name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9');
    }

}
