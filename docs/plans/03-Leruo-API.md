# Leruo’s implementation guide — Touchline API

Leruo owns the C#/ASP.NET service that the frontend calls. Your responsibility starts with committed football facts in Supabase Postgres and ends with a reliable football response or a durable fantasy decision. Supabase Auth issues tokens; ASP.NET validates them and enforces ownership.

**The API never calls Sportmonks or the Python sync worker. Postgres is the sole handoff.** Read stored match facts, freshness timestamps and completeness records. An API request for a missing lineup or a stale match cannot trigger a provider fetch. Return the known facts, their age and a usable status to the client. If required scoring facts are missing, keep affected points provisional.

The reviewed API revision was [053f047](https://github.com/TouchlineZA-dev/touchline-api/tree/053f047). The earlier review ran 211 passing API tests. The repository already contains profile creation, team and squad management, transfers, chips, points, pricing, leagues and an administrative scoring action. The following stages extend that implementation. New routes and numerical performance targets are proposals.

## Your first three tickets

1. **Approve a versioned PSL rulebook.** Resolve captaincy, substitutions, chip periods, transfer banking, sale prices and correction policy. Independently score two sample teams against the worker owner’s reference week.
2. **Build the match-detail contract against Postgres.** Fix first-half and half-time state mapping, expose freshness and return a stable delayed state for unavailable lineups. Test with the worker stopped.
3. **Publish the account/team contract.** Specify editable username, required fantasy team name, renaming behaviour, onboarding state, Supabase identity linking and stable conflict responses.

## Stage 1. Read football facts consistently

Agree the ID domain for fixtures, players and teams in every join. In particular, match events and lineup rows carry Sportmonks player IDs in parts of this schema; resolve them to the internal football player used by a fantasy squad. Keep one implementation of match status and clock interpretation for the scorecard, timeline and fantasy calculations.

| Case in Postgres | API response to define | Regression to run |
|---|---|---|
| First-half fixture with a goal | Live phase and confirmed score, plus kickoff, timing source and update timestamp | The same goal appears in fixture list, detail and scoring input. |
| No confirmed lineup yet | Coverage status plus the last known fixture information | A fixture opens successfully before lineups arrive. |
| Stale match during a worker outage | Confirmed score and last observation time, with stale status based on the match window | Stop the worker. HTTP reads succeed and cause zero outbound football requests. |
| Corrected scorer with unchanged score | Updated timeline, identifiable revision and recomputed provisional player points | Old scorer loses and new scorer gains the right contributions exactly once. |
| Postponement or changed kickoff | Correct football state and a separately resolved fantasy gameweek | A late change cannot rewrite a previously locked entry without a recorded decision. |

The current calendar service derives assignments from local date windows and gameweek numbers from ordering. Persist stable published numbers and intentional fixture assignments. Include server time, target gameweek ID and deadline on relevant writes. Supply official or reconciled PSL standings, including a policy for deductions and awarded results. [Match status][status] · [Calendar][calendar]

**Pass when:** A historical match can be served completely from a populated database, and schedule, lineup and score corrections keep the corresponding views consistent. Measure the delay between a worker commit and API visibility separately from provider delay.

## Stage 2. Establish the season rules

Use **2026/27 FPL Classic** as the behavioural reference. PSL dates, clubs, currency, market size and data coverage need their own settings. Record each choice against a season. A manager should be able to read the rules before picking a squad and later identify which rules were used to score it.

| Capability | FPL reference behaviour | Touchline baseline | Recommended scope |
|---|---|---|---|
| Entry | One manager entry can compete in multiple leagues. Team details can change. | One named team per manager per season. Generated username. | Launch with editable usernames and team names, favourite club and a late-entry policy. |
| Selection | 15 players, budget, club limit, starting XI, bench priorities and legal formations. Autopick assists entry. | R100m, 2 GK, 5 DEF, 5 MID, 3 ATT, three per club and five configured formations. | Retain the PSL budget. Agree all legal formations. Provide a validated first pick and a feasible autopick. |
| Deadline | Managers confirm their team before the gameweek deadline. | Configured for 90 minutes before first kickoff, with stored deadlines available. | Publish the exact instant, protect previously published history and specify when a future deadline may change. |
| Captain and bench | The captain gets double points. Vice takes over if the captain plays zero minutes. Formation-respecting automatic substitutions run at close. | Defaults are 3× captain, 2× vice and 4× with Triple Captain. Ordinary bench players do not automatically substitute. | Adopt familiar captaincy and autosubs if product approves. Test the vice with a captain who appears only in the second fixture of a double week. |
| Transfers | One free transfer per week, banking to five, four-point hits, and free changes before the manager’s first deadline. | One weekly allowance without rollover, four-point hits, implemented transfers. | Add a transfer allowance ledger, preview and late-entry tests. Preserve balances across supported chip transitions. |
| Market prices | Selling price depends on acquisition price. FPL offers daily price-change indicators. | Purchases preserve price. Sales use current price. Valuation runs by transfer window. | Publish a predictable selling policy for launch. Treat demand-driven daily pricing as a later product once PSL volume supports it. |
| Chips | Wildcard, Free Hit, Bench Boost and Triple Captain in each half of 2026/27. Each chip has activation and cancellation rules. | Three chips, each once per season. Free Hit is absent. | Decide entitlements per PSL season segment. If implementing Free Hit, restore its underlying squad and bank at the next deadline. |
| Scoring | Minutes, position-specific goals and clean sheets, assists, saves, penalties, disciplinary actions, defensive contributions and match bonus. | Simpler event and lineup model. The fixture statistics mapper records team-level statistics without a player ID. | Only publish scoring categories with reliable player-level inputs. Confirm assist attribution and on-pitch intervals. Version the points table. |
| Rankings | Live manager ranks and league positions, final gameweek scores, previous-season history. | Provisional points, overall standings and private classic leagues. | Launch with reproducible live and final ranks, stable tie rules, rank movement and historical results. |
| Social competition | Public and private classic leagues, general leagues, head-to-head fixtures and cups. | Private invite codes, join/leave, standings and a default private-league size of 20. | Launch with classic competition and tested membership limits. Add cohort leagues, head-to-head scheduling and knockout brackets in separate releases. |
| Player tools | Watchlists, form, ownership, fixture difficulty, price indicators and gameweek recaps. | Player summaries, prices and availability. | First expose player minutes, fixture history, upcoming opponents and points reasons. Derive form and ownership from published data. |
| Additional modes | Draft gives exclusive ownership within a league. Challenge changes the game by week. | Reviewed API implements the season-long squad game. | Give each later mode its own entry, ownership, deadlines and scoring contracts. Protect Classic histories across releases. |

Primary references: [FPL squad management][fplteam] · [transfers and selling prices][fpltransfers] · [2026/27 chips][fplchips] · [points rules][fplpoints] · [leagues and cups][fplleagues] · [season changes][fplchanges] · [account and game FAQ][fplfaq] · [Draft][fpldraft] · [Challenge][fplchallenge]. Touchline implementation: [rule defaults][rules] · [team transactions][teamsvc] · [chip and transfer rules][chipsvc].

**Pass when:** Product has signed each intentional difference from FPL, the corresponding data exists in Postgres, and the reference squads have independently calculated totals. Before a new rules version takes effect, retain the version used for historical entries.

## Stage 3. Complete account and team onboarding

**Owners:** Supabase configuration and ASP.NET account services. **Output:** A manager can move between supported sign-in methods and devices while preserving their team.

**Current behaviour.** The API validates Supabase bearer tokens, maps the verified subject to an app profile and creates a generated username on first access. Team creation requires a trimmed name of 1–50 characters and enforces a team per manager per season. The reviewed profile endpoint requires an email claim. Existing Supabase configuration and real provider approvals must be checked in staging. [Profile service][profile] · [Me controller][me] · [Team creation][teamsvc]

1. Configure separate staging and production provider credentials, callback allowlists, application identifiers and mobile return URLs. For Google, verify the intended OAuth clients. For Apple, test a relay address, first authorisation and repeat sign-in. If using Apple’s web OAuth secret, assign its rotation to an owner. For Facebook, test an ordinary user outside developer roles and the required email permission. [Google][google] · [Apple][apple] · [Facebook][facebook]
2. Use the verified Supabase subject as the permanent account key. A username, team name, relay email or device identifier can change. Use Supabase’s verified linking flow for multiple identities. Provide an explicit recovery path for previously unlinked accounts, email conflicts and missing email. [Supabase linking][linking]
3. Add username selection and renaming. Pick length and permitted characters, define Unicode normalisation and case folding, reserve administrative names and enforce a canonical unique key in the database. Return a stable conflict code if another manager wins a simultaneous claim. Make moderation, cooldown and recovery policies visible.
4. Add team renaming by the owner. Keep a permanent team ID per season, record old and new names for moderation, and preserve picks, league memberships and ranks. Proposed default: team display names may repeat; manager handles distinguish them. A fan can browse football before entering fantasy.
5. Add onboarding state, favourite club, notification preferences, account export and deletion. Document the handling of league ownership and historical scores during deletion. Require recent authentication for sensitive changes. Publish only the public handle and team display name on leaderboards.

**Pass when:** A staging user enters via each supported provider, chooses a handle, creates a named team, links an identity and returns on a second device. Renaming handle and team preserves both IDs and standings. Test Apple relay, missing Facebook email, repeated callbacks, expired sessions, conflicting names, deleted and suspended accounts. The mobile owner signs off the callback round trip.

## Stage 4. Make writes safe at the deadline

**Owner:** ASP.NET fantasy lead. **Output:** Every accepted manager action is legal, recorded once and attached to the intended gameweek.

| Action | Server behaviour |
|---|---|
| Initial pick and autopick | Validate eligibility, price, budget, player count, club cap, positions, starters, bench order and captaincy. Autopick returns either a valid candidate or an explicit explanation if the pool cannot satisfy the rules. Allow review before saving. |
| Pre-deadline management | Return the target gameweek, its deadline and squad version. Persist the named week on writes. If a client was editing an expired week, return a conflict and the current state. A stale request must never silently edit the following week. |
| Transfer preview | Return incoming and outgoing players, current purchase and sale values, bank before and after, free transfers spent, points hit, and rule violations. Include the price and squad versions used in the quote. |
| Transfer confirmation | Recheck ownership, eligibility, quote, squad version and deadline at one consistent decision point. Atomically write transfers, picks, bank, allowance and squad version. Bind retry keys to the manager, operation and payload. A retry of the same confirmed action returns its original result. |
| Chips | Store each entitlement by season segment and week. Enforce one active chip per week, its activation and cancellation policy, and Free Hit restoration if selected. Record how a chip affects previously charged hits and banked transfers. |
| Lock and carry-forward | Persist an immutable deadline entry with the chosen XI, bench order, captain, vice, active chip, purchase prices and transfer costs. Carry forward the correct squad for the next week. Restrict rivals’ unpublished decisions. |

Use a database-backed concurrent-write check for the final mutation. The existing API exposes squad versions and accepts `If-Match` for lineup and transfer writes. A version checked only before another transaction commits cannot guarantee the outcome. Exercise the actual database transaction, including concurrent first saves, rather than relying solely on sequential unit tests. [Existing fantasy routes][controllers] · [Squad persistence][teamsvc]

**Pass when:** Two devices edit one version simultaneously, a response is lost after commit, a price changes between preview and confirmation, and requests straddle the deadline. In each case the resulting squad is legal; each accepted action has one receipt, and the bank and points hit balance.

## Stage 5. Settle points, rankings and corrections

**Owners:** Worker and ASP.NET scoring leads. **Output:** A settled week can be rebuilt and explained from durable records.

1. Agree a single state machine. Proposed states are `open`, `locked`, `live`, `awaiting_review` and `final`. Store the business reason, input revision and transition time. The worker persists completeness evidence in Postgres; the API reads those records. The scoring service decides eligibility for settlement using the published rule version.
2. Fix the current disagreement. The worker waits for finished fixtures and the presence of events, lineups and statistics. A genuine zero-event fixture can remain pending, while a single row does not prove the entire feed is complete. The API accepts the worker flag **or** final match states. Define explicit outcomes for verified zero, delayed feed, awarded fixture, abandoned fixture and manual resolution. [Worker completeness logic][completeness] · [API finalisation rule][finalrule]
3. Calculate player points per fixture before aggregating the gameweek. Use on-pitch intervals for the selected minutes and clean-sheet rules. Preserve inputs for assists, cards, substitutions, penalties and reversals. Apply captain fallback and ordinary autosubs after all the player’s eligible fixtures in the week. Record the reason a substitute or multiplier was used.
4. Write provisional scores, final scores and exceptional amendments with distinguishable revisions. Persist a breakdown of player events, team multipliers, bench contribution and transfer deductions. For a large season, calculate in batches and publish a completed revision atomically. Retry or resume an interrupted settlement without double-counting.
5. Produce league totals and ranks from the same revision used on the points page. Specify ties, late joins and membership eligibility. Cache shared rankings, paginate them, and compute “my rank” efficiently. The current league service can load season-wide totals to serve a manager’s leagues. Include this endpoint in load tests. [League service][leaguesvc]
6. Give support a read-only explanation view and operators an audited correction workflow. Preview the impact, record the source, author, reason and previous revision, then publish. Issue a correction message if it changes a previously displayed result.

**Pass when:** Independent expected totals match by player, manager and league. Repeating settlement produces the same scores and rankings. A corrected assist changes only the affected contributions and aggregates. Support can answer “Why did my score drop?” using a saved explanation.

### Reference calculation

Assume captain A plays zero minutes across the entire gameweek. Vice-captain B earns six base points. Bench player C has priority, earns four and is eligible to replace A while keeping a legal formation. The other nine starters score 30 in total. The team took a four-point transfer hit.

| Component | Contribution |
|---|---:|
| Other starters | 30 |
| A | 0 |
| B at 2× | 12 |
| Eligible autosub C | 4 |
| Transfer deduction | −4 |
| **Expected team total** | **42** |

If A appears in the second fixture of a double week and earns one point in total, A retains the armband. With the other inputs unchanged, the expected total becomes **34**: 30 + 2 + 6 − 4. C remains on the bench. This paired test catches premature captain fallback and premature substitution. Attach the agreed rules version before using these numbers as release assertions.

## Stage 6. Give the frontend a supported API

Use the reviewed implementation as the baseline. Proposed routes below are candidates for design review. Publish their final spelling, request bodies, errors, auth requirements and examples in a versioned OpenAPI document before mobile depends on them. Give the mobile developer a staging account, stable fixture IDs, two reference managers and a resettable private league.

| Capability | Existing entry point | Proposed change |
|---|---|---|
| Account | `GET /api/v1/me` | `PATCH /api/v1/me` for username/preferences, plus onboarding state and account lifecycle contracts. |
| Team | `GET` and `POST /api/v1/fantasy/team` | Owner rename through `PATCH /api/v1/fantasy/team`. Preserve ID and historical memberships. |
| Squad | `GET` and `PUT /api/v1/fantasy/team/squad`, `PUT /api/v1/fantasy/team/lineup` | Add autopick preview, explicit target week, documented version and retry semantics. |
| Transfers/chips | `POST /api/v1/fantasy/team/transfers`, `POST /api/v1/fantasy/team/chips` | Add transfer preview and transaction receipts. Expose chip entitlements and allowed transitions. |
| Points and rules | `GET /api/v1/fantasy/team/points`, `GET /api/v1/fantasy/team/snapshot` | Add season rulebook, finality state, revision and scoring explanations. |
| Leagues | `/api/v1/fantasy/leagues` supports list/create, join, leave and standings | Add pagination, rank history, visibility rules and a defined ownership transfer/deletion policy. |
| Football | `GET /api/v1/fixtures`, `/teams`, `/players`, `/gameweeks` and `/seasons/current` under the same API prefix | Add fixture detail, canonical standings, club/player detail, coverage flags, freshness and paging. |
| News | Dedicated service exposes `/api/news`, `/api/categories` and `/api/teams/{team}/news` | Agree the public route and deployment owner. Preserve article IDs, attribution, filters and paging if the API exposes a unified news route. Football facts always come from Postgres. |
| Delivery | New product contracts required | Register/retire devices, manage follows and preferences, expose last synced state for reconnection. |

### Example contract for renaming a manager

**Proposed request.** The bearer token identifies the manager. The public handle is the only mutable field in this example.

```http
PATCH /api/v1/me
Authorization: Bearer <staging-access-token>
Content-Type: application/json

{"username":"psl_scout"}
```

**Proposed success and conflict.** A successful write returns the canonical username. A conflict should let the client ask for another handle without exposing the current holder.

```json
{"username":"psl_scout","onboarding":{"needsUsername":false,"needsTeam":true}}
```

```json
{"status":409,"code":"username_taken","title":"Choose another username"}
```

### Example transfer quote

This **proposed** response uses millions of rand with one decimal place. Use decimal or integer tenths for monetary arithmetic on the server. The example assumes one free transfer is available and the manager is buying one player.

```json
{
  "gameweekId": 12,
  "squadVersion": 7,
  "priceRevision": 18,
  "saleProceeds": 6.0,
  "purchaseCost": 6.5,
  "bankBefore": 2.0,
  "bankAfter": 1.5,
  "freeTransfersBefore": 1,
  "freeTransfersAfter": 0,
  "pointsHit": 0
}
```

For confirmation, carry the quote’s versions, target gameweek and an idempotency key. Document response semantics for expiry, changed price, insufficient funds, stale squad, locked gameweek and unauthorised access. Current fantasy routes already return ProblemDetails for several error types. Preserve working status codes for existing clients. Add machine-readable codes where the client would otherwise have to parse English text. [Current response filters][filters]

For private leagues, test tied ranks, concurrent joins at the cap, repeated join requests, owner departure and the difference between a public rank and an unpublished squad. Cache shared standings by scoring revision and page. Keep manager responses scoped to the authenticated account. For goal alerts, consume committed facts from Postgres, persist deduplication state and record a separate correction event when needed. Agree news-service ownership and the public news contract with the ingestion owner.

**Pass when:** A frontend developer can follow one fixture, onboard two managers, enter their squads into one league, make a transfer, view points and explain a rank change using published staging APIs. No manual database edits are needed.

## Stage 7. Operate and secure the release

Run the .NET 10 suite with Docker available so the disposable PostgreSQL tests execute. CI uses the following commands:

```sh
dotnet restore Touchline.sln
dotnet build Touchline.sln --no-restore --configuration Release
TOUCHLINE_REQUIRE_DB_TESTS=true dotnet test Touchline.sln --no-build --configuration Release
```

Record the result alongside the actual API revision. Add PostgreSQL-backed cases for concurrent squad mutations, deadline freezes, settlement interruption and duplicate retries. Replay an entire gameweek and check the public API result. Load-test the most expensive standings request alongside deadline writes, cache misses and concurrent goal updates. [API CI][apici]

**Owners:** Backend lead, independent reviewer and the person responsible for privacy. **Output:** A reviewed configuration, access-test results, remediations and a named go/no-go decision maker.

| Control | Implementation and review evidence |
|---|---|
| Supabase and identity | Pin expected issuer and audience; validate signatures and expiry. Test key rotation, session expiration, suspension and recovery. Confirm production callbacks for each provider. Require MFA for production administrators and protect administrative recovery paths. [JWT guidance][jwt] |
| Ownership and roles | Review every private read and write for anonymous fan, manager, league owner, support, operator, worker and administrator. Several fantasy tables grant broad access to the shared API role through RLS. ASP.NET must check manager ownership and league membership for each action. Check the effective permissions of direct Supabase roles as well. [Current RLS policies][rls] |
| Secrets and configuration | Separate production and staging. Store provider keys, signing material, database credentials and push credentials in managed secrets. Make startup reject incomplete production configuration. Run rotation rehearsals, dependency checks, secret scanning and least-privilege reviews. [Supabase production guidance][security] |
| Public and private data | Cache public match reads separately from authenticated responses. Test that cached squad data, invite codes, emails and device tokens stay private. Keep database diagnostics in restricted logs and return a minimal status on public health endpoints. [Existing health endpoint][health] |
| Abuse and privileged operations | Apply rate and size limits to sign-in-related operations, name changes, league joins, search, transfers and expensive standings reads. Restrict manual sync, scoring, pricing and calendar changes to their assigned roles. Keep actor, timestamp, reason and outcome for consequential operations. |
| Privacy and retention | Inventory personal information, purposes, retention periods, recipients and storage locations. Rehearse access/export, correction, deletion and notification opt-out. Record how backups and historical standings handle deletion. Have the responsible reviewer assess POPIA obligations for the actual service and incident process. [South African Government POPIA reference][popia] |
| Release and recovery | Test restore, rollback, credential rotation, incident escalation and the ability to pause scoring or outbound alerts independently. Record independent findings, owners, remediation and retest evidence. |

**Pass when:** Critical and high-severity findings are resolved and retested. An authorised reviewer approves remaining risks in writing. Production-role tests, backup recovery, privileged-action audit records and privacy workflows pass against the release configuration. The release lead signs the record at the end of this README.

### Your launch evidence

- Real staging callback results for Google, Apple and Facebook, including linking and recovery.
- Accepted contract examples for scheduled, live, stale, postponed and final matches.
- A locked squad, its accepted transfer receipt, scored player breakdowns and the league revision they contributed to.
- Two closed-beta gameweek reports, independent scoring comparison and a support explanation for a correction.
- Deadline load test, production-role access tests, secret rotation and a timed restore in an isolated environment.
- A passing architecture test with the API configured without a worker URL or Sportmonks credentials and the worker offline.

**Joint sign-off:** The worker owner writes a goal and corrects its scorer in Postgres. You demonstrate the right football score and timeline, fantasy contributions and league positions. Stop the worker and verify that reads continue, stale data is visible and finalisation waits for any missing inputs.

[Full launch handbook](01-Touchline-Backend-Launch-Handbook.md) · [Sync-worker guide](02-Sync-Worker.md)

[apici]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/.github/workflows/ci.yml
[fplteam]: https://www.premierleague.com/en/news/2174899/fpl-basics-managing-your-team
[fpltransfers]: https://www.premierleague.com/en/news/2174907
[fplchips]: https://www.premierleague.com/en/news/4679879/whats-happening-with-fpl-chips-in-202627
[fplpoints]: https://www.premierleague.com/en/news/2174909/
[fplleagues]: https://www.premierleague.com/en/news/2174934
[fplchanges]: https://fantasy.premierleague.com/help/new
[fplfaq]: https://fantasy.premierleague.com/help/faqs
[fpldraft]: https://www.premierleague.com/en/news/1245444/fpl-draft-what-you-need-to-know
[fplchallenge]: https://fplchallenge.premierleague.com/
[rules]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Fantasy/FantasyRules.cs
[teamsvc]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Services/FantasyTeamService.cs
[chipsvc]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Fantasy/TransferAndChipRules.cs
[calendar]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Services/GameweekService.cs
[status]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Business/MatchStatus.cs
[profile]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Users/UserService.cs
[me]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Controllers/MeController.cs
[google]: https://supabase.com/docs/guides/auth/social-sign-in/auth-google
[apple]: https://supabase.com/docs/guides/auth/social-sign-in/auth-apple
[facebook]: https://supabase.com/docs/guides/auth/social-sign-in/auth-facebook
[linking]: https://supabase.com/docs/guides/auth/auth-identity-linking
[controllers]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Controllers/FantasyControllers.cs
[completeness]: https://github.com/TouchlineZA-dev/touchline-sync/blob/4c550f7/app/services/gameweek_status_service.py
[finalrule]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Fantasy/GameweekRules.cs#L54-L59
[leaguesvc]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Services/LeagueService.cs
[jwt]: https://supabase.com/docs/guides/auth/jwts
[rls]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/docs/migrations/005_rls_policies_for_api.sql
[security]: https://supabase.com/docs/guides/security/product-security
[health]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Controllers/HealthController.cs
[popia]: https://www.gov.za/documents/protection-personal-information-act
[filters]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Controllers/FantasyFilters.cs
