using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuriLib.Attributes;
using RuriLib.Functions.Http;
using RuriLib.Logging;
using RuriLib.Models.Bots;
using HttpMethod = System.Net.Http.HttpMethod;

namespace RuriLib.Blocks.Hotmail;

[BlockCategory("Hotmail", "Blocks for the Hotmail mail service")]
public static class HotmailAttachmentBlocks
{
    [Block("Downloads one attachment's content and returns the raw bytes",
        name = "Download Attachment", id = "HotmailDownloadAttachment")]
    public static async Task<byte[]> DownloadAttachment(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest",
        [BlockParam("message", "Message reference: raw id or a JSON row from List Messages")]
        string message = "",
        [BlockParam("attachment", "Attachment reference: raw id or a JSON row carrying 'id'")]
        string attachment = "")
    {
        var flavor = HotmailBlocks.NormalizeFlavor(api);
        var mid = MessageRef.IdOf(message, "message");
        var aid = MessageRef.IdOf(attachment, "attachment");
        var token = await HotmailBlocks.GetToken(data, api).ConfigureAwait(false);
        var detailUrl = $"{HotmailBlocks.ApiBaseFor(flavor)}/me/messages/{mid}/attachments/{aid}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(HotmailBlocks.RequestTimeout);

        // Same client/timeout/proxy pattern as ListMessages so the download rides the bot's proxy.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());

        var detail = await GetAsync(client, detailUrl, token, timeoutCts.Token,
            data.CancellationToken, flavor).ConfigureAwait(false);
        var (type, content) = ReadDetail(detail, flavor);

        // Both flavors spell @odata.type "...#microsoft.graph.<name>"; the tail after the
        // last '#' is the concrete type. An absent type is treated as unrecognized, which
        // lands on the fileAttachment path below.
        var concrete = TypeTail(type);
        byte[] bytes;

        if (string.Equals(concrete, "referenceAttachment", StringComparison.OrdinalIgnoreCase))
        {
            // A reference attachment is a link to cloud storage, not inline content: neither
            // flavor serves its bytes (Graph answers 405 for /$value), so this throws before
            // any further request rather than burning a call on a known-dead endpoint.
            throw new HttpRequestException(
                $"Attachment {aid} for flavor {flavor} is a referenceAttachment (a link to " +
                "cloud storage): the API does not serve its content.");
        }

        if (concrete.StartsWith("itemAttachment", StringComparison.OrdinalIgnoreCase))
        {
            // Covers itemAttachment and its event/contact/meeting variants. Both flavors fetch
            // item attachment content via /$value; Rest v2.0 does not document /$value, so it
            // is best-effort — a refusal shows up on the non-2xx path below.
            bytes = await GetAsync(client, detailUrl + "/$value", token, timeoutCts.Token,
                data.CancellationToken, flavor).ConfigureAwait(false);
        }
        else if (flavor == "Rest")
        {
            // fileAttachment (and any unrecognized type) carries its payload inline on Rest:
            // base64 under "ContentBytes" in the detail response already fetched — no second
            // request, so /$value is never touched on this path.
            if (content == null)
            {
                throw new HttpRequestException(
                    $"Download attachment for flavor {flavor} failed: the detail response " +
                    $"for {aid} carries no base64 'ContentBytes' property.");
            }

            bytes = Convert.FromBase64String(content);
        }
        else
        {
            // Graph fileAttachment carries its payload inline when the detail response has
            // contentBytes: reuse it and skip the second request. Graph may omit it (large
            // attachments, restricted bodies), then fall back to the /$value endpoint.
            bytes = content != null
                ? Convert.FromBase64String(content)
                : await GetAsync(client, detailUrl + "/$value", token, timeoutCts.Token,
                    data.CancellationToken, flavor).ConfigureAwait(false);
        }

        data.Logger.Log($"{flavor} attachment {aid} {bytes.Length} bytes", LogColors.DeepChampagne);
        return bytes;
    }

    // One GET per request: Bearer token, linked 30s timeout, and non-2xx mapped to
    // HttpRequestException naming flavor, status, and a truncated body — the host turns that
    // exception into a run status for config branching, so it must not be swallowed.
    private static async Task<byte[]> GetAsync(HttpClient client, string url, string token,
        CancellationToken timeoutToken, CancellationToken botToken, string flavor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await client.SendAsync(request, timeoutToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(timeoutToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Download attachment for flavor {flavor} failed with status " +
                    $"{(int)response.StatusCode}: " +
                    $"{(string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content))}");
            }

            // Binary-safe: /$value bodies are raw bytes and are kept verbatim, never decoded.
            return await response.Content.ReadAsByteArrayAsync(timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!botToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Download attachment for flavor {flavor} timed out.");
        }
    }

    // Parses the detail JSON once; both fields may be absent (null). The payload is spelled
    // "ContentBytes" on Rest and camelCase "contentBytes" on Graph, so both casings are tried.
    // It is only meaningful for fileAttachment; the response may legitimately omit it.
    private static (string Type, string Content) ReadDetail(byte[] detail, string flavor)
    {
        try
        {
            using var document = JsonDocument.Parse(detail);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var content = ReadString(root, "ContentBytes") ?? ReadString(root, "contentBytes");
            return (ReadString(root, "@odata.type"), content);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException(
                $"Download attachment for flavor {flavor} failed: the detail response is not " +
                $"JSON ({ex.Message})");
        }
    }

    private static string ReadString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string TypeTail(string type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return string.Empty;
        }

        // Graph spells "@odata.type" as "#microsoft.graph.<name>": one leading '#' and the
        // concrete name after the last dot, so tailing at '#' returns the whole namespace.
        var dot = type.LastIndexOf('.');
        return dot >= 0 ? type[(dot + 1)..] : type;
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}
