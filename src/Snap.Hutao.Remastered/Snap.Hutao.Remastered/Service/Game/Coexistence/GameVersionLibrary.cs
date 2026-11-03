// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameVersionEntry(Guid Id, bool IsOversea, int Channel, int SubChannel, string Version, bool Pending)
{
    public string DisplayName => $"{(IsOversea ? "国际服" : "国服")} | {Channel}/{SubChannel} | {Version} | {Id.ToString("N")[..8]}{(Pending ? "（未完成）" : string.Empty)}";

    public string GetExecutable(GameVirtualDiskInfo disk) => Path.Combine(disk.MountPath, "versions", Id.ToString("N"), IsOversea ? "GenshinImpact.exe" : "YuanShen.exe");
}

public sealed record GameVersionLibrary(string OriginalExecutable, List<GameVersionEntry> Entries)
{
    public IEnumerable<GameVersionEntry> GetReadyEntries(GameVirtualDiskInfo disk, string? selectedExecutable)
        => Entries.AsEnumerable().Reverse().Where(e => !e.Pending)
            .OrderByDescending(e => e.GetExecutable(disk).Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase));

    // Collapse accidental copies while keeping different channels and game versions selectable.
    // Keep all catalog records and files so users can explicitly delete unwanted copies.
    public IReadOnlyList<GameVersionEntry> GetSelectableEntries(GameVirtualDiskInfo disk, string? selectedExecutable)
        => GetReadyEntries(disk, selectedExecutable).DistinctBy(e => (e.IsOversea, e.Channel, e.SubChannel, e.Version)).ToList();

    public void DeleteVersion(GameVirtualDiskInfo disk, Guid id, IProgress<string>? progress, CancellationToken token)
    {
        GameVersionEntry entry = Entries.Single(e => e.Id == id);
        GameCoexistenceVirtualDisk.AssertMounted(entry.GetExecutable(disk));
        string root = Path.GetDirectoryName(entry.GetExecutable(disk))!;
        string versions = Path.GetDirectoryName(root)!;
        GameServerCoexistenceStorage.EnsureOrdinaryPath(versions);
        if (Directory.Exists(versions) && (File.GetAttributes(versions) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("版本目录不能是链接或额外挂载点。"); }
        List<string> files = [];
        List<string> directories = [];
        Visit(root); // Validate the entire tree before making any changes.
        token.ThrowIfCancellationRequested();
        int index = Entries.IndexOf(entry);
        Entries[index] = entry with { Pending = true };
        Save(disk); // Interrupted deletion must not leave a launchable partial installation.
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            GameServerCoexistenceStorage.EnsureOrdinaryPath(file);
            progress?.Report(file);
            if (File.Exists(file))
            {
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }
        }

        foreach (string directory in directories.AsEnumerable().Reverse())
        {
            token.ThrowIfCancellationRequested();
            GameServerCoexistenceStorage.EnsureOrdinaryPath(directory);
            Directory.Delete(directory); // Never recursively follow a replacement directory/link.
        }

        Entries.RemoveAt(index);
        Save(disk);

        void Visit(string directory)
        {
            token.ThrowIfCancellationRequested();
            GameServerCoexistenceStorage.EnsureOrdinaryPath(directory);
            if (!Directory.Exists(directory)) { return; }
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("不能删除包含链接或额外挂载点的版本。"); }
            directories.Add(directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                GameServerCoexistenceStorage.EnsureOrdinaryPath(path);
                if (Directory.Exists(path)) { Visit(path); }
                else { files.Add(path); }
            }
        }
    }

    public static GameVersionLibrary Read(GameVirtualDiskInfo disk)
    {
        string path = Path.Combine(disk.Container, "installations.json");
        GameServerCoexistenceStorage.EnsureOrdinaryPath(path);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) { throw new InvalidDataException("Version catalog too large."); }
        using FileStream input = File.OpenRead(path);
        GameVersionLibrary library = JsonSerializer.Deserialize(input, GameServerCoexistenceJsonContext.Default.GameVersionLibrary)
            ?? throw new InvalidDataException("Invalid version catalog.");
        if (library.Entries is null || library.Entries.Any(e => e is null || e.Id == Guid.Empty) || library.Entries.Select(e => e.Id).Distinct().Count() != library.Entries.Count)
        {
            throw new InvalidDataException("Invalid version identities.");
        }

        if (string.IsNullOrEmpty(library.OriginalExecutable) || !Path.IsPathFullyQualified(library.OriginalExecutable)) { throw new InvalidDataException("Invalid original installation path."); }
        GameServerCoexistenceStorage.GetRoot(library.OriginalExecutable);
        return library;
    }

    public void Save(GameVirtualDiskInfo disk)
    {
        string path = Path.Combine(disk.Container, "installations.json");
        GameServerCoexistenceStorage.EnsureOrdinaryPath(path);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, this, GameServerCoexistenceJsonContext.Default.GameVersionLibrary);
                output.Flush(true);
            }

            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }

    public static GameVersionEntry? FindEntry(string executable)
    {
        if (GameCoexistenceVirtualDisk.Find(executable) is not { } disk) { return null; }
        bool managed = Path.GetFullPath(executable).StartsWith(Path.Combine(disk.MountPath, "versions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!File.Exists(Path.Combine(disk.Container, "installations.json")))
        {
            if (managed) { throw new InvalidDataException("共存版本清单丢失，请恢复 installations.json。"); }
            return null;
        }

        GameVersionEntry? entry = Read(disk).Entries.SingleOrDefault(e => e.GetExecutable(disk).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));
        if (managed && entry is null) { throw new InvalidDataException("此路径不属于已登记的共存版本。"); }
        return entry;
    }

    public static async Task CopySnapshotAsync(string sourceExecutable, string targetExecutable, IProgress<string>? progress, CancellationToken token)
    {
        string root = GameServerCoexistenceStorage.GetRoot(sourceExecutable);
        string targetRoot = GameServerCoexistenceStorage.GetRoot(targetExecutable);
        GameCoexistenceVirtualDisk.AssertMounted(targetExecutable);
        GameServerCoexistenceStorage.EnsureOrdinaryPath(targetRoot);
        if (Directory.Exists(targetRoot)) { throw new IOException("Version destination already exists."); }
        Directory.CreateDirectory(targetRoot);
        bool sameVolume = GameRefsBlockClone.GetVolume(sourceExecutable).Equals(GameRefsBlockClone.GetVolume(targetRoot), StringComparison.OrdinalIgnoreCase);
        foreach (string file in GameServerCoexistenceStorage.GetSourceFiles(sourceExecutable))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(root, file);
            string target = GameServerCoexistenceStorage.ResolveManifestPath(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            progress?.Report(relative);
            if (sameVolume && new FileInfo(file).Length >= 1024 * 1024)
            {
                await GameRefsBlockClone.CloneAsync(file, target, token).ConfigureAwait(false);
            }
            else
            {
                using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
                await using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(true);
            }
        }
    }
}
