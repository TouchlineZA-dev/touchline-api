using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Touchline.Api.Data;

namespace Touchline.Api.Services;

/// <summary>News settings ("News" section).</summary>
public sealed class NewsOptions
{
    public const string SectionName = "News";

    /// <summary>The sync.news_cache row the RSS job writes (touchline-sync netlify/lib/news.ts).</summary>
    public string CacheKey { get; set; } = "sa_football_news";

    /// <summary>The feed counts as stale when its last fetch is older than this. The job refreshes every 3 hours.</summary>
    public int StaleAfterHours { get; set; } = 6;

    /// <summary>Summaries are cut to this many characters: a teaser, never the article.</summary>
    public int SummaryMaxLength { get; set; } = 300;

    public int DefaultPageSize { get; set; } = 20;
    public int MaxPageSize { get; set; } = 50;
}

/// <param name="Id">Stable id: a hash of the canonical publisher URL.</param>
/// <param name="Source">Who published it. Always present; show it next to the headline.</param>
/// <param name="Url">The canonical publisher URL to open. Articles are never reproduced in full.</param>
public sealed record NewsArticle(
    string Id,
    string Title,
    string Summary,
    NewsSource Source,
    string Url,
    DateTimeOffset? PublishedAtUtc,
    string? ImageUrl,
    IReadOnlyList<string> Categories,
    IReadOnlyList<NewsClub> Clubs);

public sealed record NewsSource(string Name, string? Domain);

public sealed record NewsClub(int Id, string Name);

/// <param name="Status">ok, stale (last good articles, feed not refreshed in time), no_articles (the feed is empty),
/// or no_matches (the filter matched nothing; the feed itself has articles).</param>
public sealed record NewsFreshness(DateTimeOffset LastFetchedUtc, DateTimeOffset? ExpiresAtUtc, int AgeMinutes, bool Stale, int StaleAfterHours);

public sealed record NewsPage(
    string Status,
    IReadOnlyList<NewsArticle> Articles,
    int Page,
    int PageSize,
    int Total,
    int TotalInFeed,
    NewsFilters Filters,
    NewsFreshness Freshness,
    DateTimeOffset ServerTimeUtc);

public sealed record NewsFilters(string? Category, int? ClubId);

public sealed record NewsCategory(string Id, string Name, int Count);

public sealed record NewsCategoriesView(IReadOnlyList<NewsCategory> Categories, NewsFreshness Freshness);

/// <summary>The web app's /news/headlines shape (Touchline_Main frontend/netlify/functions/news.js), unchanged.</summary>
public sealed record HeadlineArticle(string Title, string Url, string? ImageUrl, DateTimeOffset? PublishedAt, string Summary, string Source);

public sealed record HeadlinesView(int TotalArticles, IReadOnlyList<HeadlineArticle> Articles, DateTimeOffset? FetchedAt, bool Stale,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? Unavailable = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Detail = null);

internal sealed class NewsCacheRow
{
    public string Response { get; set; } = "[]";
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>The feed hasn't been written (no sync.news_cache row, or no table): a storage problem, not an empty feed.</summary>
public sealed class NewsUnavailableException(string message) : Exception(message);

/// <summary>
/// South African football news from Postgres only: the sync.news_cache row the Netlify RSS job writes. The API
/// never calls a news provider. Articles are de-duplicated (canonical URL, then title), newest first, always
/// with their source, with a short summary and never the article text. Categories and clubs are derived from
/// the headline and summary, clubs mapped to football.teams ids of the current PSL season.
/// </summary>
public sealed partial class NewsService(TouchlineDbContext db, FootballService football, SchemaFeatures schema, IOptions<NewsOptions> options, TimeProvider time)
{
    private readonly NewsOptions _options = options.Value;

    /// <summary>Category id, display name, and the words that put an article in it (whole words, any case).</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string[] Terms)> CategoryRules =
    [
        ("psl", "PSL", ["psl", "premiership", "betway premiership", "dstv premiership", "league title", "log"]),
        ("cups", "Cups", ["mtn8", "nedbank cup", "carling knockout", "carling cup", "cup final", "semi-final", "quarter-final"]),
        ("continental", "CAF and continental", ["caf", "champions league", "confederation cup", "caf cl"]),
        ("bafana", "Bafana Bafana", ["bafana", "bafana bafana", "afcon", "world cup qualifier", "world cup qualifiers"]),
        ("banyana", "Banyana and women's football", ["banyana", "women's", "hollywoodbets super league", "safa women"]),
        ("transfers", "Transfers", ["transfer", "transfers", "sign", "signs", "signing", "signed", "joins", "loan", "contract", "deal", "target", "linked"]),
    ];

    public const string GeneralCategory = "general";

