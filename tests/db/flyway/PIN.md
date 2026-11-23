# Pinned Flyway migrations

These files are a copy of `db/migrations/*.sql` from **touchline-sync `main` at
`3d63fd0`** (V001–V041, 2026-11-23), plus `V042__operations.sql` from the sync owner's unmerged branch
`sprint/3-4-live-freshness-corrections` at `39c3c57`, so the API's migrations (V043+) are tested on top of all of his. They are the canonical schema the
API's database tests build from (`tests/Touchline.Api.Tests/Db/TestDatabase.cs`).

- One deliberate difference: the two role passwords in `V001` are replaced by `'<redacted>'`, so the
  plaintext passwords committed in the sync repo aren't copied into this one. The test harness replaces
  them with its own anyway.
- Do not edit them here otherwise. To refresh after new sync migrations merge, copy the folder again from
  touchline-sync main and update the commit above:
  `git -C <touchline-sync> archive origin/main db/migrations | tar -x --strip-components=2 -C tests/db/flyway`
- To test against a live checkout instead, set `TOUCHLINE_FLYWAY_DIR=<touchline-sync>/db/migrations`.
- This repo's own pending migrations (`docs/migrations/007_*.sql` and later, which become V043+)
  are applied after the pinned set until they are merged into touchline-sync and re-pinned.
- `V001` creates the service roles with passwords committed in the sync repo. The tests replace
  those passwords with their own; the committed passwords must be rotated in production
  (listed under "needs other owners" in docs/sprints/sprint-1-report.md).
