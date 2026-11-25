# News

**For:** the web and mobile developers, and the news-service owner.

**Status • 25 November 2026:** built (`GET /api/v1/news`, `/news/categories`, `/news/headlines`). It replaces [the proposal](news-proposal.md) where they differ.

## Where the articles come from

- **Postgres only.** The API reads one row of `sync.news_cache` (key `sa_football_news`, setting `News:CacheKey`). The Netlify RSS job in touchline-sync (`netlify/lib/news.ts`) writes that row about every 3 hours.
- **What the job writes:** up to 50 articles from SoccerLaduma, iDiski Times, KickOff (football items only) and a Google News PSL search, each with `title`, `url`, `source`, `summary`, `image_url` and `published_at`.
- **No outbound calls.** The API never calls a news provider, so a news outage can't slow down or break football or fantasy.
- **No migration needed.** `app_api_user` already has SELECT on `sync.news_cache` (V028), and RLS is off on that table. This was checked read-only on the live database on 25 Nov 2026.

## Routes (public, no token)

### `GET /api/v1/news?category=&clubId=&page=&pageSize=`

Results are newest first, with stable ordering (published time, then id). `pageSize` defaults to 20 and can be at most 50.

```json
{
  "status": "ok",
  "articles": [
    {
      "id": "3f2a9c41d07e8b15",
      "title": "Chiefs v Pirates: Cape Town leaves PSL with big decision to make",
      "summary": "A short teaser from the publisher, at most 300 characters…",
      "source": { "name": "KickOff", "domain": "kickoff.com" },
      "url": "https://www.kickoff.com/psl/kaizer-chiefs/…",
      "publishedAtUtc": "2026-11-25T20:15:00+00:00",
      "imageUrl": "https://cdn.kickoff.com/News/530952/2/default.jpg",
      "categories": ["psl", "cups"],
      "clubs": [ { "id": 12, "name": "Kaizer Chiefs" }, { "id": 15, "name": "Orlando Pirates" } ]
    }
  ],
  "page": 1, "pageSize": 20, "total": 13, "totalInFeed": 50,
  "filters": { "category": "psl", "clubId": null },
  "freshness": { "lastFetchedUtc": "2026-11-25T21:01:16+00:00", "expiresAtUtc": "2026-11-26T00:01:15+00:00", "ageMinutes": 10, "stale": false, "staleAfterHours": 6 },
  "serverTimeUtc": "2026-11-25T21:11:00+00:00"
}
```

**`status` tells the screen what to show.** Each case is separate:

| status | Meaning | What to show |
|---|---|---|
| `ok` | Fresh articles | The list |
| `stale` | The feed hasn't refreshed for over 6 hours (`News:StaleAfterHours`). These are the last good articles | The list, plus "Updated 7 hours ago" from `freshness` |
| `no_articles` | The feed was written but is empty | "No news right now" |
| `no_matches` | The feed has articles, but none match this filter | "No news for this club or category yet", with a link to all news |
| HTTP 503 `news_unavailable` | The feed has never been written (no row, or no table) | "News isn't available", with a retry. The rest of the app works |

An unknown `category` is 400 `unknown_category`.

### `GET /api/v1/news/categories`

Lists the ids the `category` filter accepts, with how many current articles each has:

```json
{ "categories": [ { "id": "psl", "name": "PSL", "count": 13 }, { "id": "cups", "name": "Cups", "count": 4 }, { "id": "continental", "name": "CAF and continental", "count": 8 },
                  { "id": "bafana", "name": "Bafana Bafana", "count": 1 }, { "id": "banyana", "name": "Banyana and women's football", "count": 0 },
                  { "id": "transfers", "name": "Transfers", "count": 7 }, { "id": "general", "name": "General", "count": 19 } ],
  "freshness": { … } }
```

### `GET /api/v1/news/headlines?max=N`