    /// <summary>Nicknames the headlines use, mapped to a word in the club's name in football.teams.</summary>
    private static readonly IReadOnlyList<(string Alias, string NameContains)> ClubAliases =
    [
        ("chiefs", "Chiefs"), ("amakhosi", "Chiefs"), ("glamour boys", "Chiefs"),
        ("pirates", "Pirates"), ("bucs", "Pirates"), ("buccaneers", "Pirates"),
        ("sundowns", "Sundowns"), ("masandawana", "Sundowns"), ("downs", "Sundowns"),
        ("galaxy", "Galaxy"), ("rockets", "Galaxy"),
        ("gallants", "Gallants"), ("arrows", "Arrows"), ("abafana bes'thende", "Arrows"),
        ("usuthu", "AmaZulu"), ("amazulu", "AmaZulu"), ("stellies", "Stellenbosch"),
        ("babina noko", "Sekhukhune"), ("chilli boys", "Chippa"), ("natal rich boyz", "Richards Bay"),
        ("rise and shine", "Polokwane"),
    ];

    public async Task<NewsPage> GetAsync(string? category, int? clubId, int? page, int? pageSize, CancellationToken ct)
    {
        var (articles, freshness) = await LoadAsync(ct);
        var size = Math.Clamp(pageSize ?? _options.DefaultPageSize, 1, _options.MaxPageSize);
        var number = Math.Max(1, page ?? 1);
        var cat = string.IsNullOrWhiteSpace(category) ? null : category.Trim().ToLowerInvariant();
        if (cat is not null && cat != GeneralCategory && CategoryRules.All(c => c.Id != cat))
        {
            throw new RuleViolationException([$"'{category}' isn't a news category. GET /api/v1/news/categories lists them."], "Unknown category");
        }

        var filtered = articles
            .Where(a => cat is null || a.Categories.Contains(cat))
            .Where(a => clubId is null || a.Clubs.Any(c => c.Id == clubId))
            .ToList();
        var status = articles.Count == 0 ? "no_articles"
            : filtered.Count == 0 ? "no_matches"
            : freshness.Stale ? "stale"
            : "ok";

        return new NewsPage(status, filtered.Skip((number - 1) * size).Take(size).ToList(), number, size, filtered.Count, articles.Count,
            new NewsFilters(cat, clubId), freshness, time.GetUtcNow());
    }

    public async Task<NewsCategoriesView> CategoriesAsync(CancellationToken ct)
    {
        var (articles, freshness) = await LoadAsync(ct);
        var list = CategoryRules.Select(c => new NewsCategory(c.Id, c.Name, articles.Count(a => a.Categories.Contains(c.Id))))
            .Append(new NewsCategory(GeneralCategory, "General", articles.Count(a => a.Categories.Contains(GeneralCategory))))
            .ToList();
        return new NewsCategoriesView(list, freshness);
    }

    /// <summary>GET /news/headlines: the web app's current contract. Never fails: an unwritten feed is "unavailable" with 200, as before.</summary>
    public async Task<HeadlinesView> HeadlinesAsync(int? max, CancellationToken ct)
    {
        var limit = Math.Clamp(max ?? 20, 1, 50);
        try
        {
            var (articles, freshness) = await LoadAsync(ct);
            var list = articles.Take(limit)
                .Select(a => new HeadlineArticle(a.Title, a.Url, a.ImageUrl, a.PublishedAtUtc, a.Summary, a.Source.Name))
                .ToList();
            return new HeadlinesView(list.Count, list, freshness.LastFetchedUtc, freshness.Stale);
        }
        catch (NewsUnavailableException ex)
        {
            return new HeadlinesView(0, [], null, false, Unavailable: true, Detail: ex.Message);
        }
    }

    // ---- Reading and normalising -------------------------------------------------------------

    private async Task<(List<NewsArticle> Articles, NewsFreshness Freshness)> LoadAsync(CancellationToken ct)
    {
        if (!await schema.HasTableAsync("sync", "news_cache", ct))
        {
            throw new NewsUnavailableException("The news cache table (sync.news_cache) doesn't exist in this database.");
        }
        var row = await db.Database.SqlQuery<NewsCacheRow>($"""
            SELECT response::text AS "Response", fetched_at AS "FetchedAt", expires_at AS "ExpiresAt"
            FROM sync.news_cache WHERE cache_key = {_options.CacheKey}
            """).FirstOrDefaultAsync(ct)
            ?? throw new NewsUnavailableException($"The news feed hasn't been written yet (no sync.news_cache row '{_options.CacheKey}').");

        var now = time.GetUtcNow();
        var age = (int)Math.Max(0, (now - row.FetchedAt).TotalMinutes);
        var freshness = new NewsFreshness(row.FetchedAt, row.ExpiresAt, age, age > _options.StaleAfterHours * 60, _options.StaleAfterHours);

        var clubs = await ClubMatchersAsync(ct);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var articles = new List<NewsArticle>();
        foreach (var item in Items(row.Response)
                     .Select(i => Normalise(i, clubs))
                     .OfType<NewsArticle>()
                     .OrderByDescending(a => a.PublishedAtUtc ?? DateTimeOffset.MinValue)
                     .ThenBy(a => a.Id, StringComparer.Ordinal))
        {
            // The same story twice (two feeds, or a Google News copy of a publisher's link): keep the newest.
            if (!seenUrls.Add(item.Url) || !seenTitles.Add(TitleKey(item.Title))) continue;
            articles.Add(item);
        }
        return (articles, freshness);
    }

