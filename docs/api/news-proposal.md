# Proposal: one news contract through the C# API (needs the news-service owner)

**Status:** proposal for the team. Nothing is built.

**Owner:** the news-service maintainer (`touchline-news-api` in Touchline_Main) together with the API owner. `05-news-service.md` isn't in `docs/plans`, so this follows 02 and 01 Gate 4.

## Contract (proposed)

| Route | Returns |
|---|---|
| `GET /api/v1/news?category=&team=&page=&pageSize=` | `{ items: [Article], page, pageSize, total, fetchedAtUtc, source: "news-service" }` |
| `GET /api/v1/news/categories` | `[ { id, slug, name } ]`: one authoritative id/slug mapping (fixes "Match Reports" stored by name but requested as `match-reports`) |
| `GET /api/v1/news/headlines?max=` | **compatibility during the rollout**: the first `max` items in the old headline shape |

**Article fields:**
- `id`, which is stable;
- `title`, `summary`, `url` (the canonical source URL);
- `source` (attribution) and `imageUrl` (nullable);
- `publishedAtUtc`, `categoryId`;
- `teamIds` (`football.teams.id`, the stable club ids from `docs/accounts/club-mapping.md`).

## Rules

- Article ids, attribution, filters and paging are preserved from the news service. Importing the same article twice yields one item.
- A provider outage (`503` with the last fetch time) is distinct from an empty category (`200`, `items: []`).
- Football facts never come from news. News is display-only.

## Open decisions (needs other owners)

1. **Deployment:** does the C# API proxy the news service, or does the news service write to Postgres (the `sync.news_cache` pattern) for the API to read? Rule 1 favours Postgres as the handoff.
2. **Production ingestion path:** is it GNews, or the RSS feeds in `sync.news_cache`? Who runs it?
3. **Rights:** republishing full articles versus linking. Only title, summary and link are proposed.
