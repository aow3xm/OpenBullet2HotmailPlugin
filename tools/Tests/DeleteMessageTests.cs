using RuriLib.Blocks.Hotmail;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Proxies;
using Xunit;

namespace Hotmail.Tests;

// Same process-wide statics as every other block test class, so the shared serial
// collection (defined in CheckLoginTests.cs) keeps these tests off sibling classes.
[Collection("HotmailSerial")]
public class DeleteMessageTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Points every endpoint at one loopback stub, empties the token cache, then pre-seeds
    // the cached token GetToken would have exchanged, so the block's requests are the only
    // ones the stub ever records and recorded paths are full API paths.
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

    private static BotData NewData()
        => BotDataFactory.Create(new BotLogger());

    // 1. Soft delete without the permanent flag is one move to DeletedItems, never a DELETE,
    //    and the id the move returns is what the config gets back to work with.
    [Fact]
    public async Task Rest_soft_delete_moves_to_deleted_items_without_a_delete_request()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{\"Id\":\"moved-1\"}");

        var id = await HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "msg-1");

        Assert.Equal("moved-1", id);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("POST", req.Method);
        Assert.Equal("/api/v2.0/me/messages/msg-1/move", req.Path);
        // The move body names the destination folder; ParseForm keeps the JSON intact as a
        // single key because it contains no '&'.
        Assert.Contains("{\"DestinationId\":\"DeletedItems\"}", req.Form.Keys);
        Assert.DoesNotContain(api.Requests, r => r.Method == "DELETE");
    }

    // 2. Graph soft delete is the same move with camelCase body and the Graph base path,
    //    returning the new id from the lowercase "id" field.
    [Fact]
    public async Task Graph_soft_delete_moves_to_deleted_items_in_one_request()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = _ => (200, "{\"id\":\"moved-2\"}");

        var id = await HotmailDeleteBlocks.DeleteMessage(NewData(), "Graph", "msg-2");

        Assert.Equal("moved-2", id);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1.0/me/messages/msg-2/move", req.Path);
        // Same ParseForm single-key convention as the Rest test above: a JSON body with
        // no '&' or '=' is recorded intact as one Form key.
        Assert.Contains("{\"destinationId\":\"DeletedItems\"}", req.Form.Keys);
    }

    // 3. Rest permanent delete is two steps: move first, then DELETE the id the move
    //    returned — deleting the old id would 404 once the message has moved away.
    [Fact]
    public async Task Rest_permanent_delete_moves_then_deletes_the_new_id()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = url => url.EndsWith("/move", StringComparison.Ordinal)
            ? (201, "{\"Id\":\"new-id-1\"}")
            : (204, "");

        var id = await HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "old-id",
            permanent: true);

        Assert.Equal("new-id-1", id);
        var reqs = await api.WaitForRequestsAsync(2, Wait);
        Assert.Equal(2, reqs.Count);
        Assert.Equal("POST", reqs[0].Method);
        Assert.Equal("/api/v2.0/me/messages/old-id/move", reqs[0].Path);
        Assert.Equal("DELETE", reqs[1].Method);
        Assert.Equal("/api/v2.0/me/messages/new-id-1", reqs[1].Path);
    }

    // 4. Graph permanent delete is a single permanentDelete call and reports the original
    //    id, since Graph returns no body to parse a new id from.
    [Fact]
    public async Task Graph_permanent_delete_is_a_single_permanent_delete_call()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = _ => (204, "");

        var id = await HotmailDeleteBlocks.DeleteMessage(NewData(), "Graph", "msg-3",
            permanent: true);

        Assert.Equal("msg-3", id);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("POST", req.Method);
        Assert.Equal("/v1.0/me/messages/msg-3/permanentDelete", req.Path);
    }

    // 5. A raw id and a JSON row from List Messages resolve to the same request path, so
    //    configs can pipe the listing straight into this block.
    [Theory]
    [InlineData("msg-9")]
    [InlineData("{\"id\":\"msg-9\",\"subject\":\"A\"}")]
    public async Task Raw_id_and_json_row_resolve_to_the_same_move_path(string reference)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{\"Id\":\"moved-9\"}");

        await HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", reference);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/msg-9/move", req.Path);
    }

    // 6. Non-2xx answer throws HttpRequestException naming the flavor, the status and the
    //    body so the host maps it into run status for config branching.
    [Fact]
    public async Task Non_2xx_response_throws_an_http_exception_naming_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (404, "{\"error\":{\"code\":\"ErrorItemNotFound\"}}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "msg-1"));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ErrorItemNotFound", ex.Message, StringComparison.Ordinal);
    }

    // 7. Bot proxy on: the delete rides the proxy. The API is addressed by a non-loopback
    //    name (never bypassed by the HTTP client); FakeProxy records the CONNECT and rewrites
    //    the host onto the loopback stub.
    [Fact]
    public async Task Delete_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}");
        api.Handler = _ => (200, "{\"Id\":\"moved-1\"}");

        var data = NewData();
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        var id = await HotmailDeleteBlocks.DeleteMessage(data, "Rest", "msg-1");

        Assert.Equal("moved-1", id);
        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/msg-1/move", req.Path);
    }

    // 8. A failed move aborts the permanent sequence before the DELETE: the DELETE would
    //    target the old id and 404 once the message never moved away.
    [Fact]
    public async Task Rest_permanent_delete_aborts_when_the_move_fails()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (500, "{\"error\":{\"code\":\"ErrorMoveFailed\"}}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "msg-1", permanent: true));

        Assert.DoesNotContain(api.Requests, r => r.Method == "DELETE");
    }

    // 9. A 2xx move whose body carries no parseable id aborts the sequence too, rather
    //    than handing a bogus id to the DELETE.
    [Fact]
    public async Task Rest_move_that_returns_no_id_throws_instead_of_handing_a_bogus_id()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "msg-1", permanent: true));

        Assert.Contains("no string id", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Requests, r => r.Method == "DELETE");
    }

    // 10. Non-2xx DELETE surfaces as HttpRequestException naming the flavor, the status
    //     and the body, same as the move failure above.
    [Fact]
    public async Task Rest_permanent_delete_surfaces_a_failed_delete_with_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = url => url.EndsWith("/move", StringComparison.Ordinal)
            ? (201, "{\"Id\":\"new-id-1\"}")
            : (502, "{\"error\":{\"code\":\"ServerError\"}}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            HotmailDeleteBlocks.DeleteMessage(NewData(), "Rest", "old-id", permanent: true));

        Assert.Contains("Rest", ex.Message, StringComparison.Ordinal);
        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ServerError", ex.Message, StringComparison.Ordinal);
    }
}
