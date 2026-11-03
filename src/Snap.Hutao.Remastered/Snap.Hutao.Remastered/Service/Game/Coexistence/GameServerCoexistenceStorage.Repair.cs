// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.

using System.IO;

namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public static partial class GameServerCoexistenceStorage
{
    public static long GetRequiredUnlinkRepairBytes(string root)
    {
        GameServerCoexistenceState? state = ReadState(root);
        if (state is null)
        {
            return 0;
        }

        GameServerCoexistenceState? peer = ReadRepairPeer(root, state);
        string data = Path.Combine(root, IsChineseRoot(root, state) ? "YuanShen_Data" : "GenshinImpact_Data");
        long bytes = 0;
        foreach (string relative in state.SharedFiles.Concat(peer?.SharedFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string path = GetAssetPath(data, relative);
            if (File.Exists(path) && GetLinkCount(path) > 1)
            {
                // The peer still occupies these blocks after unlinking this name;
                // repair needs new physical storage for the downloaded replacement.
                bytes = checked(bytes + new FileInfo(path).Length);
            }
        }

        return bytes;
    }

    // User-requested destructive recovery: remove ONLY this client's managed shared
    // names, leave the peer's content intact, then let normal verification redownload.
    public static async Task UnlinkForRepairAsync(string root, IProgress<string>? progress = null, CancellationToken token = default, Func<Task>? waitForResume = null)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            GameServerCoexistenceState? state = ReadState(root);
            if (state is null)
            {
                return;
            }

            GameServerCoexistenceState? peer = ReadRepairPeer(root, state);
            string peerRoot = GetOtherRoot(root, state);
            // Block both launch routes BEFORE removing any file. Keep the journals on
            // cancellation/failure, including a crash between unlinking and disabling.
            WriteState(root, state with { CreationPending = true });
            if (peer is not null)
            {
                WriteState(peerRoot, peer with { CreationPending = true });
            }

            string data = Path.Combine(root, IsChineseRoot(root, state) ? "YuanShen_Data" : "GenshinImpact_Data");
            string peerData = Path.Combine(peerRoot, IsChineseRoot(root, state) ? "GenshinImpact_Data" : "YuanShen_Data");
            foreach (string relative in state.SharedFiles.Concat(peer?.SharedFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (waitForResume is not null)
                {
                    await waitForResume().WaitAsync(token).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                progress?.Report(relative);
                token.ThrowIfCancellationRequested();
                string path = GetAssetPath(data, relative);
                if (File.Exists(path) && GetLinkCount(path) > 1)
                {
                    File.Delete(path);
                }

                // Interrupted creation/detach may have left journaled temporary names.
                foreach (string asset in peer is null ? new[] { path } : new[] { path, GetAssetPath(peerData, relative) })
                {
                    foreach (string suffix in new[] { ".hutao-link-", ".hutao-private-" })
                    {
                        string temporary = asset + suffix + state.PairId.ToString("N");
                        EnsureOrdinaryPath(temporary);
                        File.Delete(temporary);
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            if (peer is not null)
            {
                File.Delete(Path.Combine(peerRoot, StateFileName));
            }

            File.Delete(Path.Combine(root, StateFileName));
        }
        finally
        {
            Gate.Release();
        }
    }

    private static GameServerCoexistenceState? ReadRepairPeer(string root, GameServerCoexistenceState state)
    {
        string peerRoot = GetOtherRoot(root, state);
        GameServerCoexistenceState? peer = Directory.Exists(peerRoot) ? ReadState(peerRoot) : null;
        if (peer is not null && (peer.PairId != state.PairId || !SamePair(state, peer)))
        {
            throw new InvalidDataException("The other installation belongs to a different pair.");
        }

        return peer;
    }
}
