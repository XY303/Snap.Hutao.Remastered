using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Snap.Hutao.Remastered.Test.RuntimeBehavior;

[TestClass]
public sealed class GameVirtualDiskTest
{
    [TestMethod]
    public void ManagedOfflinePathsCannotFallBackToTheHostDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "hutao-offline-" + Guid.NewGuid().ToString("N") + ".hutao-vdisk");
        Directory.CreateDirectory(root);
        GameVirtualDiskInfo disk = new(root, "\\\\?\\Volume{" + Guid.NewGuid() + "}\\");
        try
        {
            Assert.Throws<InvalidDataException>(() => GameCoexistenceVirtualDisk.AssertMounted(disk.ChineseExecutable));
            File.WriteAllText(Path.Combine(root, GameCoexistenceVirtualDisk.DescriptorName), JsonSerializer.Serialize(disk));
            Assert.AreEqual(disk, GameCoexistenceVirtualDisk.Find(disk.ChineseExecutable));
            Assert.Throws<IOException>(() => GameCoexistenceVirtualDisk.AssertMounted(disk.ChineseExecutable));
            Assert.IsFalse(Directory.Exists(disk.MountPath));

            File.WriteAllText(Path.Combine(root, GameCoexistenceVirtualDisk.DescriptorName), JsonSerializer.Serialize(disk with { Container = root + "-other" }));
            Assert.Throws<InvalidDataException>(() => GameCoexistenceVirtualDisk.Find(disk.ChineseExecutable));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task NtfsCloningFailsWithoutCreatingAHardLink()
    {
        string root = Path.Combine(Path.GetTempPath(), "hutao-not-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.bin");
            string target = Path.Combine(root, "target.bin");
            File.WriteAllText(source, "original");
            await Assert.ThrowsAsync<IOException>(() => GameRefsBlockClone.CloneAsync(source, target));
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(source));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Opt-in: creates/formats ONLY a newly generated temporary VHDX and needs
    // administrator rights. Never use a real game installation as this fixture.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VirtualDiskPairSupportsCloningIndependentWritesAndRemount(bool sourceOversea)
    {
        if (Environment.GetEnvironmentVariable("HUTAO_VHD_INTEGRATION") != "1")
        {
            Assert.Inconclusive("Set HUTAO_VHD_INTEGRATION=1 in an elevated process to run isolated VHDX integration tests.");
        }

        string root = Path.Combine(Path.GetTempPath(), "hutao-vhd-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, sourceOversea ? "GenshinImpact.exe" : "YuanShen.exe");
        File.WriteAllText(original, "original executable");
        string sourceData = sourceOversea ? "GenshinImpact_Data" : "YuanShen_Data";
        string targetData = sourceOversea ? "YuanShen_Data" : "GenshinImpact_Data";
        string relative = "StreamingAssets/blocks/01/shared.blk";
        string largeOriginal = Path.Combine(root, sourceData, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(largeOriginal)!);
        byte[] content = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 123);
        File.WriteAllBytes(largeOriginal, content);
        Directory.CreateDirectory(Path.Combine(root, "chunks"));
        File.WriteAllText(Path.Combine(root, "chunks", "do-not-copy"), "cache");
        string payload = Path.Combine(root, "target-executable.cache");
        File.WriteAllText(payload, "opposite server executable");
        GameVirtualDiskInfo? disk = null;
        try
        {
            disk = await GameCoexistenceVirtualDisk.CreateAsync(original, content.Length, CancellationToken.None);
            Assert.AreEqual(disk, GameCoexistenceVirtualDisk.ReadDescriptor(disk.Container));
            await Assert.ThrowsAsync<IOException>(() => GameCoexistenceVirtualDisk.CreateAsync(original, 0, CancellationToken.None));
            await GameServerCoexistenceStorage.CopyBaseToVirtualDiskAsync(original, disk, GameServerCoexistenceStorage.GetSourceFiles(original), null, CancellationToken.None);
            string sourceExecutable = sourceOversea ? disk.OverseaExecutable : disk.ChineseExecutable;
            string targetExecutable = sourceOversea ? disk.ChineseExecutable : disk.OverseaExecutable;
            string sourceRoot = GameServerCoexistenceStorage.GetRoot(sourceExecutable);
            string targetRoot = GameServerCoexistenceStorage.GetRoot(targetExecutable);
            string source = Path.Combine(sourceRoot, sourceData, relative);
            string target = Path.Combine(targetRoot, targetData, relative);
            Assert.IsFalse(Directory.Exists(Path.Combine(sourceRoot, "chunks")));
            Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, !sourceOversea));

            List<GameCoexistenceAsset> assets = [
                new(Path.GetFileName(targetExecutable), payload, new FileInfo(payload).Length, Convert.ToHexString(MD5.HashData(File.ReadAllBytes(payload)))),
                new(targetData + "/" + relative, source, content.Length, Convert.ToHexString(MD5.HashData(content))),
            ];
            (int files, long bytes) = await GameServerCoexistenceStorage.CreateClonedFromManifestAsync(sourceExecutable, targetExecutable, assets, () => Task.CompletedTask);
            Assert.AreEqual(1, files);
            Assert.IsGreaterThan(0L, bytes);
            CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
            Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(source));
            Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(target));
            Assert.AreEqual(targetExecutable, GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, !sourceOversea));
            Assert.AreEqual(0L, GameServerCoexistenceStorage.GetRequiredDetachBytes(targetRoot));

            string sourceExtents = await RunAsync("fsutil.exe", ["file", "queryExtentsAndRefCounts", source]);
            string targetExtents = await RunAsync("fsutil.exe", ["file", "queryExtentsAndRefCounts", target]);
            string sourceLcn = Regex.Match(sourceExtents, @"LCN:\s*(0x[\da-f]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            Assert.IsFalse(string.IsNullOrEmpty(sourceLcn));
            Assert.AreEqual(sourceLcn, Regex.Match(targetExtents, @"LCN:\s*(0x[\da-f]+)", RegexOptions.IgnoreCase).Groups[1].Value);

            await GameServerCoexistenceStorage.DetachAsync(targetRoot);
            using (FileStream stream = File.OpenWrite(target))
            {
                stream.WriteByte((byte)(content[0] ^ 255));
                stream.Flush(true);
            }

            CollectionAssert.AreEqual(content, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(content, File.ReadAllBytes(largeOriginal));
            byte[] targetContent = File.ReadAllBytes(target);
            Assert.AreNotEqual(content[0], targetContent[0]);

            await StorageAsync("Dismount-DiskImage -ImagePath " + Quote(disk.ImagePath));
            Assert.Throws<IOException>(() => GameCoexistenceVirtualDisk.AssertMounted(sourceExecutable));
            await GameCoexistenceVirtualDisk.EnsureMountedAsync(sourceExecutable);
            GameCoexistenceVirtualDisk.AssertMounted(sourceExecutable);
            CollectionAssert.AreEqual(content, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(targetContent, File.ReadAllBytes(target));

            await GameServerCoexistenceStorage.UnpairAsync(targetRoot);
            Assert.IsNull(GameServerCoexistenceStorage.ReadState(sourceRoot));
            Assert.IsNull(GameServerCoexistenceStorage.ReadState(targetRoot));
            Assert.IsNotNull(GameCoexistenceVirtualDisk.Find(targetExecutable));
            Assert.IsTrue(File.Exists(sourceExecutable));
            Assert.IsTrue(File.Exists(targetExecutable));
        }
        finally
        {
            string container = GameCoexistenceVirtualDisk.GetContainer(original);
            string image = Path.Combine(container, "game.vhdx");
            if (File.Exists(image))
            {
                await StorageAsync("if ((Get-DiskImage -ImagePath " + Quote(image) + ").Attached) { Dismount-DiskImage -ImagePath " + Quote(image) + " }");
                string mount = Path.Combine(container, "volume");
                if (GameRefsBlockClone.IsVolumeMountPoint(mount))
                {
                    GameRefsBlockClone.RemoveOfflineMountPoint(mount);
                }

                // Only exact fixture paths, after detach; never recurse into a volume.
                File.Delete(image);
                File.Delete(Path.Combine(container, GameCoexistenceVirtualDisk.DescriptorName));
                Directory.Delete(mount);
                Directory.Delete(container);
            }

            Directory.Delete(root, true);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static Task<string> StorageAsync(string command) => RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + command))]);

    private static async Task<string> RunAsync(string command, string[] arguments)
    {
        ProcessStartInfo start = new(command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) { start.ArgumentList.Add(argument); }
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await errors);
        return await output;
    }
}
