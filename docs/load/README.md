# Load test

**Script:** `k6-deadline.js` (k6). **Never run against production.** The script refuses a `BASE_URL` containing "prod".

## What it covers

- **Match reads:** 200 requests per second for 5 minutes. Cold (cache-busting) and warm (`If-None-Match`) timings are reported separately.
- **Standings:** league standings, 20 requests per second; this is the most expensive read.
- **Deadline transfers:** each test manager quotes, confirms and retries with the same `Idempotency-Key`.

## Targets (handbook Gate 8; proposals until agreed)

| Measure | Target | How it's observed |
|---|---|---|
| Match reads | p95 < 500 ms at twice the forecast traffic | k6 trends `match_read_cold_ms` and `match_read_warm_ms` at the API boundary |
| Squad confirmations | p95 < 1 s; zero lost or duplicate writes | `transfer_confirm_ms`; afterwards, `POST /admin/gameweeks/{id}/lock` must report `reconciled: true`, and the receipts count must equal `transfers_confirmed` |
| Live update latency | p95 ≤ 30 s of a measurable provider update | Not observable from the API alone: there's no provider timestamp, and commits are hourly today. Report worker commit → API visibility instead (method in `docs/sprints/sprint-1-report.md`) |

## Running it in staging (ops)

1. Create N staging managers with squads; the staging kit gives two.
2. Export `TOKENS`, `LEAGUE_ID`, `GAMEWEEK_ID` (open) and `FIXTURE_ID`.
3. Run `k6 run docs/load/k6-deadline.js`. Record the dataset size, the hardware and the results in the release record.
4. Run the deadline lock report and reconcile.
