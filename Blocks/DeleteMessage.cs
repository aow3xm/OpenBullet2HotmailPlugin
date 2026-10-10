using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
public static class HotmailDeleteBlocks
{
    [Block("Moves the message to Deleted Items, or deletes it permanently when permanent is set",
        name = "Delete Message", id = "HotmailDeleteMessage")]
    public static async Task<string> DeleteMessage(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest",
        [BlockParam("message", "Message reference: raw id or a JSON row from List Messages")]
        string message = "",
        [BlockParam("permanent", "True deletes permanently, false (default) moves to Deleted Items")]
        bool permanent = false)
    {
        var flavor = HotmailBlocks.NormalizeFlavor(api);
        var id = Hotmail.MessageRef.IdOf(message, "message");
        var token = await HotmailBlocks.GetToken(data, api).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(HotmailBlocks.RequestTimeout);

        // Same client/timeout/proxy pattern as MessageDetails so the delete rides the bot's
        // proxy. The one client serves every request this block makes, so the Rest permanent
        // path reuses the same connection for its move and DELETE.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());

        if (permanent && flavor == "Graph")
        {
            // ADR-0004: Graph documents permanentDelete as one request straight into purges,
            // and the message never changed mailbox here, so the original id stays valid —
            // no move, no id to re-read. DELETE is never issued directly: on this API it
            // deletes outright (measured 2026-10-10) rather than moving to Deleted Items.
            var permanentPath = $"/me/messages/{id}/permanentDelete";
            await SendAsync(client, $"{HotmailBlocks.GraphApiBase}{permanentPath}",
                HttpMethod.Post, null, token, timeoutCts.Token, data, flavor, permanentPath,
                "permanent delete", returnsId: false).ConfigureAwait(false);
            return id;
        }

        // The recoverable path, and the first step of Rest permanent: POST .../move with the
        // well-known DestinationId "DeletedItems" — the only operation that lands the
        // message in Deleted Items, where a later block can still fetch it (ADR-0004).
        // Property casing follows the flavor: Rest v2.0 documents PascalCase "DestinationId",
        // Graph spells it camelCase; the value is DeletedItems on both.
        var moveBody = flavor == "Rest"
            ? "{\"DestinationId\":\"DeletedItems\"}"
            : "{\"destinationId\":\"DeletedItems\"}";
        var movePath = $"/me/messages/{id}/move";
        var newId = await SendAsync(client, $"{HotmailBlocks.ApiBaseFor(flavor)}{movePath}",
            HttpMethod.Post, moveBody, token, timeoutCts.Token, data, flavor, movePath,
            "move to Deleted Items", returnsId: true).ConfigureAwait(false);

        if (!permanent)
        {
            return newId;
        }

        // ADR-0004: Rest has no permanentDelete endpoint, so the outright delete is a second
        // request — DELETE by the id the move returned. Microsoft changes the id when the
        // message changes mailbox (documented for Graph, measured on Rest v2.0 2026-10-10);
        // the old id would 404 here. Two requests total on Rest permanent.
        var deletePath = $"/me/messages/{newId}";
        await SendAsync(client, $"{HotmailBlocks.RestApiBase}{deletePath}",
            HttpMethod.Delete, null, token, timeoutCts.Token, data, flavor, deletePath,
            "permanent delete", returnsId: false).ConfigureAwait(false);
        return newId;
    }

    // One request per call: Bearer token, linked 30s timeout, non-2xx mapped to
    // HttpRequestException naming flavor, operation, status, and a truncated body — the host
    // turns that exception into a run status for config branching, so it must not be
    // swallowed. With returnsId the response must be a JSON object carrying a string id
    // (Rest "Id", Graph "id", matched case-insensitively): that id is the message's new id
    // after the mailbox change, and every later operation must use it (ADR-0004), so a 2xx
    // without one fails loudly instead of handing configs a bogus id. The DELETE answer
    // carries no id, so its caller ignores the return value.
    private static async Task<string> SendAsync(HttpClient client, string url,
        HttpMethod method, string jsonBody, string token, CancellationToken timeoutToken,
        BotData data, string flavor, string path, string operation, bool returnsId)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (jsonBody != null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        int status;
        string content;
        try
        {
            using var response = await client.SendAsync(request, timeoutToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            content = await response.Content.ReadAsStringAsync(timeoutToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{operation} for flavor {flavor} failed with status {status}: " +
                    $"{(string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content))}");
            }
        }
        catch (OperationCanceledException) when (!data.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{operation} for flavor {flavor} timed out.");
        }

        data.Logger.Log($"{flavor} {path} {status}", LogColors.DeepChampagne);

        if (!returnsId)
        {
            return null;
        }

        if (!TryReadId(content, out var id))
        {
            throw new HttpRequestException(
                $"{operation} for flavor {flavor} returned status {status} " +
                "but the body carries no string id.");
        }

        return id;
    }

    // Rest answers a move with PascalCase "Id", Graph with camelCase "id"; the id itself is
    // an opaque API-issued string, so match the property case-insensitively. Non-JSON and
    // non-object bodies (and an empty DELETE answer) are simply "no id".
    private static bool TryReadId(string content, out string id)
    {
        id = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(property.Value.GetString()))
                {
                    id = property.Value.GetString();
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}
