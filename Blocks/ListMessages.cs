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
public static class HotmailMailBlocks
{
    [Block("Lists messages in a mailbox folder and returns one JSON row per message",
        name = "List Messages", id = "HotmailListMessages")]
    public static async Task<List<string>> ListMessages(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest",
        [BlockParam("folder", "Folder id, well-known name (Inbox, DeletedItems, Archive, Drafts, SentItems, JunkEmail, RecoverableItems*), or all")]
        string folder = "Inbox")
    {
        var flavor = HotmailBlocks.NormalizeFlavor(api);
        var token = await HotmailBlocks.GetToken(data, api).ConfigureAwait(false);
        var path = FolderPath(flavor, folder);
        var url = $"{HotmailBlocks.ApiBaseFor(flavor)}{path}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(HotmailBlocks.RequestTimeout);

        // Same client/timeout/proxy pattern as GetToken so the listing rides the bot's proxy.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());
        var rows = new List<string>();
        var page = 0;
        var nextUrl = url;

        // Follows @odata.nextLink until a page omits it (both flavors speak its name); the
        // only bound is the single 30s CancelAfter above, so a huge mailbox fails loud as TimeoutException.
        while (nextUrl != null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, nextUrl);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

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
                        $"List messages for flavor {flavor} failed with status {status}: " +
                        $"{(string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content))}");
                }
            }
            catch (OperationCanceledException) when (!data.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"List messages for flavor {flavor} timed out.");
            }

            List<string> pageRows;
            string next = null;
            try
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                pageRows = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.Array
                    ? ToRows(value)
                    : null;
                next = root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("@odata.nextLink", out var link)
                    && link.ValueKind == JsonValueKind.String
                    ? link.GetString()
                    : null;
            }
            catch (JsonException)
            {
                pageRows = null;
            }

            if (pageRows == null)
            {
                throw new HttpRequestException(
                    $"List messages for flavor {flavor} page {page} returned status {status} " +
                    "but the body carries no JSON array under a top-level 'value'.");
            }

            rows.AddRange(pageRows);
            nextUrl = string.IsNullOrEmpty(next) ? null : next;
            page++;
        }

        data.Logger.Log($"{flavor} {path} {rows.Count} rows", LogColors.DeepChampagne);
        return rows;
    }

    // Folder names are matched case-insensitively but spelled the way the flavor's own docs
    // spell them: TitleCase with lowercase "mailfolders" for Rest, lowercase names with
    // camelCase "mailFolders" for Graph; "all" skips the folder segment entirely. Anything
    // else is a raw folder id and passes through verbatim (ids are case-sensitive).
    private static string FolderPath(string flavor, string folder)
    {
        if (string.Equals(folder, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "/me/messages";
        }

        return flavor == "Rest"
            ? $"/me/mailfolders/{RestFolderName(folder)}/messages"
            : $"/me/mailFolders/{GraphFolderName(folder)}/messages";
    }

    private static string RestFolderName(string folder)
    {
        var key = folder?.ToLowerInvariant() ?? "inbox";
        return key switch
        {
            "archive" => "Archive",
            "deleteditems" => "DeletedItems",
            "drafts" => "Drafts",
            "inbox" => "Inbox",
            "junkemail" => "JunkEmail",
            "sentitems" => "SentItems",
            "recoverableitemsdeletions" => "RecoverableItemsDeletions",
            "recoverableitemspurges" => "RecoverableItemsPurges",
            _ => folder,
        };
    }

    private static string GraphFolderName(string folder)
    {
        var key = folder?.ToLowerInvariant() ?? "inbox";
        return key switch
        {
            "inbox" or "drafts" or "sentitems" or "deleteditems" or "archive"
                or "junkemail" or "recoverableitemsdeletions" or "recoverableitemspurges"
                => key,
            _ => folder,
        };
    }

    // Raw JSON text per element, so a later block can read any field out of the row itself.
    private static List<string> ToRows(JsonElement value)
    {
        var rows = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            rows.Add(element.GetRawText());
        }

        return rows;
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}
