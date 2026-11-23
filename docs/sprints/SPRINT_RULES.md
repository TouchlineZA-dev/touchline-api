# Sprint rules

Copied from `docs/sprints/RUNBOOK.md` (runbook v2, 23 Nov 2026). The runbook is the source; if they differ, the runbook wins.

## Rules for every sprint

1. **Postgres is the only handoff.**
   - The API never calls Sportmonks or the sync worker, including during outages, manual refreshes and settlement.
   - The only outbound call allowed is to the Supabase JWKS endpoint.
2. **Follow touchline-sync's `BACKEND_INTEGRATION.md`.**
   - If it, or one of Phatu's docs, conflicts with an agreed Touchline rule (Part 3), keep OUR rule.
   - Log the conflict under "conflicts" in that sprint's report and continue.
3. **Database limits:**
   - The API connects as `app_api_user`.
   - No EF migrations and no DDL from the API.
   - Never write to the `football` or `sync` schemas.
   - **No DELETE on app tables.** Use anonymisation, archiving or soft-delete flags. If something truly needs a hard delete, log it under "decisions for the team" and use soft-delete for now.
4. **Schema changes:**
   - Write the next Flyway version in `touchline-sync/db/migrations` (the live DB is at V038; continue after the highest version on main and your stacked branches).
   - Mirror it in `touchline-api/docs/migrations/`.
   - Make both rerunnable: IF NOT EXISTS, guarded ALTERs, idempotent grants.
   - **Never apply migrations.**
   - Code must not crash if a new column isn't live yet.
5. **Never change an agreed rule** (Part 3). Proposals go under "decisions for the team".
6. **Work for other teams:**
   - Sync-worker changes go under "needs sync team".
   - Mobile, Supabase dashboard, news-service, legal/product or ops items go under "needs other owners".
   - Don't do these yourself.
7. **Tests for everything.**
   - Postgres-backed tests run in CI, since Docker isn't installed locally. Skip them locally and make them required in CI.
   - The whole suite must pass before every push.
8. **Secrets:** never print, log or commit them. `.env` stays git-ignored.
9. **Git identity:**
   - Every commit is authored and committed by **Leruo24 <mtller042@myuct.ac.za>**.
   - No Claude/AI author or co-author, and no "Co-Authored-By", "Generated with" or "Claude-Session" lines in commits or PR descriptions.
   - Check with `git log -1 --format=full` before each push.
10. **PRs:** use `gh pr create` if gh is logged in; otherwise print the compare link. Never merge anything.
11. **Report:** write `docs/sprints/sprint-<n>-report.md`, containing:
    - a "Pass when" table: item | evidence (test name, endpoint output or file) | status (done / partial / blocked);
    - Phatu items covered this sprint (doc + section);
    - what's left;
    - needs sync team;
    - needs other owners;
    - conflicts;
    - decisions for the team;
    - migration file names;
    - PR links.
12. **Progress:** update `docs/sprints/PROGRESS.md` after every sprint, recording:
    - mode;
    - current sprint;
    - each sprint's status (not started / in progress / pushed / merged);
    - branches;
    - PR links;
    - migration versions;
    - blockers.

    If you're resumed in a new session, read PROGRESS.md first.
13. **Phatu coverage:** keep `docs/sprints/PHATU-COVERAGE.md` up to date. Every API-owned item from the three plan docs goes in it as: doc | section | item | sprint | evidence | status. The aim is for nothing Phatu wrote for the API to go missing.


## Agreed Touchline rules (never change these; propose under "decisions for the team")

**Squad and lineup**
- Budget R100.0m, stored in millions. Phatu's doc mentions R300m; that's scrapped.
- Squad of 15: 2 GK / 5 DEF / 5 MID / 3 ATT, max 3 per club.
- Starting XI: exactly 1 GK, in 4-3-3, 4-4-2, 3-5-2, 3-4-3 or 5-3-2 (min 3 DEF).
- Bench: 1 GK + 3 outfield. The bench GK only swaps with the GK.
- Only players in a current-season lineup are pickable (admin override available).

**Deadline and transfers**
- Deadline: 90 min before the gameweek's first kickoff.
- Transfers are like for like.
- 1 free transfer a week, no rollover, −4 per extra.
- Unlimited transfers before the GW1 deadline and with Wildcard.
- Players sell at their current price.

**Captaincy**
- Captain ×3, vice ×2.
- The vice gets ×3 only if the captain plays 0 minutes across ALL of the gameweek's fixtures.
- A missing captain or vice is filled by a deterministic pick at scoring.

**Chips**
- Triple Captain: ×4, passes to the vice if the captain plays 0.
- Bench Boost and Wildcard.
- Each chip once per season, one per gameweek. No Free Hit.

**No auto-subs.** A starter who didn't play scores 0.

**Scoring**

| Event | Points |
|---|---|
| Appearance, 45+ min | +1 |
| Appearance, full match | +2 |
| Goal | +3 |
| Assist | +2 |
| Clean sheet (GK/DEF, 60+ min) | +2 |
| Yellow card | −1 |
| Red card | −2 |
| Second yellow | −2 total |
| Own goal | −2 |

**Minutes**
- Counting is inclusive: subbed off at minute m plays m; subbed on at minute m plays length − m + 1.
- Stoppage time: 45+x counts as 45 and 90+x counts as 90. Minutes are capped at the match length.

**Live and offline**
- Live provisional points.
- Offline support: snapshot, ETag/304, If-Match → 409.

**Leagues**
- Private leagues join by code only.
- Code format: TL- plus 6 digits, with # ! @ - between them.
- Max 20 members. The owner can't leave.
- Plus an Overall league.

**Pricing:** per transfer window using the agreed formula, which is already implemented. Don't change it.

---