Compatibility for the web app's current call (`fetchNews` → `/news/headlines?max=20`, which Netlify routes to `functions/news.js`).
- **Shape:** the same as that function: `{ totalArticles, articles: [{ title, url, imageUrl, publishedAt, summary, source }], fetchedAt }`, plus `stale`.
- **`max`:** clamped to 1–50; the default is 20.
- **Never fails:** it always answers 200. An unwritten feed gives `unavailable: true` and `detail`, as the function did.

## Article rules

- **Attribution always.** `source.name` comes from the feed, or else the publisher's domain. `url` opens the publisher's page.
- **Never the article text.** Only the title, a plain-text summary (HTML stripped, cut to 300 characters at a word boundary) and the link. Any full-text field in the cache is dropped.
- **De-duplicated.** The same article appears once:
  - by canonical URL, ignoring tracking parameters, the fragment and a trailing slash;
  - then by title, ignoring case and punctuation (this catches a Google News copy of a publisher's story).
  - The newest copy is kept.
- **Image:** `imageUrl` is `null` when missing or not an http(s) URL.
- **Stable id:** a hash of the canonical URL, so the same article keeps its id across refreshes.
- **Categories:** derived from whole words in the title and summary. An article can have several; with none it is `general`.
- **Clubs:** matched to `football.teams` ids of the current PSL season (the same ids as `/clubs`). The matching uses each club's name and the common nicknames in headlines: Bucs → Orlando Pirates, Amakhosi → Kaizer Chiefs, Downs and Masandawana → Mamelodi Sundowns, Usuthu → AmaZulu, and so on.
- **Old rows ignored:** an older GNews-shaped row (`{ articles: [...] }`, the `headlines:5` key from August) can be read if configured, but it isn't used.

## Decisions for the team (conflicts with 05-news-service.md and the proposal)

1. **Where news comes from.**
   - **Plan 05:** keep the Node/Prisma `touchline-news-api` as a private provider, called by the C# API through a service URL.
   - **Built:** the API reads only Postgres (`sync.news_cache`), as this request and the architecture rule ("Postgres is the only handoff", enforced by a test) require.
   - **To bring the Node service's articles in,** it would have to write to Postgres too (the proposal's open decision 1).
2. **Categories.**
   - **Plan 05 and the proposal:** the ingester stores category slugs (`{ id, slug, name }`), one `categoryId` per article.
   - **Built:** the RSS feed has no categories, so the API derives them from keywords, and an article can have several.
   - **Decide:** keep derived categories, or have the ingester store them.
3. **Club tags.**
   - **Plan:** team tags come from ingestion.
   - **Built:** derived by name and nickname matching, so a rare mis-tag is possible (for example "Galaxy" in a non-PSL story).
   - **Decide:** whether the ingester should tag clubs.
4. **Response shape.**
   - **Proposal:** `items`, `fetchedAtUtc`, `source: "news-service"`, `categoryId`, `teamIds`.
   - **Built:** `articles`, `freshness`, `status`, `categories[]`, `clubs[{id, name}]`.
   - **Expo:** reads the Node service's `/api/news` today and would need to switch. Confirm the shape before the mobile switch.
5. **Provider outage.**
   - **Proposal:** 503 with the last fetch time.
   - **Built:** a late feed is 200 `stale` with the last good articles, as plan 05 asks ("one failed source leaves the previous feed readable"). 503 is only for a feed that was never written.
6. **One ingester.** Plan 05 wants exactly one scheduled news ingester.
   - The RSS job writes `sa_football_news`.
   - The FastAPI GNews path wrote `headlines:5`, last in August.
   - **Decide:** retire the GNews writer and its credentials.
7. **Rights.** Title, a 300-character summary, the link and a hot-linked publisher image are served. Image hotlinking is already on the legal and product list: confirm it is acceptable.
8. **Stale threshold.** 6 hours, which is two missed 3-hourly refreshes. Confirm it.
