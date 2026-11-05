// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;
using System.Security.Cryptography;
using System.ComponentModel;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameSpaceDuplicate(string Source, string Target, string Hash);

public sealed record GameSpaceScan(long HostBytes, long LogicalBytes, long UniqueBytes, long ReclaimableBytes, IReadOnlyList<GameSpaceDuplicate> Duplicates, int SkippedFiles = 0)
{
    public string Description => $"VHDX 宿主占用：{GiB(HostBytes)} GiB；游戏逻辑大小：{GiB(LogicalBytes)} GiB\n游戏数据块占用：{GiB(UniqueBytes)} GiB；估算可优化：{GiB(ReclaimableBytes)} GiB（{(UniqueBytes == 0 ? 0 : (double)ReclaimableBytes / UniqueBytes):P1}）\n优化释放虚拟盘内部空间，不会自动缩小 VHDX。" + (SkippedFiles > 0 ? $"\n{SkippedFiles} 个文件或目录正在使用或变化，已跳过；本次统计不完整。" : string.Empty);

    private static string GiB(long bytes) => (bytes / (1024D * 1024 * 1024)).ToString("F2");
}

public static class GameSpaceOptimizer
{
    private sealed record FileAllocation(string Path, long Length, IReadOnlyList<GameRefsBlockClone.Extent> Extents);

    public static async Task<GameSpaceScan> ScanAsync(GameVirtualDiskInfo disk, IProgress<string>? progress, CancellationToken token, bool background = false, Func<IDisposable>? acquireAccess = null)
    {
        GameCoexistenceVirtualDisk.AssertMounted(disk.ChineseExecutable);
        GameVersionLibrary library = GameVersionLibrary.Read(disk);
        long cluster = GameRefsBlockClone.GetClusterSize(disk.MountPath + "\\");
        List<FileAllocation> files = [];
        int skipped = 0;
        foreach (GameVersionEntry entry in library.Entries.Where(e => !e.Pending))
        {
            string executable = entry.GetExecutable(disk);
            if (!Directory.Exists(Path.GetDirectoryName(executable))) { continue; }
            IReadOnlyList<string> paths;
            try
            {
                using IDisposable? access = acquireAccess?.Invoke();
                paths = GameServerCoexistenceStorage.GetSourceFiles(executable);
            }
            catch (Exception ex) when (background && IsTransientFileError(ex)) { skipped++; continue; }
            foreach (string file in paths)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(file);
                try
                {
                    using IDisposable? access = acquireAccess?.Invoke();
                    files.Add(new(file, new FileInfo(file).Length, GameRefsBlockClone.GetExtents(file)));
                }
                catch (Exception ex) when (background && IsTransientFileError(ex)) { skipped++; }
            }
        }

        List<GameSpaceDuplicate> duplicates = [];
        Dictionary<string, IReadOnlyList<GameRefsBlockClone.Extent>> planned = new(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<long, FileAllocation> group in files.Where(f => f.Length >= 1024 * 1024 && (!background || IsBackgroundCandidate(f.Path))).GroupBy(f => f.Length).Where(g => g.Count() > 1))
        {
            Dictionary<string, (string Path, IReadOnlyList<GameRefsBlockClone.Extent> Extents)> hashes = [];
            foreach (FileAllocation file in group)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(file.Path);
                string hash;
                try
                {
                    using IDisposable? access = acquireAccess?.Invoke();
                    if (!Eligible(file.Path)) { continue; }
                    using FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
                    if (stream.Length != file.Length) { skipped++; continue; }
                    hash = Convert.ToHexString(await GameOptimizationWorker.HashAsync(stream, background, token).ConfigureAwait(false));
                }
                catch (Exception ex) when (background && IsTransientFileError(ex)) { skipped++; continue; }
                if (hashes.TryGetValue(hash, out (string Path, IReadOnlyList<GameRefsBlockClone.Extent> Extents) source))
                {
                    long prefix = file.Length / cluster;
                    List<GameRefsBlockClone.Extent> replacement = [];
                    foreach (GameRefsBlockClone.Extent extent in source.Extents.Where(e => e.Vcn < prefix))
                    {
                        replacement.Add(extent with { Clusters = Math.Min(extent.Clusters, prefix - extent.Vcn) });
                    }

                    foreach (GameRefsBlockClone.Extent extent in file.Extents.Where(e => e.Vcn + e.Clusters > prefix))
                    {
                        long skip = Math.Max(0, prefix - extent.Vcn);
                        replacement.Add(new(extent.Vcn + skip, extent.Lcn + skip, extent.Clusters - skip));
                    }

                    planned.Add(file.Path, replacement);
                    if (!file.Extents.SequenceEqual(replacement)) { duplicates.Add(new(source.Path, file.Path, hash)); }
                }
                else { hashes.Add(hash, (file.Path, file.Extents)); }
            }
        }

        long current = UnionClusters(files.SelectMany(f => f.Extents)) * cluster;
        long after = UnionClusters(files.SelectMany(f => planned.TryGetValue(f.Path, out IReadOnlyList<GameRefsBlockClone.Extent>? extents) ? extents : f.Extents)) * cluster;
        return new(GameRefsBlockClone.GetFileAllocatedBytes(disk.ImagePath), files.Sum(f => f.Length), current, Math.Max(0, current - after), duplicates, skipped);
    }

    public static long UnionClusters(IEnumerable<GameRefsBlockClone.Extent> extents)
    {
        long total = 0;
        long end = 0;
        foreach (GameRefsBlockClone.Extent extent in extents.OrderBy(e => e.Lcn))
        {
            long next = checked(extent.Lcn + extent.Clusters);
            total = checked(total + Math.Max(0, next - Math.Max(end, extent.Lcn)));
            end = Math.Max(end, next);
        }

        return total;
    }

    private static bool Eligible(string path) => (File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint | FileAttributes.SparseFile | FileAttributes.Compressed | FileAttributes.Encrypted)) == 0;

    internal static bool IsTransientFileError(Exception ex) => ex is IOException io && (io.HResult & 0xffff) is 2 or 3 or 32 or 33
        || ex is Win32Exception win32 && win32.NativeErrorCode is 2 or 3 or 32 or 33;

    internal static bool IsBackgroundCandidate(string path) => path.Replace('\\', '/').Contains("/StreamingAssets/", StringComparison.OrdinalIgnoreCase)
        && Path.GetExtension(path).ToLowerInvariant() is ".blk" or ".pck" or ".usm";
}
