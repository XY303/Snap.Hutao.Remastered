// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Web.Hoyolab.Passport;

public sealed class STokenExchangeRequest
{
    [JsonPropertyName("dst_token_types")]
    public int[] DestinationTokenTypes { get; set; } = [4];
}
