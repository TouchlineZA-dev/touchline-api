using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Touchline.Api.Users;

/// <summary>
/// Caches the Supabase project's public signing keys from
/// {url}/auth/v1/.well-known/jwks.json and refreshes them hourly, or sooner when a
/// token arrives with an unknown key id (key rotation). Refreshes are throttled.
/// </summary>
public sealed class SupabaseSigningKeys
{
    public const string HttpClientName = "supabase-jwks";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinGapBetweenFetches = TimeSpan.FromMinutes(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SupabaseSigningKeys> _logger;
    private readonly string _jwksUrl;
    private readonly SecurityKey? _legacyKey;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private volatile IReadOnlyList<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    private readonly TimeProvider _time;

    public SupabaseSigningKeys(
        IHttpClientFactory httpClientFactory,
        IOptions<SupabaseAuthOptions> options,
        ILogger<SupabaseSigningKeys> logger,
        TimeProvider time)
    {
        _time = time;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _jwksUrl = options.Value.JwksUrl;

        if (!string.IsNullOrWhiteSpace(options.Value.JwtSecret))
        {
            _legacyKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.JwtSecret));
        }
    }

    /// <summary>Used as TokenValidationParameters.IssuerSigningKeyResolver (synchronous by design).</summary>
    public IEnumerable<SecurityKey> GetKeys(string? kid)
    {
        var now = _time.GetUtcNow();
        var stale = now - _fetchedAt > CacheFor;
        var unknownKid = kid is not null && _keys.All(k => k.KeyId != kid);

        if ((stale || unknownKid) && now - _lastAttempt > MinGapBetweenFetches)
        {
            // Blocks only on refresh (hourly or on rotation); ASP.NET Core has no
            // synchronization context, so this does not deadlock.
            RefreshAsync().GetAwaiter().GetResult();
        }

        return _legacyKey is null ? _keys : _keys.Append(_legacyKey);
    }

    private async Task RefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (_time.GetUtcNow() - _lastAttempt <= MinGapBetweenFetches)
            {
                return; // another request refreshed while we waited
            }
            _lastAttempt = _time.GetUtcNow();

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var json = await client.GetStringAsync(_jwksUrl);
            var set = new JsonWebKeySet(json);

            _keys = set.Keys
                .Where(k => string.IsNullOrEmpty(k.Use) || k.Use == "sig")
                .Cast<SecurityKey>()
                .ToList();
            _fetchedAt = _time.GetUtcNow();

            if (_keys.Count == 0)
            {
                _logger.LogWarning(
                    "Supabase JWKS returned no keys. The project may still use the legacy JWT secret; " +
                    "set Supabase:JwtSecret or migrate the project to JWT signing keys.");
            }
        }
        catch (Exception ex)
        {
            // Keep serving the previous keys; tokens signed by unknown keys will fail validation.
            _logger.LogError(ex, "Could not fetch Supabase JWKS from {Url}", _jwksUrl);
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
