using RuriLib.Blocks.Hotmail;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Proxies;
using Xunit;

namespace Hotmail.Tests;

// TokenEndpointBase and TokenCache are process-wide statics, so every test redirects them
// at its own stub and clears the cache first. Keeping all tests in this one class also makes
// xunit run them sequentially, so no test can steal another's endpoint or cached token.
public class GetTokenTests
{
    private const string RestScope = "https://outlook.office.com/Mail.ReadWrite";
    private const string GraphScope = "https://graph.microsoft.com/Mail.ReadWrite";
    private const string TokenBody = "{\"access_token\":\"stub-access-token\",\"expires_in\":3600}";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Non-2xx AADSTS shape measured 2026-10-10: on an MSA account invalid_grant (AADSTS70000)
    // is also what a scope the (user, client id) pair never consented to comes back as.
    private const string Aadsts70000Body =
        """
        {"error":"invalid_grant","error_description":"AADSTS70000: Refresh token is invalid."}
        """;

    // 2xx-body-with-"error"-key shape, also measured 2026-10-10; AADSTS65001 is the
    // documented unconsented-scope code.
    private const string Aadsts65001Body =
        """
        {"error":"invalid_scope","error_description":"AADSTS65001: The user or administrator has not consented to the application."}
        """;

    private static void Reset(string tokenEndpoint)
    {
        HotmailBlocks.TokenEndpointBase = tokenEndpoint;
        TokenCache.Tokens.Clear();
    }

    // 1. Rest flavor returns the stub's access token; the recorded request is the exact
    //    refresh_token grant against the Rest scope.
    [Fact]
    public async Task Rest_flavor_returns_the_stub_token_and_posts_a_refresh_grant()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, TokenBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var token = await HotmailBlocks.GetToken(data, "Rest");

        Assert.Equal("stub-access-token", token);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("POST", req.Method);
        Assert.Equal("refresh_token", req.Form["grant_type"]);
        Assert.Equal("client-id-1", req.Form["client_id"]);
        Assert.Equal("refresh-token-1", req.Form["refresh_token"]);
        Assert.Equal(RestScope, req.Form["scope"]);
    }

    // 2. Graph flavor swaps in the Graph scope, nothing else.
    [Fact]
    public async Task Graph_flavor_requests_the_graph_scope()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, TokenBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var token = await HotmailBlocks.GetToken(data, "Graph");

        Assert.Equal("stub-access-token", token);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal(GraphScope, req.Form["scope"]);
    }

    // 3. Same (flavor, client id, refresh token) twice -> one stub request total; changing
    //    any single element of the tuple -> the stub is hit again, so the key is the full tuple.
    [Fact]
    public async Task Cache_key_is_the_full_flavor_client_refresh_tuple()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, TokenBody);

        var logger = new BotLogger();
        BotData data = BotDataFactory.Create(logger);

        await HotmailBlocks.GetToken(data, "Rest");
        await HotmailBlocks.GetToken(data, "Rest");
        Assert.Single(api.Requests);

        await HotmailBlocks.GetToken(data, "Graph");
        Assert.Equal(2, api.Requests.Count);

        BotData otherClient = BotDataFactory.Create(logger, clientId: "client-id-2");
        await HotmailBlocks.GetToken(otherClient, "Rest");
        Assert.Equal(3, api.Requests.Count);

        BotData otherRefresh = BotDataFactory.Create(logger, refreshToken: "refresh-token-2");
        await HotmailBlocks.GetToken(otherRefresh, "Rest");
        Assert.Equal(4, api.Requests.Count);
    }

    // 4. Bot proxy on: the token exchange itself rides the proxy. The endpoint is addressed
    //    by a non-loopback-looking name (never bypassed by the HTTP client); FakeProxy records
    //    the CONNECT and rewrites the host onto the loopback stub.
    [Fact]
    public async Task Token_exchange_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}/");
        api.Handler = _ => (200, TokenBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        var token = await HotmailBlocks.GetToken(data);

        Assert.Equal("stub-access-token", token);
        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("POST", req.Method);
    }

    // 4b. Contrast: proxy off reaches the same stub directly, with no CONNECT recorded.
    [Fact]
    public async Task Token_exchange_reaches_the_stub_directly_without_a_proxy()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, TokenBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        data.UseProxy = false;

        var token = await HotmailBlocks.GetToken(data);

        Assert.Equal("stub-access-token", token);
        Assert.Single(api.Requests);
    }

    // 5. Non-2xx AADSTS error: the message must name the flavor and the exact requested
    //    scope, because per ADR-0001 only the config author can fix a flavor/scope mismatch.
    [Theory]
    [InlineData("Rest", RestScope)]
    [InlineData("Graph", GraphScope)]
    public async Task Non_2xx_aadsts_error_names_flavor_and_requested_scope(string flavor, string scope)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (400, Aadsts70000Body);

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailBlocks.GetToken(data, flavor));

        Assert.Contains(flavor, ex.Message, StringComparison.Ordinal);
        Assert.Contains(scope, ex.Message, StringComparison.Ordinal);
        Assert.Contains("AADSTS70000", ex.Message, StringComparison.Ordinal);
    }

    // 5b. Microsoft also reports OAuth failures as a 2xx body with an "error" key
    //     (measured 2026-10-10); that shape must fail with the same flavor/scope message.
    [Theory]
    [InlineData("Rest", RestScope)]
    [InlineData("Graph", GraphScope)]
    public async Task Error_keyed_2xx_body_names_flavor_and_requested_scope(string flavor, string scope)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, Aadsts65001Body);

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailBlocks.GetToken(data, flavor));

        Assert.Contains(flavor, ex.Message, StringComparison.Ordinal);
        Assert.Contains(scope, ex.Message, StringComparison.Ordinal);
        Assert.Contains("AADSTS65001", ex.Message, StringComparison.Ordinal);
    }

    // 6. A failing Graph request is exactly one request against the Graph scope: no probe,
    //    no second attempt against the Rest scope or any other path (ADR-0001).
    [Fact]
    public async Task Graph_error_records_exactly_one_request_and_never_falls_back_to_rest()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (400, Aadsts70000Body);

        BotData data = BotDataFactory.Create(new BotLogger());
        await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailBlocks.GetToken(data, "Graph"));

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal(GraphScope, req.Form["scope"]);
        Assert.Single(api.Requests);
    }
}
