# Replay pack: GW9, Orlando Pirates 2–1 Kaizer Chiefs

`fixture_19781326/` is a copy of touchline-sync `tests/replay/fixture_19781326/` at sync `main`
`15b49d1` (2026-11-23), the worker owner's reference replay (Sportmonks fixture 19781326, our
`football.fixtures.id` 1393). Steps 0–3 are reconstructed from production rows; 4a and 4b are
synthetic corrections. Each step is the provider payload the hourly job would see.

The API's `Reference/ReplayTests.cs` applies each step with the worker's upsert semantics
(insert or update by Sportmonks id, never delete) and checks match detail and points after
each one. Refresh by copying the folder again from touchline-sync.

`../reference/gw9.json` is a read-only snapshot (2026-11-23) of all eight GW9 fixtures from the
production `football` schema: fixtures, teams, lineups, events and the players they name.
It is the data for the Sprint 2 reference week (`docs/rules/reference-week.md`).
