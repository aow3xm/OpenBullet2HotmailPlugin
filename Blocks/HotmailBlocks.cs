using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
public static class HotmailBlocks
{
    internal static string TokenEndpointBase = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    // Decommission announced for 03/2024, but live-measured 200/201/204 on consumer accounts
    // 2026-10-10 (ADR-0002). If it ever shuts off for real, switch configs to api = Graph
    // per ADR-0001 — no runtime fallback on purpose.
    internal static string RestApiBase = "https://outlook.office.com/api/v2.0";
    internal static string GraphApiBase = "https://graph.microsoft.com/v1.0";
    internal static TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [Block("Exchanges the refresh token from the input line for an access token",
        name = "Get Token", id = "HotmailGetToken")]
    public static async Task<string> GetToken(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest")
    {
        var (refreshToken, clientId) = ParseLine(data.Line.Data);
        var flavor = NormalizeFlavor(api);
        var scope = ScopeFor(flavor);

        if (TokenCache.TryGetValid(flavor, clientId, refreshToken, out var cached))
        {
            return cached;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        // HttpFactory wires the bot's proxy and TLS options, matching the built-in HTTP blocks.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpointBase);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = scope,
        });

        int status;
        string content;
        try
        {
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!data.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The token request for flavor {flavor} timed out.");
        }

        var success = status is >= 200 and < 300;
        var (error, errorDescription) = ReadError(content);

        // Microsoft reports OAuth failures both as non-2xx statuses and as 2xx bodies with an
        // "error" key (invalid_grant AADSTS70000, invalid_scope AADSTS65001, measured 2026-10-10),
        // so both shapes must fail. Error text names flavor and scope because, per ADR-0001, the
        // config author picked the flavor and only they can fix a scope mismatch.
        if (!success || error != null)
        {
            var detail = error != null
                ? $"{error}{(string.IsNullOrEmpty(errorDescription) ? "" : $": {errorDescription}")}"
                : string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content);
            throw new HttpRequestException(
                $"Token exchange failed for flavor {flavor} with scope {scope} (status {status}): {detail}");
        }

        var (accessToken, expiresIn) = ReadToken(content, flavor, scope, status);

