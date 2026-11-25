using System.Net;
using System.Text.Json;
using Touchline.Api.Tests.Auth;
using Touchline.Api.Tests.Db;

namespace Touchline.Api.Tests.Football;

/// <summary>GET /api/v1/news, /news/categories and /news/headlines: read only from sync.news_cache, never a provider.</summary>
public sealed class NewsTests(FootballDbFixture f) : IClassFixture<FootballDbFixture>
{
    private async Task SeedAsync(object articles, TimeSpan age, string key = "sa_football_news")
    {
        await f.FixtureAsync(f.Clock.Now.AddDays(-3), 5, 1, 0);   // makes Pirates and Chiefs current-season clubs
        var fetched = f.Clock.Now - age;
        await f.Db.ExecAsync("""
            INSERT INTO sync.news_cache (cache_key, response, fetched_at, expires_at) VALUES ($1, CAST($2 AS jsonb), $3, $4)
            ON CONFLICT (cache_key) DO UPDATE SET response = EXCLUDED.response, fetched_at = EXCLUDED.fetched_at, expires_at = EXCLUDED.expires_at
            """, key, JsonSerializer.Serialize(articles), fetched.UtcDateTime, fetched.AddHours(3).UtcDateTime);
    }

    private static object Item(string title, string url, string source, int minutesAgo, string summary = "A short teaser.", string? image = "https://cdn.example.com/a.jpg",
        string? content = null) => new Dictionary<string, object?>
    {
        ["title"] = title, ["url"] = url, ["source"] = source, ["summary"] = summary, ["image_url"] = image,
        ["published_at"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        ["content"] = content,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string path, HttpClient? client = null)
    {
        var response = await (client ?? f.Api.CreateClient()).GetAsync(path);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    [SkippableFact]
    public async Task Articles_are_newest_first_paged_attributed_and_never_carry_the_full_text()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        var longSummary = "<p>Orlando Pirates&nbsp;coach</p> " + string.Join(" ", Enumerable.Repeat("word", 120));
        await SeedAsync(new[]
        {
            Item("Older story", "https://www.kickoff.com/psl/older", "KickOff", 90),
            Item("Newest story", "https://www.soccerladuma.co.za/news/newest", "SoccerLaduma", 5, longSummary, content: "FULL ARTICLE BODY that must never be returned"),
            Item("Middle story", "https://idiskitimes.co.za/middle", "", 30, image: "not a url"),
        }, TimeSpan.FromMinutes(30));

        var (status, body) = await Get("/api/v1/news?pageSize=2");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        var page1 = body.GetProperty("articles").EnumerateArray().ToList();
        Assert.Equal(["Newest story", "Middle story"], page1.Select(a => a.GetProperty("title").GetString()));

        var newest = page1[0];
        Assert.Equal("SoccerLaduma", newest.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("soccerladuma.co.za", newest.GetProperty("source").GetProperty("domain").GetString());
        Assert.Equal("https://www.soccerladuma.co.za/news/newest", newest.GetProperty("url").GetString());
        Assert.Equal(16, newest.GetProperty("id").GetString()!.Length);
        var summary = newest.GetProperty("summary").GetString()!;
        Assert.True(summary.Length <= 301, summary);
        Assert.StartsWith("Orlando Pirates coach", summary);
        Assert.DoesNotContain("<p>", summary);
        Assert.EndsWith("…", summary);
        Assert.DoesNotContain("FULL ARTICLE BODY", body.ToString());
        Assert.False(newest.TryGetProperty("content", out _));

        // A missing source falls back to the publisher's domain; an unusable image is null.
        var middle = page1[1];
        Assert.Equal("idiskitimes.co.za", middle.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, middle.GetProperty("imageUrl").ValueKind);

        var page2 = (await Get("/api/v1/news?pageSize=2&page=2")).Body.GetProperty("articles").EnumerateArray().ToList();
        Assert.Equal(["Older story"], page2.Select(a => a.GetProperty("title").GetString()));
        Assert.Empty(f.Api.Outbound.Refused);   // no call to any news provider
    }

    [SkippableFact]
    public async Task The_same_article_appears_once_by_canonical_url_or_title()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await SeedAsync(new[]
        {
            Item("Chiefs confirm new coach", "https://www.kickoff.com/psl/chiefs-coach?utm_source=rss&utm_medium=feed", "KickOff", 10),
            Item("Chiefs confirm new coach", "https://www.kickoff.com/psl/chiefs-coach/", "KickOff", 20),
            Item("Chiefs Confirm New Coach!", "https://news.google.com/rss/articles/abc", "Google News", 15),
            Item("A different story", "https://www.kickoff.com/psl/other", "KickOff", 40),
        }, TimeSpan.FromMinutes(10));

        var body = (await Get("/api/v1/news")).Body;
        var titles = body.GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()).ToList();
        Assert.Equal(2, titles.Count);
        Assert.Equal(2, body.GetProperty("totalInFeed").GetInt32());
        var kept = body.GetProperty("articles")[0];
        Assert.Equal("https://www.kickoff.com/psl/chiefs-coach", kept.GetProperty("url").GetString());   // tracking stripped
    }

