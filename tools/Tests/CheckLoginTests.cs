using RuriLib.Blocks.Hotmail;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Proxies;
using Xunit;

namespace Hotmail.Tests;

// Both test classes mutate TokenEndpointBase/RestApiBase/GraphApiBase/TokenCache, so they
// share one serial collection and xunit runs no two of their tests at once.
[CollectionDefinition("HotmailSerial")]
public sealed class HotmailSerialCollection { }

// TokenEndpointBase, RestApiBase, GraphApiBase and TokenCache are process-wide statics, so
// every test redirects them at its own stub and clears the cache first. The collection keeps
// these tests off GetTokenTests, which mutates the same statics.
[Collection("HotmailSerial")]
public class CheckLoginTests
{
    private const string TokenBody = "{\"access_token\":\"stub-access-token\",\"expires_in\":3600}";
    private const string MeBody = "{\"id\":\"stub-user\"}";
    private const string ErrorBody = "{\"error\":{\"code\":\"ErrorInvalidId\"}}";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Points every endpoint at one loopback stub, empties the token cache, then pre-seeds
    // the cached token GetToken would have exchanged, so the check's single mail-API call
    // is the only request the stub ever records. The API bases carry their version segment
    // (ApiBaseFor returns them verbatim), so recorded paths are full API paths.
    private static void Reset(string stubBase, string flavor = "Rest")
    {
        var root = stubBase.TrimEnd('/');
        HotmailBlocks.TokenEndpointBase = $"{root}/token";
        HotmailBlocks.RestApiBase = $"{root}/api/v2.0";
        HotmailBlocks.GraphApiBase = $"{root}/v1.0";
        TokenCache.Tokens.Clear();
        TokenCache.Set(flavor, "client-id-1", "refresh-token-1",
            new CachedToken("stub-access-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    // 1. Success: exactly one GET against the Rest /me endpoint carrying the exchanged
    //    bearer token, and the 4 line fields come back as the 4 dictionary keys.
    [Fact]
    public async Task Success_returns_the_four_line_fields_and_gets_me_once_with_the_token()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, MeBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var result = await HotmailBlocks.CheckLogin(data, "Rest");

        Assert.Equal("user@example.com", result["email"]);
        Assert.Equal("hunter2", result["password"]);
        Assert.Equal("refresh-token-1", result["refreshToken"]);
        Assert.Equal("client-id-1", result["clientId"]);
        Assert.Equal(4, result.Count);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/api/v2.0/me", req.Path);
        Assert.Contains("Authorization: Bearer stub-access-token", req.RawHeaders,
            StringComparison.OrdinalIgnoreCase);
    }

    // 2. Graph flavor checks /me/messages instead of /me: the token only carries
    //    Mail.ReadWrite consent and /me needs User.Read (ADR-0001 forbids widening the
    //    scope). FakeApi records AbsolutePath only, so the $top=1 cap is asserted through
    //    the log line the block writes.
    [Fact]
    public async Task Graph_flavor_gets_the_messages_path_instead_of_me()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, flavor: "Graph");
        api.Handler = _ => (200, MeBody);

        var logger = new BotLogger();
        BotData data = BotDataFactory.Create(logger);
        await HotmailBlocks.CheckLogin(data, "Graph");

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/v1.0/me/messages", req.Path);
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("/me/messages?$top=1", StringComparison.Ordinal));
    }

    // 3. Non-2xx mail-API answer throws HttpRequestException naming the flavor and status;
    //    the host maps that exception into run status for config branching, so the one
    //    recorded request is the failed GET, never a retry or a swallowed error.
    [Fact]
    public async Task Non_2xx_me_response_throws_an_http_exception_naming_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (400, ErrorBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailBlocks.CheckLogin(data, "Rest"));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/api/v2.0/me", req.Path);
    }

    // 4. Bot proxy on: the check itself rides the proxy. The API is addressed by a
    //    non-loopback-looking name (never bypassed by the HTTP client); FakeProxy records
    //    the CONNECT and rewrites the host onto the loopback stub.
    [Fact]
    public async Task Me_request_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}");
        api.Handler = _ => (200, MeBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        await HotmailBlocks.CheckLogin(data, "Rest");

        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
    }
}