    /// <summary>The row holds a JSON array (the RSS job); an object with "articles" (older GNews rows) is accepted too.</summary>
    private static IEnumerable<JsonElement> Items(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { yield break; }
        var list = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("articles", out var a) && a.ValueKind == JsonValueKind.Array => a,
            _ => default,
        };
        if (list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object) yield return item;
        }
    }

    private NewsArticle? Normalise(JsonElement item, IReadOnlyList<(Regex Pattern, NewsClub Club)> clubs)
    {
        var url = Canonical(Str(item, "url") ?? Str(item, "link"));
        var title = Clean(Str(item, "title"));
        if (url is null || string.IsNullOrEmpty(title)) return null;

        var host = new Uri(url).Host;
        var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        var sourceName = Str(item, "source") is { Length: > 0 } s ? s
            : item.TryGetProperty("source", out var so) && so.ValueKind == JsonValueKind.Object && Str(so, "name") is { Length: > 0 } n ? n
            : domain;

        var summary = Clean(Str(item, "summary") ?? Str(item, "description") ?? "");
        if (summary.Length > _options.SummaryMaxLength)
        {
            var cut = summary[.._options.SummaryMaxLength];
            var space = cut.LastIndexOf(' ');
            summary = (space > _options.SummaryMaxLength / 2 ? cut[..space] : cut).TrimEnd(' ', ',', '.', ';', ':') + "…";
        }

        var image = Str(item, "image_url") ?? Str(item, "imageUrl") ?? Str(item, "image");
        if (image is not null && !(Uri.TryCreate(image, UriKind.Absolute, out var img) && img.Scheme is "http" or "https")) image = null;

        DateTimeOffset? published = DateTimeOffset.TryParse(Str(item, "published_at") ?? Str(item, "publishedAt") ?? Str(item, "pubDate"),
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var p) ? p : null;

        var text = " " + title + " " + summary + " ";
        var categories = CategoryRules.Where(c => c.Terms.Any(t => WholeWord(text, t))).Select(c => c.Id).ToList();
        if (categories.Count == 0) categories.Add(GeneralCategory);
        var matched = clubs.Where(c => c.Pattern.IsMatch(text)).Select(c => c.Club).DistinctBy(c => c.Id).ToList();

        return new NewsArticle(Id(url), title, summary, new NewsSource(sourceName, domain), url, published, image, categories, matched);
    }

    /// <summary>Clubs of the current PSL season with the words that identify them in a headline.</summary>
    private async Task<IReadOnlyList<(Regex Pattern, NewsClub Club)>> ClubMatchersAsync(CancellationToken ct)
    {
        var season = await football.GetCurrentPslSeasonAsync(ct);
        if (season is null) return [];
        var teams = await football.GetTeamsAsync(season.Id, ct);
        var list = new List<(Regex, NewsClub)>();
        foreach (var team in teams)
        {
            var terms = new List<string> { team.Name };
            var trimmed = Regex.Replace(team.Name, @"\b(FC|F\.C\.)\b", "", RegexOptions.IgnoreCase).Trim();
            if (trimmed != team.Name) terms.Add(trimmed);
            terms.AddRange(ClubAliases.Where(a => team.Name.Contains(a.NameContains, StringComparison.OrdinalIgnoreCase)).Select(a => a.Alias));
            var pattern = new Regex(@"(?<![\p{L}\p{N}])(" + string.Join("|", terms.Distinct().Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            list.Add((pattern, new NewsClub(team.Id, team.Name)));
        }
        return list;
    }

    // ---- small helpers ------------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    /// <summary>Strips HTML tags and entities and collapses whitespace: a teaser is plain text.</summary>
    private static string Clean(string? text) =>
        string.IsNullOrEmpty(text) ? "" : Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(text, " ")), " ").Trim();

    /// <summary>The publisher URL without tracking parameters or fragment, so the same article from two feeds matches.</summary>
    public static string? Canonical(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var query = string.Join("&", (uri.Query.TrimStart('?')).Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(kv => !Tracking().IsMatch(kv.Split('=')[0])));
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : uri.AbsolutePath;
        return $"https://{uri.Host.ToLowerInvariant()}{path}{(query.Length > 0 ? "?" + query : "")}";
    }

    private static string Id(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();

    private static string TitleKey(string title) => Whitespace().Replace(NonWord().Replace(title.ToLowerInvariant(), " "), " ").Trim();

    private static bool WholeWord(string text, string term) =>
        Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(term) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex("^(utm_[a-z]+|fbclid|gclid|mc_cid|mc_eid|ref|ocid)$", RegexOptions.IgnoreCase)]
    private static partial Regex Tracking();
}
