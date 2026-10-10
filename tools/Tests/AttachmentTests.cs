using System.Text;
using RuriLib.Blocks.Hotmail;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using RuriLib.Models.Proxies;
using Xunit;

namespace Hotmail.Tests;

// Same process-wide statics as every other block test class, so the shared serial
// collection (defined in CheckLoginTests.cs) keeps these tests off sibling classes.
[Collection("HotmailSerial")]
public class AttachmentTests
{
    private const string AttachmentId = "a-1";
    private const string MessageId = "m-1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    // Points every endpoint at one loopback stub, empties the token cache, then pre-seeds
    // the cached token GetToken would have exchanged, so the block's GETs are the only
    // requests the stub ever records.
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

    // 1. Rest fileAttachment: detail JSON carries @odata.type and base64 ContentBytes; the
    //    block decodes it in one request and never touches /$value on the Rest file path.
    [Fact]
    public async Task Rest_file_attachment_decodes_content_bytes_without_a_second_request()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200,
            "{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"ContentBytes\":\"AAECAwQ=\"}");

        var bytes = await HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Rest",
            MessageId, AttachmentId);

        Assert.Equal(Convert.FromBase64String("AAECAwQ="), bytes);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("GET", req.Method);
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1", req.Path);
    }

    // 2. Graph fileAttachment: detail first (for the type), then raw bytes off /$value, in
    //    that order.
    [Fact]
    public async Task Graph_file_attachment_fetches_detail_then_value_in_order()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = url => url.EndsWith("$value", StringComparison.Ordinal)
            ? (200, "AAECAwQ=")
            : (200, "{\"@odata.type\":\"#microsoft.graph.fileAttachment\"}");

        var bytes = await HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Graph",
            MessageId, AttachmentId);

        Assert.Equal(Encoding.ASCII.GetBytes("AAECAwQ="), bytes);

        var reqs = await api.WaitForRequestsAsync(2, Wait);
        Assert.Equal("/v1.0/me/messages/m-1/attachments/a-1", reqs[0].Path);
        Assert.Equal("/v1.0/me/messages/m-1/attachments/a-1/$value", reqs[1].Path);
    }

    // 3. Graph itemAttachment: content rides /$value too.
    [Fact]
    public async Task Graph_item_attachment_downloads_via_value()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = url => url.EndsWith("$value", StringComparison.Ordinal)
            ? (200, "item-bytes")
            : (200, "{\"@odata.type\":\"#microsoft.graph.itemAttachment\"}");

        var bytes = await HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Graph",
            MessageId, AttachmentId);

        Assert.Equal(Encoding.ASCII.GetBytes("item-bytes"), bytes);

        var reqs = await api.WaitForRequestsAsync(2, Wait);
        Assert.Equal("/v1.0/me/messages/m-1/attachments/a-1", reqs[0].Path);
        Assert.Equal("/v1.0/me/messages/m-1/attachments/a-1/$value", reqs[1].Path);
    }

    // 4. Rest itemAttachment: same /$value route as Graph (undocumented on Rest v2.0,
    //    best-effort), path asserts the Rest base is used.
    [Fact]
    public async Task Rest_item_attachment_downloads_via_value()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = url => url.EndsWith("$value", StringComparison.Ordinal)
            ? (200, "item-bytes")
            : (200, "{\"@odata.type\":\"#microsoft.graph.itemAttachment\"}");

        var bytes = await HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Rest",
            MessageId, AttachmentId);

        Assert.Equal(Encoding.ASCII.GetBytes("item-bytes"), bytes);

        var reqs = await api.WaitForRequestsAsync(2, Wait);
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1", reqs[0].Path);
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1/$value", reqs[1].Path);
    }

    // 5. referenceAttachment is a cloud link, not inline content: the block refuses after the
    //    detail GET and before any content request (exactly 1 request recorded).
    [Fact]
    public async Task Reference_attachment_throws_naming_the_type_before_fetching_content()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200,
            "{\"@odata.type\":\"#microsoft.graph.referenceAttachment\"}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Rest",
                MessageId, AttachmentId));

        Assert.Contains("referenceAttachment", ex.Message, StringComparison.Ordinal);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1", req.Path);
    }

    // 6. Attachment reference accepts a raw id and a JSON row carrying "id" alike; both land
    //    on the same path.
    [Theory]
    [InlineData("a-1")]
    [InlineData("{\"id\":\"a-1\",\"name\":\"f.bin\"}")]
    public async Task Attachment_reference_accepts_a_raw_id_or_a_json_row(string attachment)
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200,
            "{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"ContentBytes\":\"AAECAwQ=\"}");

        await HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Rest", MessageId, attachment);

        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1", req.Path);
    }

    // 7. Rest fileAttachment without ContentBytes has no bytes to return: a clear error, not
    //    a silent empty result.
    [Fact]
    public async Task Rest_file_attachment_without_content_bytes_throws_a_clear_error()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl);
        api.Handler = _ => (200, "{\"@odata.type\":\"#microsoft.graph.fileAttachment\"}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Rest",
                MessageId, AttachmentId));

        Assert.Contains("ContentBytes", ex.Message, StringComparison.Ordinal);
    }

    // 8. Non-2xx on the detail GET throws HttpRequestException naming the flavor, so the host
    //    can map it into run status for config branching.
    [Fact]
    public async Task Non_2xx_detail_response_throws_an_http_exception_naming_the_flavor()
    {
        using var api = new FakeApi();
        Reset(api.BaseUrl, "Graph");
        api.Handler = _ => (404, "{\"error\":{\"code\":\"ErrorItemNotFound\"}}");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => HotmailAttachmentBlocks.DownloadAttachment(NewData(), "Graph",
                MessageId, AttachmentId));

        Assert.Contains("Graph", ex.Message, StringComparison.Ordinal);
        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
    }

    // 9. Bot proxy on: the download rides the proxy. The API is addressed by a non-loopback
    //    name (never bypassed by the HTTP client); FakeProxy records the CONNECT and rewrites
    //    the host onto the loopback stub.
    [Fact]
    public async Task Download_goes_through_the_bot_proxy_when_the_proxy_is_on()
    {
        using var proxy = new FakeProxy();
        using var api = new FakeApi();
        Reset($"http://stub.invalid:{api.Port}");
        api.Handler = _ => (200,
            "{\"@odata.type\":\"#microsoft.graph.fileAttachment\",\"ContentBytes\":\"AAECAwQ=\"}");

        var data = NewData();
        data.UseProxy = true;
        data.Proxy = new Proxy("127.0.0.1", proxy.Port, ProxyType.Socks5);
        proxy.RewriteHost = $"127.0.0.1:{api.Port}";

        var bytes = await HotmailAttachmentBlocks.DownloadAttachment(data, "Rest",
            MessageId, AttachmentId);

        Assert.Equal(Convert.FromBase64String("AAECAwQ="), bytes);
        Assert.Equal($"stub.invalid:{api.Port}", proxy.ConnectTarget);
        var req = Assert.Single(await api.WaitForRequestsAsync(1, Wait));
        Assert.Equal("/api/v2.0/me/messages/m-1/attachments/a-1", req.Path);
    }
}
