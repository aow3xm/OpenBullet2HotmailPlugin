using RuriLib.Blocks.Hotmail;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Proxies;
using Xunit;

namespace Hotmail.Tests;

// TokenEndpointBase, RestApiBase, GraphApiBase and TokenCache are process-wide statics, so
// every test redirects them at its own stub and clears the cache first. The shared serial
// collection (defined in CheckLoginTests.cs) keeps these tests off the sibling test classes,
// which mutate the same statics.
[Collection("HotmailSerial")]
public class MessageDetailsTests
{
    private const string RestMessageBody =
        "{\"Id\":\"msg-1\",\"Subject\":\"Hello\",\"Importance\":\"Normal\",\"Size\":1024," +
        "\"Body\":{\"ContentType\":\"Text\",\"Content\":\"Hi there\"},\"Attachments\":null}";
    private const string GraphMessageBody =
        "{\"id\":\"msg-2\",\"subject\":\"Hello\",\"isRead\":false,\"body\":" +
        "{\"contentType\":\"text\",\"content\":\"Hi there\"},\"uniqueBody\":" +
        "{\"contentType\":\"text\",\"content\":\"Hi there (clean)\"},\"attachments\":[]}";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Points every endpoint at one loopback stub, empties the token cache, then pre-seeds
    // the cached token GetToken would have exchanged, so the detail fetch's single GET is
    // the only request the stub ever records and recorded paths are full API paths (the
    // bases carry the version segment the same way the production defaults do).
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

    // 1. Success: one GET against the Rest detail endpoint; string fields keep Rest's
    //    PascalCase casing verbatim and the body text comes from Body.Content.
    [Fact]
    public async Task Rest_success_returns_pascal_case_fields_and_body_text()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, RestMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var fields = await HotmailMessageBlocks.GetMessageDetails(data, "Rest", "msg-1");

        Assert.Equal("msg-1", fields["Id"]);
        Assert.Equal("Hello", fields["Subject"]);
        Assert.Equal("Normal", fields["Importance"]);
        Assert.Equal("1024", fields["Size"]);
        Assert.Equal("Hi there", fields["bodyText"]);
        Assert.DoesNotContain("Body", fields.Keys);
        Assert.DoesNotContain("Attachments", fields.Keys);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/api/v2.0/me/messages/msg-1", req.Path);
    }

    // 2. Graph flavor: camelCase fields verbatim, and bodyText prefers uniqueBody.content
    //    (Graph's cleaned body) over body.content when both are present.
    [Fact]
    public async Task Graph_success_returns_camel_case_fields_and_prefers_unique_body()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = _ => (200, GraphMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var fields = await HotmailMessageBlocks.GetMessageDetails(data, "Graph", "msg-2");

        Assert.Equal("msg-2", fields["id"]);
        Assert.Equal("Hello", fields["subject"]);
        Assert.Equal("false", fields["isRead"]);
        Assert.Equal("Hi there (clean)", fields["bodyText"]);
        Assert.DoesNotContain("uniqueBody", fields.Keys);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/v1.0/me/messages/msg-2", req.Path);
    }

    // 3. A raw id and a JSON row from List Messages resolve to the same request path, so
    //    configs can pipe the listing straight into this block.
    [Theory]
    [InlineData("msg-9")]
    [InlineData("{\"id\":\"msg-9\",\"subject\":\"A\"}")]
    public async Task Raw_id_and_json_row_resolve_to_the_same_request_path(string reference)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, RestMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMessageBlocks.GetMessageDetails(data, "Rest", reference);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/msg-9", req.Path);
    }

    // 4. Unusable references fail before any HTTP call: MessageRef rejects a JSON row
    //    without a string id, and a JSON body that is not a row at all.
    [Theory]
    [InlineData("{\"subject\":\"no id here\"}")]
    [InlineData("{not json")]
    public async Task Bad_reference_throws_before_any_request(string reference)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, RestMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await Assert.ThrowsAsync<ArgumentException>(
            () => HotmailMessageBlocks.GetMessageDetails(data, "Rest", reference));

        // No request was ever started; the short grace period only gives a regression that
        // fetched before validating time to land in the recorder before we assert it didn't.
        await Task.Delay(300);
        Assert.Empty(api.Requests);
    }

    // 5. Non-2xx answer throws HttpRequestException naming the flavor; the host maps that
    //    exception into run status for config branching, so it must not be swallowed.
    [Fact]
    public async Task Non_2xx_response_throws_an_http_exception_naming_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (404, "{\"error\":{\"code\":\"ErrorItemNotFound\"}}");

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailMessageBlocks.GetMessageDetails(data, "Rest", "msg-1"));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    // 6. 2xx body that is not JSON also throws: an undecodable detail answer is not a
    //    legitimate result for config branching to act on.
    [Fact]
    public async Task Malformed_success_body_throws_an_http_exception()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "<html>not json</html>");

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailMessageBlocks.GetMessageDetails(data, "Rest", "msg-1"));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
    }

    // 7. A 2xx JSON message with neither uniqueBody.content nor body.content also throws:
    //    an empty bodyText would silently mislead a config that filters on the body.
    [Fact]
    public async Task Message_without_a_body_throws_an_http_exception()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{\"id\":\"msg-1\",\"subject\":\"A\"}");

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailMessageBlocks.GetMessageDetails(data, "Rest", "msg-1"));

        Assert.Contains("body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // 8. Bot proxy on: the detail fetch itself rides the proxy. The API is addressed by a
    //    non-loopback-looking name (never bypassed by the HTTP client); FakeProxy records
    //    the CONNECT and rewrites the host onto the loopback stub.
    [Fact]
    public async Task Detail_fetch_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}");
        api.Handler = _ => (200, RestMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        var fields = await HotmailMessageBlocks.GetMessageDetails(data, "Rest", "msg-1");

        Assert.Equal("Hi there", fields["bodyText"]);
        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/msg-1", req.Path);
    }

    // 9. Rest List Messages rows carry PascalCase "Id"; piping one into this block must
    //    resolve the same way a lowercase "id" Graph row does instead of throwing.
    [Fact]
    public async Task Pascal_case_json_row_resolves_to_the_same_request_path()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, RestMessageBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMessageBlocks.GetMessageDetails(data, "Rest",
            "{\"Id\":\"msg-9\",\"Subject\":\"A\"}");

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/msg-9", req.Path);
    }
}
