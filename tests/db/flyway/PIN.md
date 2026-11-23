# Pinned Flyway migrations

These files are a copy of `db/migrations/*.sql` from **touchline-sync `main` at
`15b49d1e59b04cbfc77646898d54b59b01aee15e`** (2026-11-23). They are the canonical schema the
API's database tests build from (`tests/Touchline.Api.Tests/Db/TestDatabase.cs`).

- One deliberate difference: the two role passwords in `V001` are replaced by `'<redacted>'`, so the
  plaintext passwords committed in the sync repo aren't copied into this one. The test harness replaces
  them with its own anyway.
- Do not edit them here otherwise. To refresh after new sync migrations merge, copy the folder again from
  touchline-sync main and update the commit above:
  `git -C <touchline-sync> archive origin/main db/migrations | tar -x --strip-components=2 -C tests/db/flyway`
- To test against a live checkout instead, set `TOUCHLINE_FLYWAY_DIR=<touchline-sync>/db/migrations`.
- This repo's own pending migrations (`docs/migrations/007_*.sql` and later, which become V039+)
  are applied after the pinned set until they are merged into touchline-sync and re-pinned.
- `V001` creates the service roles with passwords committed in the sync repo. The tests replace
  those passwords with their own; the committed passwords must be rotated in production
  (listed under "needs other owners" in docs/sprints/sprint-1-report.md).
