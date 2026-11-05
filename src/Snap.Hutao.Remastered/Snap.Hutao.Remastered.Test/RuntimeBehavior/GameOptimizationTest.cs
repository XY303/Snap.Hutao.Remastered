using Snap.Hutao.Remastered.Service.Game.Coexistence;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace Snap.Hutao.Remastered.Test.RuntimeBehavior;

[TestClass]
public sealed class GameOptimizationTest
{
    [TestMethod]
    [DataRow(false, false, 119999U, false)]
    [DataRow(false, false, 120000U, true)]
    [DataRow(false, true, 0U, false)]
    [DataRow(true, false, 900000U, false)]
    [DataRow(true, true, 0U, true)]
    public void AutomaticOptimizationHonorsIdleAndGamePolicy(bool running, bool allowGame, uint idle, bool expected)
        => Assert.AreEqual(expected, GameOptimizationWorker.ShouldOptimize(running, allowGame, idle));

    [TestMethod]
    [DataRow("C:/game/YuanShen_Data/StreamingAssets/blocks/a.blk", true)]
    [DataRow("C:/game/GenshinImpact_Data/StreamingAssets/Audio/a.PCK", true)]
    [DataRow("C:/game/YuanShen_Data/Persistent/a.blk", false)]
    [DataRow("C:/game/YuanShen_Data/StreamingAssets/Plugins/a.dll", false)]
    public void BackgroundOptimizationOnlyTargetsResourceData(string path, bool expected)
        => Assert.AreEqual(expected, GameSpaceOptimizer.IsBackgroundCandidate(path));

    [TestMethod]
    public async Task BackgroundHashStaysOnWorkerAndHonorsCancellation()
    {
        byte[] data = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 1);
        byte[] actual = await GameOptimizationWorker.RunAsync(() =>
        {
            Assert.IsFalse(Thread.CurrentThread.IsThreadPoolThread);
            Assert.AreEqual(ThreadPriority.Lowest, Thread.CurrentThread.Priority);
            int thread = Environment.CurrentManagedThreadId;
            using MemoryStream stream = new(data);
            Task<byte[]> hash = GameOptimizationWorker.HashAsync(stream, true, CancellationToken.None);
            Assert.IsTrue(hash.IsCompletedSuccessfully);
            Assert.AreEqual(thread, Environment.CurrentManagedThreadId);
            return hash.GetAwaiter().GetResult();
        });
        CollectionAssert.AreEqual(SHA256.HashData(data), actual);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => GameOptimizationWorker.RunAsync(() =>
        {
            using MemoryStream stream = new(data);
            return GameOptimizationWorker.HashAsync(stream, true, cancellation.Token).GetAwaiter().GetResult();
        }));
    }
}