    [SkippableFact]
    public async Task Category_and_club_filters_map_to_categories_and_football_team_ids()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await SeedAsync(new[]
        {
            Item("Pirates sign striker on loan", "https://a.example.com/1", "KickOff", 5),
            Item("Bucs beat Chiefs in the Soweto derby", "https://a.example.com/2", "KickOff", 10, "A big PSL win."),
            Item("Bafana squad named for AFCON", "https://a.example.com/3", "iDiski Times", 15),
            Item("Weather delays training", "https://a.example.com/4", "SoccerLaduma", 20),
        }, TimeSpan.FromMinutes(10));

        var pirates = (await Get($"/api/v1/news?clubId={f.Home}")).Body;
        Assert.Equal(2, pirates.GetProperty("total").GetInt32());
        Assert.All(pirates.GetProperty("articles").EnumerateArray(), a =>
            Assert.Contains(a.GetProperty("clubs").EnumerateArray(), c => c.GetProperty("id").GetInt32() == f.Home));
        var derby = pirates.GetProperty("articles").EnumerateArray().Single(a => a.GetProperty("title").GetString()!.StartsWith("Bucs"));
        Assert.Equal(2, derby.GetProperty("clubs").GetArrayLength());   // Bucs → Pirates, Chiefs → Kaizer Chiefs

