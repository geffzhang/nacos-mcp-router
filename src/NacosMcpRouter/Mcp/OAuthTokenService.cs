using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace NacosMcpRouter.Mcp;

public sealed record OAuthOptions(
    string Flow,
    string? ClientId,
    string? ClientSecret,
    string? TokenUrl,
    string? AuthorizationUrl,
    string? DeviceAuthorizationUrl,
    string? AuthorizationCode,
    string? DeviceCode,
    IReadOnlyList<string> Scopes);

public sealed class OAuthAuthorizationRequiredException : Exception
{
    public OAuthAuthorizationRequiredException(string message, string? verificationUri = null, string? userCode = null)
        : base(message)
    {
        VerificationUri = verificationUri;
        UserCode = userCode;
    }

    public string? VerificationUri { get; }
    public string? UserCode { get; }
}

public sealed class OAuthTokenService
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);

    public OAuthTokenService(HttpClient httpClient) => _httpClient = httpClient;

    public async ValueTask<string> GetAccessTokenAsync(OAuthOptions options, CancellationToken cancellationToken = default)
    {
        var key = CreateCacheKey(options);
        if (_tokens.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return cached.AccessToken;
        }

        var token = await RequestTokenAsync(options, cancellationToken).ConfigureAwait(false);
        _tokens[key] = token;
        return token.AccessToken;
    }

    private async Task<CachedToken> RequestTokenAsync(OAuthOptions options, CancellationToken cancellationToken)
    {
        if (string.Equals(options.Flow, "device_code", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(options.DeviceCode))
        {
            return await BeginDeviceAuthorizationAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(options.Flow, "authorization_code", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(options.AuthorizationCode))
        {
            throw new OAuthAuthorizationRequiredException(
                $"OAuth authorization required. Open {options.AuthorizationUrl}, then configure the returned authorizationCode.",
                options.AuthorizationUrl);
        }

        if (string.IsNullOrWhiteSpace(options.TokenUrl))
        {
            throw new InvalidOperationException("OAuth tokenUrl is required");
        }

        var form = new Dictionary<string, string>();
        switch (options.Flow.ToLowerInvariant())
        {
            case "client_credentials":
                form["grant_type"] = "client_credentials";
                break;
            case "authorization_code":
                form["grant_type"] = "authorization_code";
                form["code"] = options.AuthorizationCode!;
                break;
            case "device_code":
                form["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code";
                form["device_code"] = options.DeviceCode!;
                break;
            default:
                throw new NotSupportedException($"unsupported OAuth flow '{options.Flow}'");
        }

        if (!string.IsNullOrWhiteSpace(options.ClientId)) form["client_id"] = options.ClientId;
        if (!string.IsNullOrWhiteSpace(options.ClientSecret)) form["client_secret"] = options.ClientSecret;
        if (options.Scopes.Count > 0) form["scope"] = string.Join(' ', options.Scopes);

        using var response = await _httpClient.PostAsync(options.TokenUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        return await ParseTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CachedToken> BeginDeviceAuthorizationAsync(OAuthOptions options, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.DeviceAuthorizationUrl))
        {
            throw new InvalidOperationException("OAuth deviceAuthorizationUrl is required");
        }

        var form = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(options.ClientId)) form["client_id"] = options.ClientId;
        if (options.Scopes.Count > 0) form["scope"] = string.Join(' ', options.Scopes);
        using var response = await _httpClient.PostAsync(options.DeviceAuthorizationUrl, new FormUrlEncodedContent(form), cancellationToken).ConfigureAwait(false);
        var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var verificationUri = payload.TryGetProperty("verification_uri_complete", out var complete)
            ? complete.GetString()
            : payload.TryGetProperty("verification_uri", out var uri) ? uri.GetString() : null;
        var userCode = payload.TryGetProperty("user_code", out var code) ? code.GetString() : null;
        throw new OAuthAuthorizationRequiredException(
            $"OAuth device authorization required. Open {verificationUri} and enter {userCode}, then configure deviceCode.",
            verificationUri,
            userCode);
    }

    private static async Task<CachedToken> ParseTokenResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        if (!payload.TryGetProperty("access_token", out var accessToken) || string.IsNullOrWhiteSpace(accessToken.GetString()))
        {
            throw new InvalidOperationException("OAuth token response did not contain access_token");
        }
        var expiresIn = payload.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? seconds : 300;
        return new CachedToken(accessToken.GetString()!, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"OAuth endpoint returned {(int)response.StatusCode}: {body}");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string CreateCacheKey(OAuthOptions options) => string.Join('|', options.Flow, options.ClientId, options.TokenUrl, options.AuthorizationCode, options.DeviceCode, string.Join(' ', options.Scopes));

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}

internal sealed class OAuthBearerHandler(OAuthTokenService tokenService, OAuthOptions options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenService.GetAccessTokenAsync(options, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}