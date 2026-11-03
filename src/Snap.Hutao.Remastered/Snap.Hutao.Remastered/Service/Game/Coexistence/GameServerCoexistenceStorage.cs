// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Win32.SafeHandles;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

// No directory links, recursive deletion, or implicit copies of executable/configuration files.
// All mutations are journaled, and shared files are replaced through independent siblings.
public static partial class GameServerCoexistenceStorage
{
    public const string StateFileName = "snap_hutao_server_coexistence.json";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string GetRoot(string executable)
    {
        string fullPath = Path.GetFullPath(executable);
        string name = Path.GetFileName(fullPath);
        if (!name.Equals("YuanShen.exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Expected YuanShen.exe or GenshinImpact.exe.");
        }

        return Path.GetDirectoryName(fullPath)!;
    }

    public static GameServerCoexistenceState? ReadState(string root)
    {
        string path = Path.Combine(root, StateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        EnsureOrdinaryPath(path);
        using FileStream stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("Coexistence journal exceeds the size limit.");
        }

        GameServerCoexistenceState state = JsonSerializer.Deserialize(stream, GameServerCoexistenceJsonContext.Default.GameServerCoexistenceState)
            ?? throw new InvalidDataException("Invalid coexistence journal.");
        ValidateState(root, state);
        return state;
    }

    public static string? GetCounterpart(string executable, bool targetOversea)
    {
        string root = GetRoot(executable);
        GameServerCoexistenceState? state = ReadState(root);
        if (state is null)
        {
            return null;
        }

        string target = targetOversea ? state.OverseaExecutable : state.ChineseExecutable;
        GameServerCoexistenceState? peer = ReadState(GetRoot(target));
        if (state.CreationPending || peer is null || peer.CreationPending || peer.PairId != state.PairId || !SamePair(state, peer) || !File.Exists(target))
        {
            throw new InvalidDataException("The paired installation is unavailable or its journal does not match. Restore independent files before continuing.");
        }

        return target;
    }

    public static async Task<(int Files, long Bytes)> ShareAsync(string chineseExecutable, string overseaExecutable, IProgress<string>? progress = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string chineseRoot = GetRoot(chineseExecutable);
            string overseaRoot = GetRoot(overseaExecutable);
            ValidateRoots(chineseRoot, overseaRoot);
            if (!Path.GetFileName(chineseExecutable).Equals("YuanShen.exe", StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(overseaExecutable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Coexistence requires one Chinese and one overseas installation.");
            }

            ValidateExistingPair(chineseRoot, chineseExecutable, overseaExecutable);
            ValidateExistingPair(overseaRoot, chineseExecutable, overseaExecutable);
            // Keep old journals intact until all old sharing has been detached.
            await DetachCoreAsync(chineseRoot, progress, token).ConfigureAwait(false);
            await DetachCoreAsync(overseaRoot, progress, token).ConfigureAwait(false);

            List<string> candidates = [];
            string chineseData = Path.Combine(chineseRoot, "YuanShen_Data");
            string overseaData = Path.Combine(overseaRoot, "GenshinImpact_Data");
            foreach (string source in EnumerateOrdinaryFiles(Path.Combine(chineseData, "StreamingAssets")))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(chineseData, source);
                if (!IsShareableRelativePath(relative))
                {
                    continue;
                }

                string target = GetAssetPath(overseaData, relative);
                if (File.Exists(target) && new FileInfo(source).Length == new FileInfo(target).Length)
                {
                    EnsureOrdinaryPath(target);
                    candidates.Add(relative);
                }
            }

            candidates.Sort(StringComparer.OrdinalIgnoreCase);
            GameServerCoexistenceState state = new()
            {
                PairId = Guid.NewGuid(),
                ChineseExecutable = Path.GetFullPath(chineseExecutable),
                OverseaExecutable = Path.GetFullPath(overseaExecutable),
                SharedFiles = candidates,
            };
            // An interrupted run may have extra journal entries, but never untracked links.
            WriteState(chineseRoot, state);
            WriteState(overseaRoot, state);

            int count = 0;
            long bytes = 0;
            foreach (string relative in candidates)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(relative);
                string source = GetAssetPath(chineseData, relative);
                string target = GetAssetPath(overseaData, relative);
                long length;
                using (FileStream sourceStream = OpenStableRead(source))
                using (FileStream targetStream = OpenStableRead(target))
                {
                    length = sourceStream.Length;
                    if (length != targetStream.Length)
                    {
                        continue;
                    }

                    byte[] sourceHash = await SHA256.HashDataAsync(sourceStream, token).ConfigureAwait(false);
                    byte[] targetHash = await SHA256.HashDataAsync(targetStream, token).ConfigureAwait(false);
                    if (!sourceHash.AsSpan().SequenceEqual(targetHash))
                    {
                        continue;
                    }
                }

                // Windows requires all our read handles closed before replacing the name.
                // External launchers must remain closed for the entire operation.
                string temporary = target + ".hutao-link-" + state.PairId.ToString("N");
                try
                {
                    if (!CreateHardLink(temporary, source, 0))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    }

                    File.Move(temporary, target, true);
                    count++;
                    bytes += length;
                }
                finally
                {
                    File.Delete(temporary);
                }
            }