        var transfers = (await Get("/api/v1/news?category=transfers")).Body;
        Assert.Equal(["Pirates sign striker on loan"], transfers.GetProperty("articles").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        Assert.Equal("Weather delays training", (await Get("/api/v1/news?category=general")).Body.GetProperty("articles")[0].GetProperty("title").GetString());

        var categories = (await Get("/api/v1/news/categories")).Body.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(1, categories.Single(c => c.GetProperty("id").GetString() == "bafana").GetProperty("count").GetInt32());
        Assert.Equal(1, categories.Single(c => c.GetProperty("id").GetString() == "psl").GetProperty("count").GetInt32());

        var none = (await Get("/api/v1/news?category=cups")).Body;
        Assert.Equal("no_matches", none.GetProperty("status").GetString());   // the feed has articles; the filter matched none
        Assert.Empty(none.GetProperty("articles").EnumerateArray());

        var unknown = await Get("/api/v1/news?category=gossip");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
        Assert.Equal("unknown_category", unknown.Body.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task An_empty_feed_is_no_articles_and_a_late_one_is_stale_with_the_last_good_articles()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await SeedAsync(Array.Empty<object>(), TimeSpan.FromMinutes(20));
        var empty = (await Get("/api/v1/news")).Body;
        Assert.Equal("no_articles", empty.GetProperty("status").GetString());
        Assert.Equal(0, empty.GetProperty("total").GetInt32());
        Assert.False(empty.GetProperty("freshness").GetProperty("stale").GetBoolean());

        await SeedAsync(new[] { Item("Last good story", "https://a.example.com/last", "KickOff", 500) }, TimeSpan.FromHours(7));
        var (status, stale) = await Get("/api/v1/news");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("stale", stale.GetProperty("status").GetString());
        Assert.True(stale.GetProperty("freshness").GetProperty("stale").GetBoolean());
        Assert.InRange(stale.GetProperty("freshness").GetProperty("ageMinutes").GetInt32(), 419, 421);
        Assert.Equal("Last good story", stale.GetProperty("articles")[0].GetProperty("title").GetString());
        Assert.True((await Get("/api/v1/news/headlines")).Body.GetProperty("stale").GetBoolean());
    }

    [SkippableFact]
    public async Task Headlines_keep_the_web_apps_shape_and_clamp_max()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await SeedAsync(Enumerable.Range(1, 60).Select(i => Item($"Story {i}", $"https://a.example.com/s{i}", "KickOff", i)).ToArray(), TimeSpan.FromMinutes(5));

        var body = (await Get("/api/v1/news/headlines?max=3")).Body;
        Assert.Equal(["totalArticles", "articles", "fetchedAt", "stale"], body.EnumerateObject().Select(p => p.Name));   // the web shape, plus "stale"
        Assert.Equal(3, body.GetProperty("totalArticles").GetInt32());
        var first = body.GetProperty("articles")[0];
        Assert.Equal(["title", "url", "imageUrl", "publishedAt", "summary", "source"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Story 1", first.GetProperty("title").GetString());
        Assert.Equal("KickOff", first.GetProperty("source").GetString());

        Assert.Equal(20, (await Get("/api/v1/news/headlines")).Body.GetProperty("totalArticles").GetInt32());
        Assert.Equal(50, (await Get("/api/v1/news/headlines?max=500")).Body.GetProperty("totalArticles").GetInt32());
        Assert.Equal(1, (await Get("/api/v1/news/headlines?max=0")).Body.GetProperty("totalArticles").GetInt32());
    }

    [SkippableFact]
    public async Task Older_gnews_shaped_rows_are_read_too()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        await SeedAsync(new
        {
            totalArticles = 1,
            articles = new[] { new { title = "GNews story", description = "Teaser", url = "https://news.example.org/x", image = (string?)null,
                publishedAt = "2026-11-25T10:00:00Z", source = new { name = "Example News", url = "https://news.example.org" } } },
        }, TimeSpan.FromMinutes(5), key: "gnews_shape");
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["News:CacheKey"] = "gnews_shape" });
        var article = (await Get("/api/v1/news", api.CreateClient())).Body.GetProperty("articles")[0];
        Assert.Equal("Example News", article.GetProperty("source").GetProperty("name").GetString());
        Assert.Equal("Teaser", article.GetProperty("summary").GetString());
    }

    [SkippableFact]
    public async Task An_unwritten_feed_is_503_news_unavailable_and_headlines_stay_200_unavailable()
    {
        Skip.If(f.SkipReason is not null, f.SkipReason);
        using var api = new TestApiFactory(f.Db.ApiConnectionString, f.Tokens, f.Clock, new Dictionary<string, string> { ["News:CacheKey"] = "never_written" });
        var client = api.CreateClient();
        var (status, body) = await Get("/api/v1/news", client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("news_unavailable", body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Get("/api/v1/news/categories", client)).Status);

        var (hs, headlines) = await Get("/api/v1/news/headlines", client);
        Assert.Equal(HttpStatusCode.OK, hs);
        Assert.True(headlines.GetProperty("unavailable").GetBoolean());
        Assert.Equal(0, headlines.GetProperty("totalArticles").GetInt32());
        Assert.Empty(api.Outbound.Refused);
    }
}
