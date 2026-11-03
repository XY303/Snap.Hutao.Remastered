using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Snap.Hutao.Remastered.Test.RuntimeBehavior;

[TestClass]
public sealed class GameCoexistenceManifestTest
{
    private string workspace = null!;
    private string sourceExecutable = null!;
    private string targetExecutable = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Requires Windows NTFS hard links.");
        }

        workspace = Path.Combine(Path.GetTempPath(), "hutao-manifest-" + Guid.NewGuid().ToString("N"));
        sourceExecutable = Path.Combine(workspace, "Game", "YuanShen.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceExecutable)!);
        File.WriteAllText(sourceExecutable, "original CN executable");
        targetExecutable = GameServerCoexistenceStorage.GetGeneratedExecutable(sourceExecutable);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }

    [TestMethod]
    public async Task CreatesOppositeServerFromManifestWithoutSecondInstallation()
    {
        List<GameCoexistenceAsset> assets =
        [
            Asset("GenshinImpact.exe", "cache/GenshinImpact.exe", "OS executable"),
            Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "shared resource"),
            Asset("UnityPlayer.dll", "Game/UnityPlayer.dll", "private DLL"),
            Asset("GenshinImpact_Data/StreamingAssets/new.blk", "cache/new.blk", "different resource"),
        ];
        bool finished = false;
        (int files, long bytes) = await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, assets, () =>
        {
            finished = true;
            Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
            return Task.CompletedTask;
        });

        Assert.IsTrue(finished);
        Assert.AreEqual(1, files);
        Assert.AreEqual(15L, bytes);
        Assert.AreEqual("OS executable", File.ReadAllText(targetExecutable));
        Assert.AreEqual("original CN executable", File.ReadAllText(sourceExecutable));
        Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(Target("GenshinImpact_Data/StreamingAssets/a.blk")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Target("UnityPlayer.dll")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Target("GenshinImpact_Data/StreamingAssets/new.blk")));
        Assert.AreEqual(targetExecutable, GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
        await GameServerCoexistenceStorage.DetachAsync(GameServerCoexistenceStorage.GetRoot(targetExecutable));
        File.WriteAllText(Target("GenshinImpact_Data/StreamingAssets/a.blk"), "updated by repair");
        Assert.AreEqual("shared resource", File.ReadAllText(assets[1].SourcePath));
    }

    [TestMethod]
    [DataRow("pkg_version")]
    [DataRow("Audio_Chinese_pkg_version")]
    [DataRow("Audio_English(US)_pkg_version")]
    [DataRow("Audio_Japanese_pkg_version")]
    [DataRow("Audio_Korean_pkg_version")]
    public async Task MutableRootInventoriesUseVerifiedTargetContent(string name)
    {
        Assert.IsTrue(GameServerCoexistenceStorage.IsPackageInventory(name));
        string local = Path.Combine(GameServerCoexistenceStorage.GetRoot(sourceExecutable), name);
        File.WriteAllText(local, "launcher-rewritten inventory, not the published bytes");
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset inventory = Asset(name, "cache/target-inventory", "verified target inventory");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, inventory], () => Task.CompletedTask);
        Assert.AreEqual("verified target inventory", File.ReadAllText(Target(name)));
        Assert.AreEqual("launcher-rewritten inventory, not the published bytes", File.ReadAllText(local));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(local));
    }

    [TestMethod]
    [DataRow("YuanShen_Data/StreamingAssets/Audio_Chinese_pkg_version")]
    [DataRow("StreamingAssets/a.pck")]
    [DataRow("Audio_Chinese_pkg_version.blk")]
    public void InventoryExceptionDoesNotApplyToPayloadPaths(string name)
    {
        Assert.IsFalse(GameServerCoexistenceStorage.IsPackageInventory(name));
    }

    [TestMethod]
    public async Task CorruptSourceCannotBecomeLaunchable()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "executable");
        GameCoexistenceAsset resource = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "valid");
        File.WriteAllText(resource.SourcePath, "wrong");
        await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, resource], () => Task.CompletedTask));
        Assert.IsTrue(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(sourceExecutable))!.CreationPending);
        Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(sourceExecutable));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(sourceExecutable)));
        Assert.AreEqual("wrong", File.ReadAllText(resource.SourcePath));
    }

    [TestMethod]
    public async Task FailedSdkFinalizationRetainsRecoveryRecords()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset resource = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "same");
        await Assert.ThrowsAsync<IOException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, resource], () => throw new IOException("SDK download failed")));
        Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(sourceExecutable));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(resource.SourcePath));
        Assert.AreEqual("same", File.ReadAllText(resource.SourcePath));
    }

    [TestMethod]
    public async Task CancellationAfterFirstSharedFileCanBeUnpaired()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset first = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "same");
        GameCoexistenceAsset last = Asset("GenshinImpact_Data/StreamingAssets/b.blk", "Game/YuanShen_Data/StreamingAssets/b.blk", "last");
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(path => { if (path == last.Name) { cancellation.Cancel(); } });
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, first, last], () => Task.CompletedTask, progress, cancellation.Token));
        Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(first.SourcePath));
        Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(sourceExecutable));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(first.SourcePath));
        Assert.AreEqual("same", File.ReadAllText(first.SourcePath));
    }

    [TestMethod]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        Directory.CreateDirectory(GameServerCoexistenceStorage.GetRoot(targetExecutable));
        File.WriteAllText(targetExecutable, "existing user file");
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "new executable");
        await Assert.ThrowsAsync<IOException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable], () => Task.CompletedTask));
        Assert.AreEqual("existing user file", File.ReadAllText(targetExecutable));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(sourceExecutable)));
    }

    [TestMethod]
    [DataRow("../escape.blk")]
    [DataRow("GenshinImpact_Data/../../escape.blk")]
    [DataRow("C:/escape.blk")]
    [DataRow("GenshinImpact_Data/StreamingAssets/a.blk:ads")]
    [DataRow("GenshinImpact_Data/StreamingAssets/a.blk.")]
    [DataRow("GenshinImpact_Data//a.blk")]
    [DataRow("GenshinImpact_Data/CON.blk")]
    public async Task RejectsRemotePathTraversalBeforeCreatingRecords(string name)
    {
        GameCoexistenceAsset asset = Asset(name, "cache/source", "data");
        await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [asset], () => Task.CompletedTask));
        Assert.IsFalse(Directory.Exists(GameServerCoexistenceStorage.GetRoot(targetExecutable)));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(sourceExecutable)));
    }

    [TestMethod]
    public async Task GeneratesChineseServerFromOverseasSource()
    {
        File.Delete(sourceExecutable);
        sourceExecutable = Path.Combine(workspace, "Game", "GenshinImpact.exe");
        File.WriteAllText(sourceExecutable, "OS executable");
        targetExecutable = GameServerCoexistenceStorage.GetGeneratedExecutable(sourceExecutable);
        GameCoexistenceAsset executable = Asset("YuanShen.exe", "cache/exe", "CN executable");
        GameCoexistenceAsset resource = Asset("YuanShen_Data/StreamingAssets/a.pck", "Game/GenshinImpact_Data/StreamingAssets/a.pck", "same");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, resource], () => Task.CompletedTask);
        Assert.AreEqual(targetExecutable, GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, false));
        Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(resource.SourcePath));
    }

    [TestMethod]
    public async Task SdkExtractionReplacesNamesWithoutWritingThroughSharedResources()
    {
        const string resourceName = "GenshinImpact_Data/StreamingAssets/a.blk";
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset resource = Asset(resourceName, "Game/YuanShen_Data/StreamingAssets/a.blk", "original");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, resource], async () =>
        {
            using MemoryStream zip = new();
            using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                using StreamWriter writer = new(archive.CreateEntry(resourceName).Open());
                writer.Write("SDK replacement");
            }

            zip.Position = 0;
            await GameServerCoexistenceStorage.ExtractSdkAsync(zip, GameServerCoexistenceStorage.GetRoot(targetExecutable));
        });
        Assert.AreEqual("original", File.ReadAllText(resource.SourcePath));
        Assert.AreEqual("SDK replacement", File.ReadAllText(Target(resourceName)));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(resource.SourcePath));
    }

    [TestMethod]
    public async Task SdkArchiveCannotEscapeTargetDirectory()
    {
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using StreamWriter writer = new(archive.CreateEntry("../escape.dll").Open());
            writer.Write("invalid");
        }

        zip.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.ExtractSdkAsync(zip, GameServerCoexistenceStorage.GetRoot(targetExecutable)));
        Assert.IsFalse(File.Exists(Path.Combine(workspace, "escape.dll")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ForcedRepairUnlinksCurrentResourcesWithoutCopyingOrChangingPeer(bool repairSource)
    {
        const string name = "GenshinImpact_Data/StreamingAssets/a.blk";
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset shared = Asset(name, "Game/YuanShen_Data/StreamingAssets/a.blk", "original");
        GameCoexistenceAsset privateFile = Asset("UnityPlayer.dll", "Game/UnityPlayer.dll", "private");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, shared, privateFile], () => Task.CompletedTask);
        string root = GameServerCoexistenceStorage.GetRoot(repairSource ? sourceExecutable : targetExecutable);
        string selected = repairSource ? shared.SourcePath : Target(name);
        string peer = repairSource ? Target(name) : shared.SourcePath;
        Assert.AreEqual(shared.Size, GameServerCoexistenceStorage.GetRequiredUnlinkRepairBytes(root));
        await GameServerCoexistenceStorage.UnlinkForRepairAsync(root);

        Assert.IsFalse(File.Exists(selected));
        Assert.AreEqual("original", File.ReadAllText(peer));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(peer));
        Assert.AreEqual("private", File.ReadAllText(Path.Combine(root, privateFile.Name)));
        Assert.IsTrue(File.Exists(repairSource ? sourceExecutable : targetExecutable));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(root));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(sourceExecutable)));
        Assert.IsFalse(Directory.EnumerateFiles(workspace, "*.hutao-private-*", SearchOption.AllDirectories).Any());

        // A later repair writes an independent replacement without touching the peer.
        File.WriteAllText(selected, "repaired");
        Assert.AreEqual("original", File.ReadAllText(peer));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(selected));
    }

    [TestMethod]
    public async Task InterruptedForcedRepairBlocksLaunchAndCanResumeUnlinking()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset first = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "first");
        GameCoexistenceAsset last = Asset("GenshinImpact_Data/StreamingAssets/b.blk", "Game/YuanShen_Data/StreamingAssets/b.blk", "last");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, first, last], () => Task.CompletedTask);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(path => { if (path.EndsWith("b.blk", StringComparison.Ordinal)) { cancellation.Cancel(); } });
        string root = GameServerCoexistenceStorage.GetRoot(targetExecutable);
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameServerCoexistenceStorage.UnlinkForRepairAsync(root, progress, cancellation.Token));
        Assert.IsFalse(File.Exists(Target(first.Name)));
        Assert.IsTrue(File.Exists(Target(last.Name)));
        Assert.IsTrue(GameServerCoexistenceStorage.ReadState(root)!.CreationPending);
        Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(sourceExecutable, true));
        await GameServerCoexistenceStorage.UnlinkForRepairAsync(root);
        Assert.IsFalse(File.Exists(Target(last.Name)));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(root));
        Assert.AreEqual("first", File.ReadAllText(first.SourcePath));
        Assert.AreEqual("last", File.ReadAllText(last.SourcePath));
    }

    [TestMethod]
    public async Task ForcedRepairKeepsAlreadyIndependentResources()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset shared = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "independent");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, shared], () => Task.CompletedTask);
        string root = GameServerCoexistenceStorage.GetRoot(targetExecutable);
        await GameServerCoexistenceStorage.DetachAsync(root);
        Assert.AreEqual(0L, GameServerCoexistenceStorage.GetRequiredUnlinkRepairBytes(root));
        await GameServerCoexistenceStorage.UnlinkForRepairAsync(root);
        Assert.AreEqual("independent", File.ReadAllText(Target(shared.Name)));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(root));
    }

    [TestMethod]
    public async Task ForcedRepairCanRecoverIncompleteCreationAndMissingPeerRecord()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset shared = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "original");
        await Assert.ThrowsAsync<IOException>(() => GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, shared], () => throw new IOException("interrupted")));
        File.Delete(Path.Combine(GameServerCoexistenceStorage.GetRoot(sourceExecutable), GameServerCoexistenceStorage.StateFileName));
        string root = GameServerCoexistenceStorage.GetRoot(targetExecutable);
        await GameServerCoexistenceStorage.UnlinkForRepairAsync(root);
        Assert.IsFalse(File.Exists(Target(shared.Name)));
        Assert.AreEqual("original", File.ReadAllText(shared.SourcePath));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(root));
    }

    [TestMethod]
    public async Task ForcedRepairPausePreventsUnlinkingUntilResumed()
    {
        GameCoexistenceAsset executable = Asset("GenshinImpact.exe", "cache/exe", "exe");
        GameCoexistenceAsset shared = Asset("GenshinImpact_Data/StreamingAssets/a.blk", "Game/YuanShen_Data/StreamingAssets/a.blk", "original");
        await GameServerCoexistenceStorage.CreateFromManifestAsync(sourceExecutable, targetExecutable, [executable, shared], () => Task.CompletedTask);
        TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = GameServerCoexistenceStorage.UnlinkForRepairAsync(GameServerCoexistenceStorage.GetRoot(targetExecutable), waitForResume: () =>
        {
            paused.TrySetResult();
            return resume.Task;
        });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(File.Exists(Target(shared.Name)));
        Assert.IsFalse(operation.IsCompleted);
        resume.SetResult();
        await operation;
        Assert.IsFalse(File.Exists(Target(shared.Name)));
        Assert.AreEqual("original", File.ReadAllText(shared.SourcePath));
    }

    private GameCoexistenceAsset Asset(string name, string source, string content)
    {
        string path = Path.Combine(workspace, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        using FileStream stream = File.OpenRead(path);
        return new(name, path, stream.Length, Convert.ToHexString(MD5.HashData(stream)));
    }

    private string Target(string relative)
    {
        return Path.Combine(GameServerCoexistenceStorage.GetRoot(targetExecutable), relative);
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
