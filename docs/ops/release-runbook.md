# Release and recovery runbook

**Owners:** the backend lead, plus a named backup who has rehearsed every section.

## Release

1. **Check CI is green on the release commit:**
   - build;
   - all tests, with DB tests required;
   - dependency check;
   - secret scan.

   Record the commit SHA in `docs/release/release-record.md`.
2. **Apply pending migrations, in order, in staging first:** `docs/migrations` / touchline-sync Flyway. Each one is safe to rerun.
3. **Deploy the API to staging and smoke-test:**
   - `/api/v1/health/ready`;
   - `/api/v1/health/freshness`;
   - one match detail;
   - a staging manager's transfer preview.
4. **Repeat in production** at a low-traffic time: never within 2 h of a deadline or during matches.

## Rollback

| Situation | Action |
|---|---|
| The new API version misbehaves | Redeploy the previous image or commit. The migrations are additive, and every new feature checks for its tables or columns, so the previous version runs on the newer schema |
| A migration is wrong | Don't drop anything. Write a corrective forward migration. If it blocks writes, pause the affected feature (switches below) while fixing |
| After either | Reconcile pending operations: run the deadline lock report for the current gameweek (`reconciled`) and check settlement for the last gameweek |

## Switches (independent)

| Switch | Effect |
|---|---|
| `Operations__ScoringPaused=true` | Finalising, corrections and recorded settlement transitions return `503 scoring_paused`. Reads, live points and manager writes continue |
| `Fantasy__RequireTargetGameweek=false` | Accepts writes without `gameweekId`, for a client rollout only |
| `RateLimits__*` | Tighten during abuse |
| Outbound alerts | Not built. When the delivery worker exists, it gets its own pause switch, separate from scoring |

## Restore (rehearse in an isolated environment; record the times)

1. Restore the latest Supabase backup into an **isolated** project, never over production.
2. Point a staging API at it (its own `ConnectionStrings__Touchline` and `Supabase__Url`).
3. Reconcile:
   - squads and transfers (lock report);
   - replay football facts (the worker refills `football`);
   - verify standings against the last published revision;
   - re-apply account deletions recorded after the backup.
4. Agree the maximum data loss and recovery time in writing **before** the rehearsal, then record the actuals.

## Credential and secret rotation

| Secret | Where | How to rotate |
|---|---|---|
| `app_api_user` password | DB role; `ConnectionStrings__Touchline` in the host's secret store | (1) `ALTER ROLE app_api_user PASSWORD '<new>'` as owner. (2) Update the host secret. (3) Restart; check `/health/ready`. The old password is in the sync repo's V001: rotate **now** |
| `sync_worker_user` password | DB role; worker hosting | Sync team, same steps. Also committed in V001: rotate now |
| Supabase JWT signing keys | Supabase dashboard | Rotate in Supabase: the new key is published in JWKS and the API picks it up (tested: `A_rotated_signing_key_is_picked_up…`). Revoke the old key after the token lifetime (1 h) |
| Legacy `Supabase__JwtSecret` | only if HS256 is still used | Migrate to signing keys; then clear it |
| Apple OAuth secret | Supabase provider config | Every ≤ 6 months, by the owner named in `docs/accounts/provider-checklist.md` |
| Sportmonks key | worker only | Sync team; the API never holds it |

**Rehearsal:** rotate `app_api_user` in staging with the API running. Expect at most one failed request per pooled connection; record the downtime.

## Incidents

1. **Declare:** the on-call primary takes the lead and opens an incident note (time, symptom).
2. **Stabilise:** use the switches above, or roll back.
3. **Communicate:** a status message in the app or socials if managers are affected (deadlines, scores).
4. **Escalate:** after 30 minutes without mitigation, to the backend lead and then the product owner. For a data breach, call the privacy officer immediately (POPIA notification, `docs/security/privacy-inventory.md`).
5. **Close:** write a timeline, the root cause and follow-ups, and attach them to the release record.
