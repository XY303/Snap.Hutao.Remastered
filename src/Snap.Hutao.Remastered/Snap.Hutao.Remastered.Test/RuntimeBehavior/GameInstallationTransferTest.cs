using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.IO;
using System.Threading;

namespace Snap.Hutao.Remastered.Test.RuntimeBehavior;

[TestClass]
public sealed class GameInstallationTransferTest
{
    [TestMethod]
    public async Task TransferIncludesUserFilesAndCleanupKeepsVerifiedCopy()
    {
        using Fixture fixture = new();
        await GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, CancellationToken.None);
        foreach (string relative in fixture.Files)
        {
            Assert.AreEqual(File.ReadAllText(fixture.AtSource(relative)), File.ReadAllText(fixture.AtTarget(relative)));
        }
        File.SetAttributes(fixture.AtSource("config.ini"), FileAttributes.ReadOnly);
        await GameInstallationTransfer.DeleteVerifiedSourceAsync(fixture.Source, fixture.Target, null, CancellationToken.None);
        foreach (string relative in fixture.Files)
        {
            Assert.IsFalse(File.Exists(fixture.AtSource(relative)));
            Assert.IsTrue(File.Exists(fixture.AtTarget(relative)));
        }
    }

    [TestMethod]
    public async Task ChangedCopyPreventsAnySourceDeletion()
    {
        using Fixture fixture = new();
        await GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, CancellationToken.None);
        File.WriteAllText(fixture.AtTarget("config.ini"), "changed");
        await Assert.ThrowsAsync<IOException>(() => GameInstallationTransfer.DeleteVerifiedSourceAsync(fixture.Source, fixture.Target, null, CancellationToken.None));
        foreach (string relative in fixture.Files) { Assert.IsTrue(File.Exists(fixture.AtSource(relative))); }
    }

    [TestMethod]
    public async Task CancelledTransferDoesNotCreateDestinationOrDeleteSource()
    {
        using Fixture fixture = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Target)));
        foreach (string relative in fixture.Files) { Assert.IsTrue(File.Exists(fixture.AtSource(relative))); }
    }

    [TestMethod]
    public async Task CancellationBeforeCleanupPreservesBothCopies()
    {
        using Fixture fixture = new();
        await GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameInstallationTransfer.DeleteVerifiedSourceAsync(fixture.Source, fixture.Target, null, cancellation.Token));
        foreach (string relative in fixture.Files)
        {
            Assert.IsTrue(File.Exists(fixture.AtSource(relative)));
            Assert.IsTrue(File.Exists(fixture.AtTarget(relative)));
        }
    }

    [TestMethod]
    public async Task ExistingAndNestedDestinationsAreRejected()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Target)!);
        File.WriteAllText(fixture.Target, "existing");
        await Assert.ThrowsAsync<IOException>(() => GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, CancellationToken.None));
        Assert.AreEqual("existing", File.ReadAllText(fixture.Target));
        string nested = Path.Combine(Path.GetDirectoryName(fixture.Source)!, "nested", "YuanShen.exe");
        await Assert.ThrowsAsync<IOException>(() => GameInstallationTransfer.CopyAsync(fixture.Source, nested, null, CancellationToken.None));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(nested)));
    }

    [TestMethod]
    public async Task SourceChangesDuringCopyFailFinalVerification()
    {
        using Fixture fixture = new();
        string? first = null;
        int count = 0;
        InlineProgress progress = new(value =>
        {
            if (!value.StartsWith("复制并校验：", StringComparison.Ordinal)) { return; }
            count++;
            if (count == 1) { first = value[6..]; }
            if (count == 2) { File.WriteAllText(fixture.AtSource(first!), "changed during copy"); }
        });
        await Assert.ThrowsAsync<IOException>(() => GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, progress, CancellationToken.None));
        foreach (string relative in fixture.Files) { Assert.IsTrue(File.Exists(fixture.AtSource(relative))); }
    }

    [TestMethod]
    public async Task DestinationChangeAfterPreflightCannotDeleteSource()
    {
        using Fixture fixture = new();
        await GameInstallationTransfer.CopyAsync(fixture.Source, fixture.Target, null, CancellationToken.None);
        InlineProgress progress = new(value =>
        {
            if (value.StartsWith("清理原文件：", StringComparison.Ordinal)) { File.WriteAllText(fixture.AtTarget(value[6..]), "changed after preflight"); }
        });
        await Assert.ThrowsAsync<IOException>(() => GameInstallationTransfer.DeleteVerifiedSourceAsync(fixture.Source, fixture.Target, progress, CancellationToken.None));
        foreach (string relative in fixture.Files) { Assert.IsTrue(File.Exists(fixture.AtSource(relative))); }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hutao-transfer-" + Guid.NewGuid().ToString("N"));
        public string[] Files { get; } = ["YuanShen.exe", "config.ini", "ScreenShot/photo.png", "chunks/cache.bin", "YuanShen_Data/Persistent/ScriptVersion"];
        public string Source => AtSource("YuanShen.exe");
        public string Target => AtTarget("YuanShen.exe");
        public string AtSource(string relative) => Path.Combine(root, "source", relative);
        public string AtTarget(string relative) => Path.Combine(root, "destination", relative);
        public Fixture()
        {
            foreach (string relative in Files)
            {
                string path = AtSource(relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relative + " content");
            }
        }
        public void Dispose()
        {
            // This fixture exclusively owns the generated temp tree; no links are created.
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(file, FileAttributes.Normal); }
            Directory.Delete(root, true);
        }
    }
}
