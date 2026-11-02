using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.IO;

namespace Snap.Hutao.Remastered.Test.RuntimeBehavior;

[TestClass]
public sealed class GameServerCoexistenceTest
{
    private string workspace = null!;
    private string chinese = null!;
    private string oversea = null!;

    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Hard-link integration tests require Windows.");
        }

        workspace = Path.Combine(Path.GetTempPath(), "hutao-coexistence-test-" + Guid.NewGuid().ToString("N"));
        chinese = Path.Combine(workspace, "CN", "YuanShen.exe");
        oversea = Path.Combine(workspace, "OS", "GenshinImpact.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(chinese)!);
        Directory.CreateDirectory(Path.GetDirectoryName(oversea)!);
        File.WriteAllText(chinese, "fake CN executable");
        File.WriteAllText(oversea, "fake OS executable");
        Directory.CreateDirectory(Path.Combine(workspace, "CN", "YuanShen_Data", "StreamingAssets"));
        Directory.CreateDirectory(Path.Combine(workspace, "OS", "GenshinImpact_Data", "StreamingAssets"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (workspace is not null && Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }

    [TestMethod]
    public async Task OnlyIdenticalAllowlistedResourcesAreShared()
    {
        AddBoth("StreamingAssets/blocks/shared.blk", "same", "same");
        AddBoth("StreamingAssets/blocks/different.blk", "aaaa", "bbbb");
        AddBoth("StreamingAssets/Plugins/plugin.dll", "same", "same");
        AddBoth("Persistent/cache.blk", "same", "same");
        AddBoth("StreamingAssets/config.json", "same", "same");

        (int files, long bytes) = await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);

        Assert.AreEqual(1, files);
        Assert.AreEqual(4L, bytes);
        Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(Asset(false, "StreamingAssets/blocks/shared.blk")));
        Assert.AreEqual(4L, GameServerCoexistenceStorage.GetRequiredDetachBytes(GameServerCoexistenceStorage.GetRoot(oversea)));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, "StreamingAssets/blocks/different.blk")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, "StreamingAssets/Plugins/plugin.dll")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, "Persistent/cache.blk")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, "StreamingAssets/config.json")));
        Assert.AreEqual(oversea, GameServerCoexistenceStorage.GetCounterpart(chinese, true));
        Assert.AreEqual(chinese, GameServerCoexistenceStorage.GetCounterpart(oversea, false));
    }

    [TestMethod]
    public async Task DetachingBeforeAnInPlaceRepairPreservesTheOtherInstallation()
    {
        const string relative = "StreamingAssets/blocks/shared.blk";
        AddBoth(relative, "original", "original");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);

        await GameServerCoexistenceStorage.DetachAsync(GameServerCoexistenceStorage.GetRoot(oversea));
        File.WriteAllText(Asset(true, relative), "repaired");

        Assert.AreEqual("original", File.ReadAllText(Asset(false, relative)));
        Assert.AreEqual("repaired", File.ReadAllText(Asset(true, relative)));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, relative)));
        Assert.AreEqual(oversea, GameServerCoexistenceStorage.GetCounterpart(chinese, true));
        Assert.AreEqual(0, GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(oversea))!.SharedFiles.Count);
        Assert.AreEqual(0L, GameServerCoexistenceStorage.GetRequiredDetachBytes(GameServerCoexistenceStorage.GetRoot(oversea)));
    }

    [TestMethod]
    public async Task InterruptedSharingCanBeRecoveredWithoutLosingEitherResource()
    {
        const string first = "StreamingAssets/a.blk";
        const string second = "StreamingAssets/b.blk";
        AddBoth(first, "first", "first");
        AddBoth(second, "second", "second");
        using CancellationTokenSource cancellation = new();
        int reported = 0;
        InlineProgress progress = new(_ =>
        {
            if (++reported == 2)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => GameServerCoexistenceStorage.ShareAsync(chinese, oversea, progress, cancellation.Token));
        Assert.IsNotNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(chinese)));
        Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(Asset(false, first)));

        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(chinese));

        Assert.AreEqual("first", File.ReadAllText(Asset(false, first)));
        Assert.AreEqual("first", File.ReadAllText(Asset(true, first)));
        Assert.AreEqual("second", File.ReadAllText(Asset(false, second)));
        Assert.AreEqual("second", File.ReadAllText(Asset(true, second)));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(false, first)));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(chinese)));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(oversea)));
    }

    [TestMethod]
    public async Task InterruptedDetachRetainsRecoveryJournal()
    {
        const string relative = "StreamingAssets/a.blk";
        AddBoth(relative, "original", "original");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        using CancellationTokenSource cancellation = new();
        InlineProgress progress = new(_ => cancellation.Cancel());
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameServerCoexistenceStorage.DetachAsync(GameServerCoexistenceStorage.GetRoot(oversea), progress, cancellation.Token));

        Assert.IsTrue(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(oversea))!.SharedFiles.Count > 0);
        Assert.AreEqual("original", File.ReadAllText(Asset(false, relative)));
        Assert.AreEqual("original", File.ReadAllText(Asset(true, relative)));
        await GameServerCoexistenceStorage.DetachAsync(GameServerCoexistenceStorage.GetRoot(oversea));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, relative)));
        Assert.IsFalse(Directory.EnumerateFiles(workspace, "*.hutao-private-*", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task MalformedAndEscapingJournalsFailClosed()
    {
        string root = GameServerCoexistenceStorage.GetRoot(chinese);
        string marker = Path.Combine(root, GameServerCoexistenceStorage.StateFileName);
        File.WriteAllText(marker, "{");
        await Assert.ThrowsAsync<JsonException>(() => GameServerCoexistenceStorage.DetachAsync(root));

        GameServerCoexistenceState state = new()
        {
            PairId = Guid.NewGuid(),
            ChineseExecutable = chinese,
            OverseaExecutable = oversea,
            SharedFiles = ["StreamingAssets/../../outside.blk"],
        };
        File.WriteAllText(marker, JsonSerializer.Serialize(state, GameServerCoexistenceJsonContext.Default.GameServerCoexistenceState));
        await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.DetachAsync(root));
    }

    [TestMethod]
    public async Task MissingPeerCannotSilentlyLaunchOrConvertTheCurrentInstallation()
    {
        AddBoth("StreamingAssets/a.blk", "same", "same");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        File.Delete(Path.Combine(GameServerCoexistenceStorage.GetRoot(oversea), GameServerCoexistenceStorage.StateFileName));

        Assert.Throws<InvalidDataException>(() => GameServerCoexistenceStorage.GetCounterpart(chinese, true));
        // Recovery is still possible even if the peer's journal was deleted externally.
        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(chinese));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(false, "StreamingAssets/a.blk")));
    }

    [TestMethod]
    public async Task NestedInstallationsAreRejectedBeforeAnyJournalIsCreated()
    {
        string nested = Path.Combine(GameServerCoexistenceStorage.GetRoot(chinese), "OS", "GenshinImpact.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "fake");
        await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.ShareAsync(chinese, nested));
        Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(chinese)));
    }

    [TestMethod]
    public async Task ReassociationDoesNotLoseRecoveryTracking()
    {
        AddBoth("StreamingAssets/a.blk", "same", "same");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(oversea));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(false, "StreamingAssets/a.blk")));
        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, "StreamingAssets/a.blk")));
        Assert.IsNull(GameServerCoexistenceStorage.GetCounterpart(chinese, true));
    }

    [TestMethod]
    public async Task RecoveryRemovesPreparedSiblingsLeftByProcessTermination()
    {
        const string relative = "StreamingAssets/a.blk";
        AddBoth(relative, "original", "original");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        GameServerCoexistenceState state = GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(oversea))!;
        string pendingCopy = Asset(true, relative) + ".hutao-private-" + state.PairId.ToString("N");
        string pendingLink = Asset(true, relative) + ".hutao-link-" + state.PairId.ToString("N");
        File.WriteAllText(pendingCopy, "incomplete");
        File.WriteAllText(pendingLink, "prepared");

        await GameServerCoexistenceStorage.UnpairAsync(GameServerCoexistenceStorage.GetRoot(oversea));

        Assert.IsFalse(File.Exists(pendingCopy));
        Assert.IsFalse(File.Exists(pendingLink));
        Assert.AreEqual("original", File.ReadAllText(Asset(false, relative)));
        Assert.AreEqual("original", File.ReadAllText(Asset(true, relative)));
    }

    [TestMethod]
    public async Task PackagePauseCheckpointPreventsResourceWritesUntilResumed()
    {
        const string relative = "StreamingAssets/a.blk";
        AddBoth(relative, "original", "original");
        await GameServerCoexistenceStorage.ShareAsync(chinese, oversea);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = GameServerCoexistenceStorage.DetachAsync(
            GameServerCoexistenceStorage.GetRoot(oversea),
            waitForResume: () =>
            {
                entered.TrySetResult();
                return resume.Task;
            });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(operation.IsCompleted);
            Assert.AreEqual(2U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, relative)));
        }
        finally
        {
            resume.TrySetResult();
            await operation;
        }

        Assert.AreEqual(1U, GameServerCoexistenceStorage.GetLinkCount(Asset(true, relative)));
    }

    [TestMethod]
    public async Task DirectoryJunctionsAreRejectedWithoutTouchingTheirTargets()
    {
        string outside = Path.Combine(workspace, "outside");
        string junction = Path.GetFullPath(Asset(false, "StreamingAssets/linked"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "a.blk"), "external");
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", junction, outside })
        {
            start.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, output + error);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => GameServerCoexistenceStorage.ShareAsync(chinese, oversea));
            Assert.AreEqual("external", File.ReadAllText(Path.Combine(outside, "a.blk")));
            Assert.IsNull(GameServerCoexistenceStorage.ReadState(GameServerCoexistenceStorage.GetRoot(chinese)));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    [DataRow("../StreamingAssets/a.blk")]
    [DataRow("StreamingAssets/../a.blk")]
    [DataRow("StreamingAssets/a.blk:stream")]
    [DataRow("Persistent/a.blk")]
    [DataRow("StreamingAssets/Persistent/a.blk")]
    [DataRow("StreamingAssets/Plugins/a.blk")]
    [DataRow("StreamingAssets/a.dll")]
    [DataRow("C:\\StreamingAssets\\a.blk")]
    [DataRow("StreamingAssets//a.blk")]
    public void UnsafeOrMutablePathsCannotBeShared(string path)
    {
        Assert.IsFalse(GameServerCoexistenceStorage.IsShareableRelativePath(path));
    }

    private string Asset(bool os, string relative)
    {
        return Path.Combine(workspace, os ? "OS" : "CN", os ? "GenshinImpact_Data" : "YuanShen_Data", relative);
    }

    private void AddBoth(string relative, string cnContent, string osContent)
    {
        foreach ((bool os, string content) in new[] { (false, cnContent), (true, osContent) })
        {
            string path = Asset(os, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
