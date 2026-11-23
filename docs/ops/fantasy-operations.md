# Fantasy operations: who runs what, and how

This is for the fantasy operator on duty, normally Leruo, with a named backup.

**How jobs run:**
- The API runs **no background jobs or schedulers**. Postgres is the only handoff, and every consequential job is an explicit admin call.
- Every admin route needs a Supabase token for a user with `app.users.is_admin = true`. Non-admins get 403.
- Each call is safe to repeat. A scheduler such as a GitHub Actions cron or a Render cron can call these routes with an admin service account if the team wants automation. That choice belongs to the team ("decisions for the team").

| Job | Trigger | Route | Notes |
|---|---|---|---|
| Create a gameweek | Operator, before the season and whenever a new PSL round is published | `POST /api/v1/admin/gameweeks` `{seasonId, name, startDate, endDate, deadlineAt?}` | Uses the synced fixtures. The deadline defaults to 90 min before the first kickoff in its dates. Gets the next public number and assigns the unassigned fixtures in its dates (migration 007). The 2026/27 season was seeded (28 weeks) |
| Edit a future deadline | Operator, when a first kickoff moves | `PUT /api/v1/admin/gameweeks/{id}/deadline` `{deadlineAt, reason}` | Refused once the deadline has passed. Never later than the first kickoff |
| Move a fixture between gameweeks | Operator, when a fixture is rescheduled (postponements: default decision 1) | `PUT /api/v1/admin/fixtures/{id}/gameweek` `{gameweekId, reason, confirmLockedChange?}` | A locked gameweek needs `confirmLockedChange: true`. The result says if a finalised week now needs a correction. History: `GET /api/v1/admin/fixtures/{id}/gameweek-history` |
| Lock at the deadline | Operator, just after each deadline | `POST /api/v1/admin/gameweeks/{id}/lock` | Freezes each team's entry (immutable snapshot) and reconciles receipts with entries. Check `reconciled: true`; escalate `mismatches` before publishing ranks |
| Watch settlement | Operator, after the last final whistle | `GET /api/v1/admin/gameweeks/{id}/settlement` | Shows the state, each fixture's outcome and the blockers. `POST` to the same route records the state transition |
| Resolve a fixture without worker evidence | Operator, after checking the match | `POST /api/v1/admin/fixtures/{id}/resolution` `{outcome, reason}` | `verified_zero` for a genuinely quiet match; `manual_resolution` for a checked finished match; `unattributed_resolved` for unattributed goals or cards after the 48 h hold. The resolution is tied to the facts it saw: if they change, the fixture needs resolving again (`late_facts`) |
| Finalise | Operator, when settlement says `canFinalise` | `POST /api/v1/admin/gameweeks/{id}/score` | Publishes the final revision atomically. **Never on fixture states alone**: it needs the worker's `marked_complete` (`sync.gameweek_sync_status`, 0 rows today) or a recorded resolution for every finished fixture. An interrupted run resumes on the next call |
| Correct a final gameweek (rescore) | Operator, when settlement shows `amendmentNeeded`, or a fixture was moved after finalising | `POST /api/v1/admin/gameweeks/{id}/corrections/preview`, then `POST /api/v1/admin/gameweeks/{id}/corrections` `{source, reason, previousRevisionId}` | The preview lists changed players, teams and overall ranks. Publishing creates an amendment revision with a correction message that managers see on their points. Revisions: `GET /api/v1/admin/gameweeks/{id}/revisions` |
| Transfer windows | Operator, when the PSL announces the window dates | `POST /api/v1/admin/transfer-windows` `{seasonId, name, opensOn, closesOn}` | |
| Preview prices | Operator, before applying | `POST /api/v1/admin/prices/preview?seasonId=` | Dry run with a backtest; saves nothing |
| Apply reviewed window prices | Operator, once per window, after the preview is reviewed | `POST /api/v1/admin/prices/recalculate?windowId=` | Refuses a second run for the same window unless `force=true`. Changes the price revision, so open transfer quotes get `price_changed` |
| Price or availability overrides | Operator | `PUT /api/v1/admin/players/{id}/price-override`, `PUT /api/v1/admin/players/{id}/available` | Insert-only, with a reason |
| Suspend or reinstate an account | Operator (support) | `PUT /api/v1/admin/users/{id}/suspension` `{suspended, reason}` | Read-only and hidden from Overall |

## A gameweek, end to end

1. **Before the deadline:** the gameweek exists with its fixtures assigned (`GET /api/v1/gameweeks`). Check the deadline in SAST.
2. **Deadline + a few minutes:** run `POST …/lock` and check that `reconciled` is true.
3. **During play:** provisional points are live. Nothing to do unless the feed is stale; see the matchday runbook (Sprint 7).
4. **After the last final whistle:**
   - Run `GET …/settlement`.
   - With the worker's evidence and no unattributed events, it's ready.
   - Otherwise, resolve each blocked fixture after checking it.
   - Unattributed goals and cards wait out the 48 h hold first.
5. **Finalise:** run `POST …/score`. The final revision is published, ranks follow from it, and the state is `final`.
6. **Late corrections:** `settlement.amendmentNeeded` turns true. Preview and publish an amendment with source and reason.

## States

`open` → `locked` → `live` → `awaiting_review` → `final`.
- Every change is recorded in `app.gameweek_state_transitions`, with reason, input revision, actor and time.
- `awaiting_review` → `live` is allowed if a fixture reopens.
- `final` only changes through amendments, which stay `final`.
