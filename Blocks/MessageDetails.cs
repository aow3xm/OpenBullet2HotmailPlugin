using System;
using System.Collections.Generic;
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
public static class HotmailMessageBlocks
{
    [Block("Fetches one message's details and returns them as field-name/value pairs including bodyText",
        name = "Get Message Details", id = "HotmailGetMessageDetails")]
    public static async Task<Dictionary<string, string>> GetMessageDetails(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest",
        [BlockParam("message", "Message reference: raw id or a JSON row from List Messages")]
        string message = "")
    {
        var flavor = HotmailBlocks.NormalizeFlavor(api);
        var id = Hotmail.MessageRef.IdOf(message, "message");
        var token = await HotmailBlocks.GetToken(data, api).ConfigureAwait(false);

        // The id goes into the URL verbatim, the same way List Messages passes raw folder
        // ids through: ids are opaque API-issued strings, not user text needing escapes.
        var path = $"/me/messages/{id}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(HotmailBlocks.RequestTimeout);

        // Same client/timeout/proxy pattern as ListMessages so the fetch rides the bot's proxy.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{HotmailBlocks.ApiBaseFor(flavor)}{path}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");

        int status;
        string content;
        try
        {
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The host maps this exception into run status for config branching, so the
                // message must name flavor, status, and a truncated body.
                throw new HttpRequestException(
                    $"Get message details for flavor {flavor} failed with status {status}: " +
                    $"{(string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content))}");
            }
        }
        catch (OperationCanceledException) when (!data.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Get message details for flavor {flavor} timed out.");
        }

        Dictionary<string, string> fields;
        string bodyText;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                fields = null;
                bodyText = null;
            }
            else
            {
                fields = ReadFields(document.RootElement);
                bodyText = ReadBodyText(document.RootElement);
            }
        }
        catch (JsonException)
        {
            // A 2xx answer that is not JSON is not a legitimate result for config branching
            // to act on, so it fails like List Messages' malformed success body.
            throw new HttpRequestException(
                $"Get message details for flavor {flavor} returned status {status} " +
                "but the body is not JSON.");
        }

        if (fields == null)
        {
            throw new HttpRequestException(
                $"Get message details for flavor {flavor} returned status {status} " +
                "but the body is not a JSON message object.");
        }

        // Both flavors carry the body under an object with a "content" string; Graph fills
        // uniqueBody with the cleaned-up version while Rest omits it, so prefer it. Neither
        // present means the response was not a full message, which is worth failing on
        // loudly rather than handing the config an empty body.
        if (bodyText == null)
        {
            throw new HttpRequestException(
                $"Get message details for flavor {flavor} returned status {status} " +
                "but the message body carries neither uniqueBody.content nor body.content.");
        }

        fields["bodyText"] = bodyText;
        data.Logger.Log($"{flavor} {path} {fields.Count} fields", LogColors.DeepChampagne);
        return fields;
    }

    // Top-level string properties keep the flavor's own casing verbatim (Rest PascalCase,
    // Graph camelCase) so configs address fields exactly as the API spells them. Numbers
    // and booleans become invariant-culture strings; objects, arrays, and null have no
    // string value to hand a config and are skipped.
    private static Dictionary<string, string> ReadFields(JsonElement root)
    {
        var fields = new Dictionary<string, string>();
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    fields[property.Name] = property.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    fields[property.Name] = property.Value.GetRawText();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    fields[property.Name] = property.Value.GetBoolean()
                        ? "true" : "false";
                    break;
            }
        }

        return fields;
    }

    // uniqueBody.content when present (Graph's cleaned body), otherwise body.content; null
    // when the message carries neither. Rest v2.0 PascalCases every name on the wire
    // ("Body", "Content"), Graph camelCases them ("body", "content"), so nested lookups
    // match case-insensitively instead of encoding one flavor's spelling.
    private static string ReadBodyText(JsonElement root)
    {
        foreach (var (container, leaf) in new[] { ("uniqueBody", "content"), ("body", "content") })
        {
            if (TryGetPropertyIgnoreCase(root, container, out var body)
                && body.ValueKind == JsonValueKind.Object
                && TryGetPropertyIgnoreCase(body, leaf, out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }

        return null;
    }

    // JsonElement.TryGetProperty is ordinal case-sensitive; the flavors spell the same JSON
    // members with different casing (Rest "Body"/"Content", Graph "body"/"content").
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}
