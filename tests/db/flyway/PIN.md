# Pinned Flyway migrations

These files are a copy of `db/migrations/*.sql` from **touchline-sync `main` at
`c817044`** (V001–V041, V043–V050, 2026-11-25). The sync owner's operations migration is V049 there
(renamed from V042, which no longer exists). They are the canonical schema the API's database tests build
from (`tests/Touchline.Api.Tests/Db/TestDatabase.cs`).

- `V001` is copied unchanged: since 24 Nov 2026 it creates the two login roles without passwords. The test
  harness gives them its own test-only password.
- Do not edit them here otherwise. To refresh after new sync migrations merge, copy the folder again from
  touchline-sync main and update the commit above:
  `git -C <touchline-sync> archive origin/main db/migrations | tar -x --strip-components=2 -C tests/db/flyway`
- To test against a live checkout instead, set `TOUCHLINE_FLYWAY_DIR=<touchline-sync>/db/migrations`.
- This repo's own pending migrations (`docs/migrations/007_*.sql` and later, which become V043+)
  are applied after the pinned set until they are merged into touchline-sync and re-pinned.
- `V001` creates the service roles with passwords committed in the sync repo. The tests replace
  those passwords with their own; the committed passwords must be rotated in production
  (listed under "needs other owners" in docs/sprints/sprint-1-report.md).
