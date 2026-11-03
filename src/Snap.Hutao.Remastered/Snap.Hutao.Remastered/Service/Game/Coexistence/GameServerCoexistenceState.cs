// Copyright (c) Snap Hutao RP. All rights reserved.
// Licensed under the MIT license.


namespace Snap.Hutao.Remastered.Service.Game.Coexistence;

public sealed record GameServerCoexistenceState
{
    public int Format { get; init; } = 1;

    public required Guid PairId { get; init; }

    public required string ChineseExecutable { get; init; }

    public required string OverseaExecutable { get; init; }

    // Paths are relative to each installation's _Data directory, never absolute.
    // Written BEFORE linking, so interrupted operations can still be detached.
    public required List<string> SharedFiles { get; init; }

    public bool CreationPending { get; init; }

    // Format 2 uses independent ReFS files inside this managed VHDX.
    public string? VirtualDiskImage { get; init; }
}

[JsonSerializable(typeof(GameServerCoexistenceState))]
[JsonSerializable(typeof(GameVirtualDiskInfo))]
internal sealed partial class GameServerCoexistenceJsonContext : JsonSerializerContext
{
}
