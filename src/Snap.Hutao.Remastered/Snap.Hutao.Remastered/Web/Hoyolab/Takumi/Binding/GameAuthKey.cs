// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Web.Hoyolab.Takumi.Binding;

public sealed class GameAuthKey
{
    [JsonPropertyName("authkey")]
    public string AuthKey { get; set; } = default!;

    [JsonPropertyName("authkey_ver")]
    public int AuthKeyVersion { get; set; } = 1;

    [JsonPropertyName("sign_type")]
    public int SignType { get; set; } = 2;
}
