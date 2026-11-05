// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

// Transfers include screenshots, configuration and caches, unlike version snapshots.
// A new destination is required: never merge into or overwrite an existing install.
public static class GameInstallationTransfer
{
    public static long GetRequiredBytes(string executable) => Enumerate(GameServerCoexistenceStorage.GetRoot(executable)).Sum(f => new FileInfo(f).Length);

    public static async Task CopyAsync(string sourceExecutable, string destinationExecutable, IProgress<string>? progress, CancellationToken token)
    {
        (string source, string destination) = ValidateRoots(sourceExecutable, destinationExecutable);
        List<string> files = Enumerate(source);
        if (!File.Exists(sourceExecutable)) { throw new FileNotFoundException("源游戏程序不存在。", sourceExecutable); }
        if (Directory.Exists(destination) || File.Exists(destination)) { throw new IOException("恢复目标必须是尚不存在的新目录，防止覆盖现有文件。"); }
        long bytes = files.Sum(f => new FileInfo(f).Length);
        string parent = Path.GetDirectoryName(destination)!;
        while (!Directory.Exists(parent)) { parent = Path.GetDirectoryName(parent) ?? throw new IOException("目标磁盘不存在。"); }
        if (GameRefsBlockClone.GetAvailableBytes(parent) < bytes + 1024L * 1024 * 1024) { throw new IOException("目标磁盘空间不足，无法复制完整安装并保留临时空间。"); }
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, file);
            progress?.Report("复制并校验：" + relative);
            string target = Path.Combine(destination, relative);
            GameServerCoexistenceStorage.EnsureOrdinaryPath(file);
            GameServerCoexistenceStorage.EnsureOrdinaryPath(target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
            await using (FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(true);
            }
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("校验完整副本：" + Path.GetRelativePath(source, file));
            await VerifyAsync(file, Path.Combine(destination, Path.GetRelativePath(source, file)), token).ConfigureAwait(false);
        }
        // Catch additions/removals by an external launcher during the transfer.
        if (!files.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(Enumerate(source).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            throw new IOException("复制期间源目录发生变化，未删除原文件。请关闭游戏和启动器后重试。");
        }
    }

    public static async Task DeleteVerifiedSourceAsync(string sourceExecutable, string destinationExecutable, IProgress<string>? progress, CancellationToken token)
    {
        (string source, string destination) = ValidateRoots(sourceExecutable, destinationExecutable);
        if (!File.Exists(destinationExecutable)) { throw new IOException("完整副本不存在，禁止删除原安装。"); }
        List<string> files = Enumerate(source);
        // Validate every source file before deleting any names. A stale/imported
        // library is never sufficient proof that deleting this source is safe.
        foreach (string file in files) { await VerifyAsync(file, Path.Combine(destination, Path.GetRelativePath(source, file)), token).ConfigureAwait(false); }
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            progress?.Report("清理原文件：" + Path.GetRelativePath(source, file));
            GameServerCoexistenceStorage.EnsureOrdinaryPath(file);
            GameServerCoexistenceStorage.EnsureOrdinaryPath(target);
            using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.Asynchronous);
            using FileStream copy = new(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
            await VerifyStreamsAsync(input, copy, token).ConfigureAwait(false);
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        // Keep the empty source folder. Never recursively delete an installation
        // root that another application could have populated during cleanup.
    }

    private static async Task VerifyAsync(string source, string target, CancellationToken token)
    {
        GameServerCoexistenceStorage.EnsureOrdinaryPath(source);
        GameServerCoexistenceStorage.EnsureOrdinaryPath(target);
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        using FileStream copy = new(target, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous);
        await VerifyStreamsAsync(input, copy, token).ConfigureAwait(false);
    }

    private static async Task VerifyStreamsAsync(Stream source, Stream target, CancellationToken token)
    {
        byte[] sourceHash = await SHA256.HashDataAsync(source, token).ConfigureAwait(false);
        byte[] targetHash = await SHA256.HashDataAsync(target, token).ConfigureAwait(false);
        if (source.Length != target.Length || !sourceHash.AsSpan().SequenceEqual(targetHash))
        {
            throw new IOException("原文件与虚拟盘副本不一致，已停止清理并保留未删除的原文件。");
        }
    }

    private static (string Source, string Destination) ValidateRoots(string sourceExecutable, string destinationExecutable)
    {
        string source = Path.TrimEndingDirectorySeparator(GameServerCoexistenceStorage.GetRoot(sourceExecutable));
        string destination = Path.TrimEndingDirectorySeparator(GameServerCoexistenceStorage.GetRoot(destinationExecutable));
        if (source.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(source)!), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(sourceExecutable).Equals(Path.GetFileName(destinationExecutable), StringComparison.OrdinalIgnoreCase)
            || source.Equals(destination, StringComparison.OrdinalIgnoreCase)
            || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new IOException("源安装与目标安装必须是互不包含的独立目录。"); }
        GameServerCoexistenceStorage.EnsureOrdinaryPath(source);
        GameServerCoexistenceStorage.EnsureOrdinaryPath(destination);
        return (source, destination);
    }

    private static List<string> Enumerate(string root)
    {
        List<string> files = [];
        Visit(root);
        return files;
        void Visit(string directory)
        {
            GameServerCoexistenceStorage.EnsureOrdinaryPath(directory);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { throw new IOException("迁移不允许目录链接或额外挂载点。"); }
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                GameServerCoexistenceStorage.EnsureOrdinaryPath(path);
                if (Directory.Exists(path)) { Visit(path); }
                else { files.Add(path); }
            }
        }
    }
}