        TokenCache.Set(flavor, clientId, refreshToken,
            new CachedToken(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn)));
        data.Logger.Log($"token {flavor} {status}", LogColors.DeepChampagne);
        return accessToken;
    }

    // Exactly 4 non-empty ':'-separated fields; the line itself is never echoed because it
    // carries the password and refresh token.
    private static (string RefreshToken, string ClientId) ParseLine(string line)
    {
        var fields = SplitLine(line);
        return (fields[2], fields[3]);
    }

    private static string[] SplitLine(string line)
    {
        var fields = (line ?? string.Empty).Split(':');

        if (fields.Length != 4)
        {
            throw new InvalidOperationException(
                $"The input line must have exactly 4 ':'-separated fields " +
                $"(email:password:refreshToken:clientId) but has {fields.Length}.");
        }

        if (Array.Exists(fields, string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                "Every input line field must be non-empty: email:password:refreshToken:clientId.");
        }

        return fields;
    }

    [Block("Checks the account by calling the mail API once with the exchanged access token",
        name = "Check Login", id = "HotmailCheckLogin")]
    public static async Task<Dictionary<string, string>> CheckLogin(BotData data,
        [BlockParam("api", "API flavor: Rest or Graph")] string api = "Rest")
    {
        var fields = SplitLine(data.Line.Data);
        var flavor = NormalizeFlavor(api);

        // Token cache applies: a second CheckLogin in the same run reuses the token and
        // still makes its own single mail-API call.
        var accessToken = await GetToken(data, api).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(data.CancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        // Same client/timeout/proxy pattern as GetToken so the check rides the bot's proxy.
        using var client = HttpFactory.GetHttpClient(data.UseProxy ? data.Proxy : null,
            new HttpOptions(), new CookieContainer());
        var path = CheckPath(flavor);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBaseFor(flavor)}{path}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        int status;
        try
        {
            using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // The host maps this exception into run status for config branching, so the
                // message must name flavor, status, and a truncated body.
                throw new HttpRequestException(
                    $"The mail API check for flavor {flavor} failed with status {status}: " +
                    $"{(string.IsNullOrWhiteSpace(content) ? "(empty body)" : Truncate(content))}");
            }
        }
        catch (OperationCanceledException) when (!data.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The mail API check for flavor {flavor} timed out.");
        }

        data.Logger.Log($"{path} {flavor} {status}", LogColors.DeepChampagne);
        return new Dictionary<string, string>
        {
            ["email"] = fields[0],
            ["password"] = fields[1],
            ["refreshToken"] = fields[2],
            ["clientId"] = fields[3],
        };
    }

    internal static string ApiBaseFor(string flavor)
        => flavor == "Rest" ? RestApiBase : GraphApiBase;

    // Rest checks /me (live-verified, ADR-0002). Graph's token only carries Mail.ReadWrite
    // consent and /me needs User.Read, so the Graph check rides an endpoint the granted
    // scope does cover (ADR-0001) instead of asking for a wider scope.
    private static string CheckPath(string flavor)
        => flavor == "Rest" ? "/me" : "/me/messages?$top=1";

    // No auto-probing, no fallback between flavors: ADR-0001.
    internal static string NormalizeFlavor(string api)
    {
        if (string.Equals(api, "Rest", StringComparison.OrdinalIgnoreCase))
        {
            return "Rest";
        }

        if (string.Equals(api, "Graph", StringComparison.OrdinalIgnoreCase))
        {
            return "Graph";
        }

        throw new InvalidOperationException(
            $"Invalid api value '{api}': the only valid values are 'Rest' and 'Graph'. " +
            "The plugin never probes or falls back between flavors.");
    }

    private static string ScopeFor(string flavor)
        => flavor == "Rest"
            ? "https://outlook.office.com/Mail.ReadWrite"
            : "https://graph.microsoft.com/Mail.ReadWrite";

    private static (string AccessToken, long ExpiresIn) ReadToken(
        string content, string flavor, string scope, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var accessToken = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String
                ? tokenElement.GetString()
                : null;

            string expiresRaw = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("expires_in", out var expiresElement))
            {
                expiresRaw = expiresElement.ValueKind == JsonValueKind.Number
                    ? expiresElement.GetRawText()
                    : expiresElement.ValueKind == JsonValueKind.String
                        ? expiresElement.GetString()
                        : null;
            }

            if (string.IsNullOrEmpty(accessToken))
            {
                throw new HttpRequestException(
                    $"Token exchange for flavor {flavor} with scope {scope} returned status {status} " +
                    "but the body carries no string access_token.");
            }

            if (!long.TryParse(expiresRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresIn))
            {
                throw new HttpRequestException(
                    $"Token exchange for flavor {flavor} with scope {scope} returned status {status} " +
                    "but the body carries no usable expires_in.");
            }

            return (accessToken, expiresIn);
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException(
                $"Token exchange for flavor {flavor} with scope {scope} returned status {status} " +
                $"with a body that is not valid token JSON ({ex.Message}).");
        }
    }

    // Top-level "error" / "error_description" of an OAuth error body; (null, null) for
    // anything else, including non-JSON bodies.
    private static (string Error, string Description) ReadError(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var error = root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.String
                ? errorElement.GetString()
                : null;
            var description = root.TryGetProperty("error_description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;
            return (error, description);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}

internal sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);

internal static class TokenCache
{
    // Tokens within 60s of expiry are treated as expired so a token never goes stale
    // mid-request while in flight through the proxy.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(60);

    internal static readonly ConcurrentDictionary<(string Flavor, string ClientId, string RefreshToken), CachedToken>
        Tokens = new();

    // Sweep removes only expired entries; a full cache of fresh one-shot keys is cleared
    // and rebuilt (the just-set token re-inserted) so memory stays bounded.
    private const int PruneThreshold = 1024;

    internal static bool TryGetValid(string flavor, string clientId, string refreshToken, out string accessToken)
    {
        accessToken = null;

        var key = (flavor, clientId, refreshToken);
        if (!Tokens.TryGetValue(key, out var token))
        {
            return false;
        }

        if (token.ExpiresAt - ExpiryMargin > DateTimeOffset.UtcNow)
        {
            accessToken = token.AccessToken;
            return true;
        }

        // Remove only if this exact expired entry is still current; a concurrent
        // Set of a fresh token must survive.
        Tokens.TryRemove(KeyValuePair.Create(key, token));
        return false;
    }

    internal static void Set(string flavor, string clientId, string refreshToken, CachedToken token)
    {
        var key = (flavor, clientId, refreshToken);
        Tokens[key] = token;

        if (Tokens.Count >= PruneThreshold && PruneExpired() == 0)
        {
            Tokens.Clear();
            Tokens[key] = token;
        }
    }

    // Returns the number of expired entries removed.
    private static int PruneExpired()
    {
        var removed = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in Tokens)
        {
            if (entry.Value.ExpiresAt - ExpiryMargin <= now && Tokens.TryRemove(entry))
            {
                removed++;
            }
        }

        return removed;
    }
}
