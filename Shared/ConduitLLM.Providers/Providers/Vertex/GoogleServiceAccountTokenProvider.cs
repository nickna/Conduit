using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using ConduitLLM.Configuration;
using ConduitLLM.Configuration.Utilities;
using ConduitLLM.Core.Exceptions;
using ConduitLLM.Providers.Http;

namespace ConduitLLM.Providers.Vertex;

/// <summary>
/// Exchanges a Google service-account JWT assertion for a short-lived OAuth access token.
/// </summary>
/// <remarks>
/// Tokens are shared across client instances for the same stored credential and refreshed before
/// expiry. The service-account key never leaves this process: only the signed assertion is posted
/// to Google's OAuth token endpoint.
/// </remarks>
internal sealed partial class GoogleServiceAccountTokenProvider
{
    internal const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    internal const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";
    private const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private static readonly ConcurrentDictionary<TokenCacheKey, CachedAccessToken> TokenCache = new();
    private static readonly ConcurrentDictionary<TokenCacheKey, SemaphoreSlim> RefreshLocks = new();

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GoogleServiceAccountCredential _serviceAccount;
    private readonly TokenCacheKey _cacheKey;
    private readonly TimeProvider _timeProvider;

    internal GoogleServiceAccountTokenProvider(
        IHttpClientFactory httpClientFactory,
        int credentialId,
        string serviceAccountJson,
        TimeProvider? timeProvider = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _serviceAccount = ParseCredential(serviceAccountJson);
        _cacheKey = new TokenCacheKey(
            credentialId,
            Sha256Hash.UpperHex(serviceAccountJson));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (TryGetUsableToken(now, out var token))
        {
            return token;
        }

        var refreshLock = RefreshLocks.GetOrAdd(_cacheKey, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (TryGetUsableToken(now, out token))
            {
                return token;
            }

            var assertion = CreateJwtAssertion(_serviceAccount, now);
            using var client = _httpClientFactory.CreateClient(
                ProviderHttpClientNames.Chat(ProviderType.Vertex));
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = JwtBearerGrantType,
                    ["assertion"] = assertion
                })
            };
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var providerMessage = ExtractOAuthError(body)
                    ?? $"{(int)response.StatusCode} ({response.StatusCode})";
                throw new LLMCommunicationException(
                    $"Vertex service-account token exchange failed: {providerMessage} "
                    + $"[HTTP {(int)response.StatusCode}]. Verify the service-account JSON key.",
                    response.StatusCode,
                    body);
            }

            OAuthTokenResponse? tokenResponse;
            try
            {
                tokenResponse = JsonSerializer.Deserialize(
                    body,
                    GoogleServiceAccountJsonContext.Default.OAuthTokenResponse);
            }
            catch (JsonException ex)
            {
                throw new LLMCommunicationException(
                    "Vertex service-account token exchange returned invalid JSON.",
                    response.StatusCode,
                    body,
                    ex);
            }

            if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken)
                || tokenResponse.ExpiresIn <= 0)
            {
                throw new LLMCommunicationException(
                    "Vertex service-account token exchange returned no usable access token.",
                    response.StatusCode,
                    body);
            }

            // Refresh at ten percent of the lifetime (up to five minutes) before expiry. This
            // avoids beginning a long streaming request with a token about to expire.
            var refreshSkewSeconds = Math.Min(300, Math.Max(1, tokenResponse.ExpiresIn / 10));
            var refreshAfter = now.AddSeconds(tokenResponse.ExpiresIn - refreshSkewSeconds);
            TokenCache[_cacheKey] = new CachedAccessToken(tokenResponse.AccessToken, refreshAfter);
            return tokenResponse.AccessToken;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    internal string CreateJwtAssertion(DateTimeOffset issuedAt) =>
        CreateJwtAssertion(_serviceAccount, issuedAt);

    internal static string CreateJwtAssertion(string serviceAccountJson, DateTimeOffset issuedAt) =>
        CreateJwtAssertion(ParseCredential(serviceAccountJson), issuedAt);

    internal static void ResetCacheForTests()
    {
        TokenCache.Clear();
        RefreshLocks.Clear();
    }

    private bool TryGetUsableToken(DateTimeOffset now, out string token)
    {
        if (TokenCache.TryGetValue(_cacheKey, out var cached) && now < cached.RefreshAfter)
        {
            token = cached.AccessToken;
            return true;
        }

        token = string.Empty;
        return false;
    }

    private static string CreateJwtAssertion(
        GoogleServiceAccountCredential serviceAccount,
        DateTimeOffset issuedAt)
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        };
        if (!string.IsNullOrWhiteSpace(serviceAccount.PrivateKeyId))
        {
            header["kid"] = serviceAccount.PrivateKeyId;
        }

        var claims = new Dictionary<string, object>
        {
            ["iss"] = serviceAccount.ClientEmail,
            ["scope"] = CloudPlatformScope,
            ["aud"] = TokenEndpoint,
            ["iat"] = issuedAt.ToUnixTimeSeconds(),
            ["exp"] = issuedAt.AddHours(1).ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            header,
            GoogleServiceAccountJsonContext.Default.DictionaryStringObject));
        var encodedClaims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(
            claims,
            GoogleServiceAccountJsonContext.Default.DictionaryStringObject));
        var unsignedAssertion = $"{encodedHeader}.{encodedClaims}";

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(serviceAccount.PrivateKey);
            var signature = rsa.SignData(
                Encoding.ASCII.GetBytes(unsignedAssertion),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return $"{unsignedAssertion}.{Base64UrlEncode(signature)}";
        }
        catch (CryptographicException ex)
        {
            throw new ConfigurationException(
                "Vertex service_account_json contains an invalid RSA private_key.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new ConfigurationException(
                "Vertex service_account_json contains an invalid RSA private_key.", ex);
        }
    }

    private static GoogleServiceAccountCredential ParseCredential(string serviceAccountJson)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            throw new ConfigurationException(
                "Vertex is missing required configuration: Service Account JSON.");
        }

        GoogleServiceAccountCredential? credential;
        try
        {
            credential = JsonSerializer.Deserialize(
                serviceAccountJson,
                GoogleServiceAccountJsonContext.Default.GoogleServiceAccountCredential);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException(
                "Vertex service_account_json is not a valid JSON document.", ex);
        }

        if (credential == null
            || !string.Equals(credential.Type, "service_account", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(credential.ClientEmail)
            || string.IsNullOrWhiteSpace(credential.PrivateKey))
        {
            throw new ConfigurationException(
                "Vertex service_account_json must be a service-account key containing type, client_email, and private_key.");
        }

        if (!string.IsNullOrWhiteSpace(credential.TokenUri)
            && !string.Equals(credential.TokenUri.TrimEnd('/'), TokenEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConfigurationException(
                $"Vertex service_account_json token_uri must be '{TokenEndpoint}'.");
        }

        return credential;
    }

    private static string? ExtractOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("error_description", out var description)
                && description.ValueKind == JsonValueKind.String)
            {
                return description.GetString();
            }

            if (root.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // The HTTP status remains useful when Google or an intermediary returned non-JSON.
        }

        return null;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private readonly record struct TokenCacheKey(int CredentialId, string CredentialFingerprint);

    private sealed record CachedAccessToken(string AccessToken, DateTimeOffset RefreshAfter);

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }
    }

    private sealed class GoogleServiceAccountCredential
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }

        [JsonPropertyName("client_email")]
        public string ClientEmail { get; init; } = string.Empty;

        [JsonPropertyName("private_key")]
        public string PrivateKey { get; init; } = string.Empty;

        [JsonPropertyName("private_key_id")]
        public string? PrivateKeyId { get; init; }

        [JsonPropertyName("token_uri")]
        public string? TokenUri { get; init; }
    }

    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(OAuthTokenResponse))]
    [JsonSerializable(typeof(GoogleServiceAccountCredential))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(long))]
    private sealed partial class GoogleServiceAccountJsonContext : JsonSerializerContext;
}