            return (count, bytes);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Used before every in-app package write, even Verify (which can repair).
    // Journals remain so routing continues to work after an update.
    public static async Task DetachAsync(string root, IProgress<string>? progress = null, CancellationToken token = default, Func<Task>? waitForResume = null)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await DetachCoreAsync(root, progress, token, waitForResume).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task UnpairAsync(string root, IProgress<string>? progress = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            GameServerCoexistenceState? state = ReadState(root);
            if (state is null)
            {
                return;
            }

            string peerRoot = GetOtherRoot(root, state);
            GameServerCoexistenceState? peer = Directory.Exists(peerRoot) ? ReadState(peerRoot) : null;
            if (peer is not null && (peer.PairId != state.PairId || !SamePair(state, peer)))
            {
                throw new InvalidDataException("The other installation belongs to a different pair.");
            }

            await DetachCoreAsync(root, progress, token).ConfigureAwait(false);
            if (peer is not null)
            {
                await DetachCoreAsync(peerRoot, progress, token).ConfigureAwait(false);
                File.Delete(Path.Combine(peerRoot, StateFileName));
            }

            File.Delete(Path.Combine(root, StateFileName));
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool IsShareableRelativePath(string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "" or "." or "..")
            || normalized.Split('/').Any(part => part.Equals("Persistent", StringComparison.OrdinalIgnoreCase) || part.Equals("Plugins", StringComparison.OrdinalIgnoreCase))
            || !normalized.StartsWith("StreamingAssets/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Conservative allowlist: never DLLs, executables, config, scripts, or Persistent.
        return Path.GetExtension(relative).ToLowerInvariant() is ".blk" or ".pck" or ".usm";
    }

    public static long GetRequiredDetachBytes(string root)
    {
        GameServerCoexistenceState? state = ReadState(root);
        if (state is null || state.Format == 2)
        {
            return 0;
        }

        string data = Path.Combine(root, IsChineseRoot(root, state) ? "YuanShen_Data" : "GenshinImpact_Data");
        long bytes = 0;
        foreach (string relative in state.SharedFiles)
        {
            string path = GetAssetPath(data, relative);
            if (File.Exists(path) && GetLinkCount(path) > 1)
            {
                bytes = checked(bytes + new FileInfo(path).Length);
            }
        }

        return bytes;
    }

    public static uint GetLinkCount(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out FileInformation information))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return information.NumberOfLinks;
    }

    private static async Task DetachCoreAsync(string root, IProgress<string>? progress, CancellationToken token, Func<Task>? waitForResume = null)
    {
        GameServerCoexistenceState? state = ReadState(root);
        if (state is null || state.Format == 2 || state.SharedFiles.Count == 0)
        {
            return;
        }

        string data = Path.Combine(root, IsChineseRoot(root, state) ? "YuanShen_Data" : "GenshinImpact_Data");
        long requiredBytes = 0;
        foreach (string relative in state.SharedFiles)
        {
            string path = GetAssetPath(data, relative);
            // Sibling names are deterministic and covered by the journal, including a crash
            // between preparing a new file/link and atomically replacing the original name.
            foreach (string suffix in new[] { ".hutao-link-", ".hutao-private-" })
            {
                string temporary = path + suffix + state.PairId.ToString("N");
                EnsureOrdinaryPath(temporary);
                try
                {
                    File.Delete(temporary);
                }
                catch (DirectoryNotFoundException)
                {
                    // Legacy interrupted builds may not have created this directory.
                }
            }

            if (File.Exists(path) && GetLinkCount(path) > 1)
            {
                requiredBytes = checked(requiredBytes + new FileInfo(path).Length);
            }
        }

        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < requiredBytes)
        {
            throw new IOException($"Insufficient free space to restore independent resources ({requiredBytes} bytes required).");
        }

        foreach (string relative in state.SharedFiles)
        {
            if (waitForResume is not null)
            {
                await waitForResume().WaitAsync(token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            string path = GetAssetPath(data, relative);
            if (!File.Exists(path) || GetLinkCount(path) <= 1)
            {
                continue;
            }

            progress?.Report(relative);
            string temporary = path + ".hutao-private-" + state.PairId.ToString("N");
            try
            {
                using (FileStream source = OpenStableRead(path))
                {
                    await using FileStream target = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                    await source.CopyToAsync(target, token).ConfigureAwait(false);
                    target.Flush(true);
                }

                // Removing/replacing a name preserves the other hard links' content.
                File.Move(temporary, path, true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        WriteState(root, state with { SharedFiles = [] });
    }

    private static FileStream OpenStableRead(string path)
    {
        EnsureOrdinaryPath(path);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string directory)
    {
        EnsureOrdinaryPath(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            EnsureOrdinaryPath(path);
            if (Directory.Exists(path))
            {
                foreach (string file in EnumerateOrdinaryFiles(path))
                {
                    yield return file;
                }
            }
            else
            {
                yield return path;
            }
        }
    }

    private static string GetAssetPath(string data, string relative)
    {
        if (!IsShareableRelativePath(relative))
        {
            throw new InvalidDataException("Unsafe coexistence asset path.");
        }

        string path = Path.GetFullPath(Path.Combine(data, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Coexistence asset escapes the installation.");
        }

        EnsureOrdinaryPath(path);
        return path;
    }

    private static void ValidateRoots(string first, string second)
    {
        string firstFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));
        string secondFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        if (firstFull.Equals(secondFull, StringComparison.OrdinalIgnoreCase)
            || firstFull.StartsWith(secondFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || secondFull.StartsWith(firstFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Installations must be in separate, non-nested directories.");
        }

        string firstVolume = Path.GetPathRoot(firstFull)!;
        string secondVolume = Path.GetPathRoot(secondFull)!;
        if (firstVolume.StartsWith("\\\\", StringComparison.Ordinal) || !firstVolume.Equals(secondVolume, StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(firstVolume).DriveType is not (DriveType.Fixed or DriveType.Removable)
            || !new DriveInfo(firstVolume).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Experimental sharing requires two local installations on the same NTFS volume.");
        }

        EnsureOrdinaryPath(firstFull);
        EnsureOrdinaryPath(secondFull);
    }

    private static void ValidateState(string root, GameServerCoexistenceState state)
    {
        if (state.Format is not (1 or 2) || state.PairId == Guid.Empty || state.SharedFiles is null
            || !Path.IsPathFullyQualified(state.ChineseExecutable) || !Path.IsPathFullyQualified(state.OverseaExecutable)
            || !Path.GetFileName(state.ChineseExecutable).Equals("YuanShen.exe", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(state.OverseaExecutable).Equals("GenshinImpact.exe", StringComparison.OrdinalIgnoreCase)
            || state.SharedFiles.Any(relative => relative is null || !IsShareableRelativePath(relative)))
        {
            throw new InvalidDataException("Invalid coexistence journal fields.");
        }

        if (state.Format == 2)
        {
            GameVirtualDiskInfo disk = GameCoexistenceVirtualDisk.Find(state.ChineseExecutable)
                ?? throw new InvalidDataException("Missing VHDX descriptor.");
            if (!disk.ImagePath.Equals(state.VirtualDiskImage, StringComparison.OrdinalIgnoreCase)
                || !disk.ChineseExecutable.Equals(state.ChineseExecutable, StringComparison.OrdinalIgnoreCase)
                || !disk.OverseaExecutable.Equals(state.OverseaExecutable, StringComparison.OrdinalIgnoreCase)
                || !GameRefsBlockClone.GetVolume(root).Equals(disk.VolumeName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The journal does not match the managed virtual disk.");
            }

            GameRefsBlockClone.EnsureReFS(root);
            EnsureOrdinaryPath(root);
        }
        else
        {
            ValidateRoots(GetRoot(state.ChineseExecutable), GetRoot(state.OverseaExecutable));
        }
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!fullRoot.Equals(GetRoot(state.ChineseExecutable), StringComparison.OrdinalIgnoreCase)
            && !fullRoot.Equals(GetRoot(state.OverseaExecutable), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The coexistence journal belongs to another installation.");
        }
    }

    private static bool IsChineseRoot(string root, GameServerCoexistenceState state)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(GetRoot(state.ChineseExecutable), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOtherRoot(string root, GameServerCoexistenceState state)
    {
        return GetRoot(IsChineseRoot(root, state) ? state.OverseaExecutable : state.ChineseExecutable);
    }

    private static bool SamePair(GameServerCoexistenceState first, GameServerCoexistenceState second)
    {
        return first.ChineseExecutable.Equals(second.ChineseExecutable, StringComparison.OrdinalIgnoreCase)
            && first.OverseaExecutable.Equals(second.OverseaExecutable, StringComparison.OrdinalIgnoreCase)
            && first.Format == second.Format
            && string.Equals(first.VirtualDiskImage, second.VirtualDiskImage, StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateExistingPair(string root, string chineseExecutable, string overseaExecutable)
    {
        GameServerCoexistenceState? state = ReadState(root);
        if (state is not null && (!state.ChineseExecutable.Equals(Path.GetFullPath(chineseExecutable), StringComparison.OrdinalIgnoreCase)
            || !state.OverseaExecutable.Equals(Path.GetFullPath(overseaExecutable), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Restore independent files before associating a different installation.");
        }
    }

    private static void WriteState(string root, GameServerCoexistenceState state)
    {
        string path = Path.Combine(root, StateFileName);
        EnsureOrdinaryPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, GameServerCoexistenceJsonContext.Default.GameServerCoexistenceState);
                stream.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static void EnsureOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
                    && !GameRefsBlockClone.IsVolumeMountPoint(current))
                {
                    throw new InvalidDataException("Directory junctions and symbolic links are not supported in coexistence installations.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            current = Path.GetDirectoryName(current);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
