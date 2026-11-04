// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

namespace Snap.Hutao.Remastered.Web.Hoyolab.Passport;

public sealed class STokenExchangeResult
{
    [JsonPropertyName("tokens")]
    public List<TokenWrapper> Tokens { get; set; } = [];
}
