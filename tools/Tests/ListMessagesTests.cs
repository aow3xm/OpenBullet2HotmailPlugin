using System.Text.Json;
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
public class ListMessagesTests
{
    private const string MessagesBody =
        "{\"value\":[{\"id\":\"msg-1\",\"subject\":\"A\"},{\"id\":\"msg-2\",\"subject\":\"B\"}]}";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Points every endpoint at one loopback stub, empties the token cache, then pre-seeds
    // the cached token GetToken would have exchanged, so the listing's single GET is the
    // only request the stub ever records and recorded paths are full API paths (the bases
    // carry the version segment the same way the production defaults do).
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

    // 1. Success: one raw JSON row per message; each row parses on its own, so a later block
    //    can read a field out of the row and still has the whole row verbatim.
    [Fact]
    public async Task Rest_success_returns_one_raw_json_row_per_message()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        var rows = await HotmailMailBlocks.ListMessages(data);

        Assert.Equal(2, rows.Count);
        using var row1 = JsonDocument.Parse(rows[0]);
        Assert.Equal("msg-1", row1.RootElement.GetProperty("id").GetString());
        using var row2 = JsonDocument.Parse(rows[1]);
        Assert.Equal("msg-2", row2.RootElement.GetProperty("id").GetString());
        Assert.Equal("{\"id\":\"msg-1\",\"subject\":\"A\"}", rows[0]);
        Assert.Equal("{\"id\":\"msg-2\",\"subject\":\"B\"}", rows[1]);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/api/v2.0/me/mailfolders/Inbox/messages", req.Path);
    }

    // 2. Rest well-known name: case-insensitive input, TitleCase segment and lowercase
    //    "mailfolders" per the Rest docs.
    [Fact]
    public async Task Rest_well_known_folder_uses_the_titlecase_mailfolders_segment()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMailBlocks.ListMessages(data, "Rest", "DeletedItems");

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/mailfolders/DeletedItems/messages", req.Path);
    }

    // 3. "all" lists the whole mailbox (no folder segment), any casing.
    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    public async Task Rest_all_folder_lists_the_whole_mailbox_regardless_of_casing(string folder)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMailBlocks.ListMessages(data, "Rest", folder);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages", req.Path);
    }

    // 4. Graph well-known name: lowercase folder name and camelCase "mailFolders" per the
    //    Graph docs.
    [Fact]
    public async Task Graph_well_known_folder_uses_the_lowercase_camel_mailFolders_segment()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMailBlocks.ListMessages(data, "Graph", "Inbox");

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/v1.0/me/mailFolders/inbox/messages", req.Path);
    }

    // 5. Anything that is not a well-known name is a raw folder id: passed through verbatim,
    //    no validation rejects it and no casing is applied to it.
    [Fact]
    public async Task Raw_folder_id_passes_through_verbatim_without_validation()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        await HotmailMailBlocks.ListMessages(data, "Rest", "AAMkAG...xyz");

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/mailfolders/AAMkAG...xyz/messages", req.Path);
    }

    // 6. Non-2xx answer throws HttpRequestException naming the flavor; the host maps that
    //    exception into run status for config branching, so it must not be swallowed.
    [Fact]
    public async Task Non_2xx_response_throws_an_http_exception_naming_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (400, "{\"error\":{\"code\":\"ErrorInvalidId\"}}");

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailMailBlocks.ListMessages(data));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    }

    // 7. 2xx body without a JSON array under top-level "value" also throws: an empty or
    //    malformed listing is not a legitimate result for config branching to act on.
    [Fact]
    public async Task Malformed_success_body_throws_an_http_exception()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{\"oops\": true}");

        BotData data = BotDataFactory.Create(new BotLogger());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailMailBlocks.ListMessages(data));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
    }

    // 8. Bot proxy on: the listing itself rides the proxy. The API is addressed by a
    //    non-loopback-looking name (never bypassed by the HTTP client); FakeProxy records
    //    the CONNECT and rewrites the host onto the loopback stub.
    [Fact]
    public async Task Listing_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}");
        api.Handler = _ => (200, MessagesBody);

        BotData data = BotDataFactory.Create(new BotLogger());
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        var rows = await HotmailMailBlocks.ListMessages(data);

        Assert.Equal(2, rows.Count);
        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/mailfolders/Inbox/messages", req.Path);
    }

    // 9. Pagination: first page carries @odata.nextLink, so the block GETs that absolute URL
    //    too and returns both pages' rows concatenated; the second page omits the link, ending
    //    the loop (the fake records path only, so a per-call counter separates the two GETs).
    [Fact]
    public async Task Next_link_pages_until_a_page_omits_it_and_rows_concatenate()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        var pageOne = "{\"value\":[{\"id\":\"msg-1\",\"subject\":\"A\"}]," +
            $"\"@odata.nextLink\":\"http://127.0.0.1:{api.Port}/api/v2.0/me/mailfolders/Inbox/messages?page=2\"}}";
        var pageTwo = "{\"value\":[{\"id\":\"msg-2\",\"subject\":\"B\"}]}";
        var calls = 0;
        api.Handler = _ => ++calls == 1 ? (200, pageOne) : (200, pageTwo);

        BotData data = BotDataFactory.Create(new BotLogger());
        var rows = await HotmailMailBlocks.ListMessages(data);

        Assert.Equal(2, rows.Count);
        Assert.Contains("msg-1", rows[0]);
        Assert.Contains("msg-2", rows[1]);

        var reqs = await api.WaitForRequestsAsync(2, Wait);
        Assert.All(reqs, r => Assert.Equal("GET", r.Method));
        Assert.Equal("/api/v2.0/me/mailfolders/Inbox/messages", reqs[1].Path);
    }
}
