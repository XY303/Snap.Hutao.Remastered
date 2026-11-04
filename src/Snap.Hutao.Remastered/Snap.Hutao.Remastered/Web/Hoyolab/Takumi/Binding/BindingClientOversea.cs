// Copyright (c) DGP Studio. All rights reserved.
// Licensed under the MIT license.

using Snap.Hutao.Remastered.Core.DependencyInjection.Annotation.HttpClient;
using Snap.Hutao.Remastered.Model.Entity;
using Snap.Hutao.Remastered.Web.Endpoint.Hoyolab;
using Snap.Hutao.Remastered.Web.Hoyolab.DataSigning;
using Snap.Hutao.Remastered.Web.Hoyolab.Passport;
using Snap.Hutao.Remastered.Web.Request.Builder;
using Snap.Hutao.Remastered.Web.Request.Builder.Abstraction;
using Snap.Hutao.Remastered.Web.Response;
using System.Net.Http;

namespace Snap.Hutao.Remastered.Web.Hoyolab.Takumi.Binding;

[HttpClient(HttpClientConfiguration.XRpc3)]
[PrimaryHttpMessageHandler(UseCookies = false)]
public sealed partial class BindingClientOversea
{
    private readonly IHttpRequestMessageBuilderFactory httpRequestMessageBuilderFactory;
    [FromKeyed(ApiEndpointsKind.Oversea)]
    private readonly IApiEndpoints apiEndpoints;
    private readonly HttpClient httpClient;

    [GeneratedConstructor]
    public partial BindingClientOversea(IServiceProvider serviceProvider, HttpClient httpClient);

    public async ValueTask<Response<GameAuthKey>> GenerateAuthenticationKeyAsync(User user, GenAuthKeyData data, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(user.Aid) || string.IsNullOrEmpty(user.Mid))
        {
            return new(Response.Response.publicFailure, SH.ServiceGachaLogUrlProviderAuthkeyRequestFailed, default);
        }

        // Legacy cookie_token values cannot be used as cookie_token_v2.
        string? cookieToken = user.CookieToken?.GetValueOrDefault(Cookie.COOKIE_TOKEN_V2);
        if (string.IsNullOrEmpty(cookieToken))
        {
            if (string.IsNullOrEmpty(user.SToken?.GetValueOrDefault(Cookie.STOKEN)))
            {
                return new(Response.Response.publicFailure, SH.ServiceUserProcessCookieNoSToken, default);
            }

            // Tokens issued by the built-in HoYoPlay login are bound to its app ID.
            // Imported HoYoLAB tokens require the HoYoLAB app ID instead.
            Response<STokenExchangeResult> tokenResponse = await ExchangeCookieTokenAsync(user, HoyolabOptions.HoyoPlayAppIdOversea, token).ConfigureAwait(false);
            if (tokenResponse.ReturnCode == (int)KnownReturnCode.RET_TOKEN_INVALID)
            {
                tokenResponse = await ExchangeCookieTokenAsync(user, HoyolabOptions.HoyolabAppIdOversea, token).ConfigureAwait(false);
            }

            if (tokenResponse.ReturnCode != 0)
            {
                return Response.Response.CloneReturnCodeAndMessage<GameAuthKey, STokenExchangeResult>(tokenResponse);
            }

            cookieToken = tokenResponse.Data?.Tokens?.FirstOrDefault(value => value.TokenType == 4)?.Token;
            if (string.IsNullOrEmpty(cookieToken))
            {
                return new(Response.Response.publicFailure, SH.ServiceGachaLogUrlProviderAuthkeyRequestFailed, default);
            }
        }

        Cookie cookie = new()
        {
            [Cookie.COOKIE_TOKEN_V2] = cookieToken,
            [Cookie.ACCOUNT_ID_V2] = user.Aid,
            [Cookie.ACCOUNT_MID_V2] = user.Mid,
        };

        HttpRequestMessageBuilder builder = httpRequestMessageBuilderFactory.Create()
            .SetRequestUri(apiEndpoints.BindingGenAuthKeyByCookieToken())
            .SetHeader("Cookie", cookie.ToString())
            .PostJson(data);

        Response<GameAuthKey>? response = await builder
            .SendAsync<Response<GameAuthKey>>(httpClient, token)
            .ConfigureAwait(false);

        return Response.Response.DefaultIfNull(response);
    }

    private async ValueTask<Response<STokenExchangeResult>> ExchangeCookieTokenAsync(User user, string appId, CancellationToken token)
    {
        HttpRequestMessageBuilder builder = httpRequestMessageBuilderFactory.Create()
            .SetRequestUri(apiEndpoints.AccountGetV2TokenBySToken())
            .SetUserCookieAndFpHeader(user, CookieType.SToken)
            .SetHeader("x-rpc-app_id", appId)
            .PostJson(new STokenExchangeRequest());

        await builder.SignDataAsync(DataSignAlgorithmVersion.Gen1, SaltType.AppLogin, true).ConfigureAwait(false);

        Response<STokenExchangeResult>? response = await builder
            .SendAsync<Response<STokenExchangeResult>>(httpClient, token)
            .ConfigureAwait(false);

        return Response.Response.DefaultIfNull(response);
    }
}
