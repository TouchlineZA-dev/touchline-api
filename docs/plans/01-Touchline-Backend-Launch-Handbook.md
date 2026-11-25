# Touchline backend launch handbook

**Release objective:** A PSL supporter can follow a match as it unfolds. A fantasy manager can enter a named team, make a decision before the deadline, follow its effect during play and understand the final result. The same football facts must power both experiences.

**Start with one completed PSL gameweek.** Choose two reference squads, hand-score them, and preserve the supporting fixture data under your provider agreement. Use that gameweek to test the worker, API, standings and mobile contract together. Before opening the competition publicly, run two consecutive live PSL gameweeks in staging or closed beta, complete the recovery exercises below, and obtain a security release decision.

This is a backend delivery plan dated **23 November 2026**. It covers Sportmonks ingestion, Supabase/Postgres, the ASP.NET API, news and backend operations. The mobile team owns screens, device permissions and native callbacks. Your team owns the contracts, data, authentication configuration, permissions and evidence those screens rely on. Items marked **proposed** are design decisions or acceptance targets for the team to approve. Each gate ends with evidence to attach to the release record.

## Architecture and responsibilities

**Supabase Postgres is the sole handoff between the sync worker and the Touchline API. Only the Python sync worker calls Sportmonks. The ASP.NET API never calls Sportmonks or the sync worker, including during outages, manual refreshes and settlement.** The API uses durable database records for football facts and sync status. A frontend refresh repeats a read from the product API.

```text
Sportmonks ← fetches from ← Python sync worker → commits facts and sync status → Supabase Postgres
Frontend → calls → ASP.NET Touchline API → reads football facts and writes fantasy results → Supabase Postgres
Frontend → signs in with → Supabase Auth → issues access token for the frontend to present to ASP.NET
```

| Decision or incident | Sync-worker owner | Leruo, ASP.NET owner |
|---|---|---|
| A goal is reassigned | Store the corrected scorer against the stable fixture/event identity and record the revision. | Read the committed revision, recalculate points and publish coherent standings. |
| A fixture is postponed | Store the football status, revised kickoff and observation time. | Apply the published fantasy assignment and deadline policy to that fixture. |
| Upstream data is delayed | Flag the observation gap when possible and reconcile active/recent matches on recovery. | Infer staleness from stored timestamps even if the worker is entirely down. Return the last confirmed state and hold affected settlement. |
| A gameweek finishes | Persist evidence of completeness, unresolved fields and confirmed zeroes. | Decide whether that evidence satisfies the season rules. Own settlement, appeals and re-publication. |
| A shared field changes | Document its write semantics, nullability and backfill. | Verify how current and previous API versions interpret it. Agree the migration order together. |
| Accounts, teams and alerts | Supply attributable match facts. | Own manager identity, editable usernames, team names, squads, notification preferences and delivery. |
| News | Proposed division: own ingestion and reconciliation of permitted articles with the news-service maintainer. | Own the product-facing news contract and freshness semantics. Confirm the separate news deployment before assigning operations. |

**Architecture acceptance test.** Seed a staging match and its status records, stop the worker and run the API without Sportmonks credentials or a worker URL. Reading the fixture still succeeds. As time passes the response indicates staleness. After the worker resumes and commits a correction, the API picks it up from Postgres. Retain that test with the release evidence.

Role guides: [Sync-worker implementation guide](02-Sync-Worker.md) · [Leruo’s API implementation guide](03-Leruo-API.md).

## Contents

- [Architecture and responsibilities](#architecture-and-responsibilities)
- [Reviewed baseline](#reviewed-baseline)
- [FPL parity decisions](#fpl-parity-decisions)
- [Delivery gates](#delivery-gates)
- [API handoff](#api-handoff)
- [Reference gameweek and acceptance tests](#reference-gameweek-and-acceptance-tests)
- [Matchday operations](#matchday-operations)
- [Release record](#release-record)
- [First implementation tickets](#first-implementation-tickets)

## Reviewed baseline

| Component | Reviewed revision | What exists | First issue to resolve |
|---|---|---|---|
| Python/FastAPI sync worker | [touchline-sync at 4c550f7][worker] | Scheduled fixture and reference-data ingestion, per-fixture commits, retries, job tracking, readiness reporting and gameweek completeness checks | The recurring fixture job runs hourly. The repo contains both Python and Netlify scheduling paths. Verify which is active in each environment. |
| ASP.NET product API | [touchline-api at 053f047][api] | Supabase token validation, profiles, football lists, squad management, transfers, chips, scoring, pricing, overall and private leagues | First half and half-time fall through to scheduled. Finalisation can proceed when every fixture is final even if the worker has incomplete data. |
| News service | [Touchline_Main at 790fc10][news] | Node/Prisma service with articles, categories, team tags, filtering, pagination and scheduled GNews ingestion when configured | A multiword category can be stored as a name and requested as a slug. Pick one supported news contract and confirm the production ingestion path. |
| Supabase/Postgres | [Worker migrations][migrations] and [API migration history][apimigrations] | Football facts, users, gameweeks, fantasy squads, entries, transfers, prices and access policies | Reconcile overlapping migration histories and actual deployed grants before applying changes. Verify provider configuration in each Supabase project. |

The earlier review ran **211 API tests and 29 worker tests**, all passing at the reviewed revisions. The lineup type and substitution-direction codes are populated in API main. Preserve those regression tests. The test counts describe those runs only. Production throughput, data rights, subscription coverage, real social sign-in and end-to-end settlement still require separate evidence. Earlier local planning notes may refer to older branches. [Confirmed provider codes][codes] · [Worker CI][workerci] · [API CI][apici]

### Runtime ownership

| Boundary | Owner | Contract |
|---|---|---|
| Provider → football database | Sync worker | Translate provider IDs to internal IDs, preserve revisions and record freshness and completeness. |
| Football database → match experience | ASP.NET API | Serve one definition of status, score, event order, lineups and standings to every supported client. |
| Supabase Auth → manager profile | Supabase and ASP.NET | Supabase authenticates. ASP.NET maps its verified subject to one manager and authorises each operation. |
| Manager decision → fantasy result | ASP.NET fantasy services | Own budget, eligibility, deadline, locked entry, transfers, scoring version and rankings. |
| News provider → supported feed | News service and API owners | Publish a documented feed with source attribution, stable club identifiers and consistent filters. |
| Events → devices | Backend delivery worker and mobile client | Backend records preferences and queues pushes. Mobile registers devices and opens the supplied match or article link. |

For a public release, the football page and fantasy page should agree on the scorer and fixture state. They may legitimately differ on *when points become final*. Include both the football update time and the fantasy scoring status in responses.

## FPL parity decisions

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

## Delivery gates

Start a gate from the previous gate’s accepted evidence. A gate owner names the reviewer, records test inputs and links the results to the exact release revision. An unverified production setting stays open until someone checks it. Security controls are part of implementation throughout. Gate 9 provides independent verification.

### Gate 1. Verify PSL coverage and publish the season rulebook

**Owners:** Worker lead and fantasy lead, with product sign-off. **Output:** A PSL coverage matrix, versioned rules and reference gameweek.

1. Sample actual entitled provider responses from several PSL clubs and matchdays, including a quiet match, a postponed match and a fixture with substitutions. Capture the provider endpoint, fixture ID, timestamps, season and observed completeness within agreed retention rights.
2. Record each required fact separately: participants, score, phase, period clock, starters, bench, substitutions, minutes, scorer, assister, rescinded events, cards, team stats and player stats. For saves, defensive actions, shot locations, ratings and xG, verify actual coverage for this competition and plan before advertising the feature. A team total cannot identify which player earned a point. [Worker mapper][mapper]
3. For every field, distinguish confirmed zero, missing value, delayed value and unsupported statistic. Decide which states block settlement and what the public API should show while waiting.
4. Approve all season rules, including ties, late entries, reassignments, corrections, chip periods and sale price rounding. Pin a rules version to the season and locked entries. Publish a read-only machine-readable rulebook for mobile.
5. Check redistribution, caching, image, article, push and historical retention terms for the actual providers. Record approved use alongside each feature. A news source URL and a licence to republish its full article serve different purposes.

**Pass when:** Every launch points category has an attributable input, an independently calculated example, a missing-data policy and a passing test. A second reviewer can reproduce the two reference teams’ totals from the rulebook.

### Gate 2. Make deployments and the fantasy calendar reproducible

**Owners:** Database and worker leads. **Output:** An installable schema and a stable calendar shared with ASP.NET.

1. Inventory active deploys, scheduler processes, versioned migrations, manually applied SQL, current grants and role ownership in each environment. Choose one migration authority per object and one active scheduler per job. Baseline existing production objects before enabling automated migrations. Preserve existing user IDs and fantasy history.
2. Test migrations on an empty database and a sanitised copy of the previous release. Test with the same restricted roles the worker and API use in production, including through the configured connection pool.
3. Persist an immutable gameweek ID, public number and season. Store fixture assignment and revision history. Today the API groups by SAST kickoff dates and derives the week number by ordering the rows. An inserted early week can change later numbers. Persist assignment decisions when a fixture moves. [Calendar implementation][calendar]
4. Keep UTC instants for kickoff and deadline, and format them for Africa/Johannesburg. Publish the rule for a moved kickoff after a deadline announcement. Define who approves changes to a published week and who gets notified.
5. Rehearse blank and double weeks, newly promoted clubs, unavailable players, midseason club moves and season rollover. The 2026/27 seed documents seven postponed fixtures in one week and two weeks with sixteen fixtures. Use those examples. [Seeded PSL calendar][seed]

**Pass when:** Clean install and upgrade yield the same supported schema, permissions and calendar. One scheduled run produces one logical ingest. A reschedule assigns the fixture exactly once and leaves a previously settled team total reproducible.

### Gate 3. Deliver the live match feed

**Owners:** Worker lead and football API lead. **Output:** A timely, recoverable feed with a documented status for every supported match phase.

- Keep the broad daily reconciliation and rolling fixture refresh. Add a separate, quota-aware loop for active PSL fixtures. Poll for lineups before kickoff, update during play and reconcile again after full time. Calculate the calls required for the busiest overlapping fixtures, including pagination, retries and recovery.
- Map scheduled, first half, half-time, second half, stoppage, interrupted, postponed, abandoned and finished states explicitly. Where supported, handle extra time, shootouts and awarded outcomes. Preserve unknown provider codes for review. Test first-half scores explicitly because the reviewed API only marks the second-half code as live. [Status mapping][status] · [Sportmonks states][smstates]
- Use provider IDs and revisions to apply fixtures, events and lineups safely more than once. A correction may amend the scorer, remove an event or leave the score unchanged. Reconcile the authoritative event list when the provider confirms it is complete. A partial response must preserve previously known facts until reconciled.
- Record provider timestamps where available, first observation time, commit time and API publication time. If only observation time is available, label the measured interval accordingly. Expose score, phase, clock source, last update and stale status.
- On restart, discover active and recently finished fixtures, fetch full snapshots, then resume normal polling. Sportmonks’ latest-updated livescore window is ten seconds and observes particular fixture fields. An extended outage or an event-only correction needs a recovery path. [Sportmonks latest updates][smlive]

**Pass when:** Replay kickoff → first-half goal → half-time → second half → equaliser → goal reversal → full time. Interrupt the worker for five minutes, restart it and verify timeline, score and participant IDs converge. As an initial target, publish 95% of measurable provider updates within 30 seconds when live coverage is available. Report upstream lag separately.

### Gate 4. Publish the match centre, news and alerts

**Owners:** Football API, news and delivery leads. **Output:** Supported read endpoints and event delivery contracts.

| Product surface | Backend work | Acceptance example |
|---|---|---|
| Fixtures and match detail | Return by date, competition and club. Provide phase, score, clock, events, lineups, substitutions and supported team/player statistics. Supply stable IDs and ordered, paginated events. | A phone reopening at half-time gets the same score and timeline as a continuously connected client. A scheduled match has an unknown score until appropriate. |
| Standings and club pages | Provide one table including position, played, points, goal difference, deductions and tie-break state. Document whether data comes from official standings or fixture-derived values and how discrepancies are resolved. | An awarded match or points deduction is reflected in the table without inventing a goal event. |
| Player detail and search | Return stable identity, current club, position, eligibility, recent minutes, availability source, upcoming fixtures and points history. Resolve duplicate names and club changes by ID. | Two players sharing a surname have distinct profiles. A club move changes future eligibility while historical match details keep the original club. |
| News | Keep an authoritative category ID/slug mapping, team tagging, canonical article URL, publication time, source and paging. Retain source attribution. Treat provider outage and empty categories separately. | Filtering by `match-reports` returns articles stored in “Match Reports”. Importing the same article twice yields one item. [News query implementation][newsquery] |
| Follows and alerts | Store opt-in by manager/device, club/player/match and event type. Write the committed football revision and a pending notification together, then dispatch asynchronously. Retry transient failures, expire invalid device tokens and suppress duplicates. | Following both a club and its fixture yields one goal alert per device. A rescinded goal generates a correction. Muting or opting out suppresses subsequent alerts. |
| Richer match coverage | Evaluate shot maps, ratings, commentary and advanced statistics separately by PSL coverage and rights. Publish capability flags per fixture. | The app can distinguish unavailable data from an actual zero. [FotMob matchday reference][fotmob] |

**Pass when:** The mobile developer can complete a matchday using staging APIs alone, including following a club, opening a match, reading the timeline and lineup, receiving a corrected alert and filtering club news.

### Gate 5. Ship identity, editable usernames and named teams

**Owners:** Supabase configuration and ASP.NET account services. **Output:** A manager can move between supported sign-in methods and devices while preserving their team.

**Current behaviour.** The API validates Supabase bearer tokens, maps the verified subject to an app profile and creates a generated username on first access. Team creation requires a trimmed name of 1–50 characters and enforces a team per manager per season. The reviewed profile endpoint requires an email claim. Existing Supabase configuration and real provider approvals must be checked in staging. [Profile service][profile] · [Me controller][me] · [Team creation][teamsvc]

1. Configure separate staging and production provider credentials, callback allowlists, application identifiers and mobile return URLs. For Google, verify the intended OAuth clients. For Apple, test a relay address, first authorisation and repeat sign-in. If using Apple’s web OAuth secret, assign its rotation to an owner. For Facebook, test an ordinary user outside developer roles and the required email permission. [Google][google] · [Apple][apple] · [Facebook][facebook]
2. Use the verified Supabase subject as the permanent account key. A username, team name, relay email or device identifier can change. Use Supabase’s verified linking flow for multiple identities. Provide an explicit recovery path for previously unlinked accounts, email conflicts and missing email. [Supabase linking][linking]
3. Add username selection and renaming. Pick length and permitted characters, define Unicode normalisation and case folding, reserve administrative names and enforce a canonical unique key in the database. Return a stable conflict code if another manager wins a simultaneous claim. Make moderation, cooldown and recovery policies visible.
4. Add team renaming by the owner. Keep a permanent team ID per season, record old and new names for moderation, and preserve picks, league memberships and ranks. Proposed default: team display names may repeat; manager handles distinguish them. A fan can browse football before entering fantasy.
5. Add onboarding state, favourite club, notification preferences, account export and deletion. Document the handling of league ownership and historical scores during deletion. Require recent authentication for sensitive changes. Publish only the public handle and team display name on leaderboards.

**Pass when:** A staging user enters via each supported provider, chooses a handle, creates a named team, links an identity and returns on a second device. Renaming handle and team preserves both IDs and standings. Test Apple relay, missing Facebook email, repeated callbacks, expired sessions, conflicting names, deleted and suspended accounts. The mobile owner signs off the callback round trip.

### Gate 6. Protect squad decisions at the deadline

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

### Gate 7. Settle points and rankings from the same revision

**Owners:** Worker and ASP.NET scoring leads. **Output:** A settled week can be rebuilt and explained from durable records.

1. Agree a single state machine. Proposed states are `open`, `locked`, `live`, `awaiting_review` and `final`. Store the business reason, input revision and transition time. The worker persists completeness evidence in Postgres; the API reads those records. The scoring service decides eligibility for settlement using the published rule version.
2. Fix the current disagreement. The worker waits for finished fixtures and the presence of events, lineups and statistics. A genuine zero-event fixture can remain pending, while a single row does not prove the entire feed is complete. The API accepts the worker flag **or** final match states. Define explicit outcomes for verified zero, delayed feed, awarded fixture, abandoned fixture and manual resolution. [Worker completeness logic][completeness] · [API finalisation rule][finalrule]
3. Calculate player points per fixture before aggregating the gameweek. Use on-pitch intervals for the selected minutes and clean-sheet rules. Preserve inputs for assists, cards, substitutions, penalties and reversals. Apply captain fallback and ordinary autosubs after all the player’s eligible fixtures in the week. Record the reason a substitute or multiplier was used.
4. Write provisional scores, final scores and exceptional amendments with distinguishable revisions. Persist a breakdown of player events, team multipliers, bench contribution and transfer deductions. For a large season, calculate in batches and publish a completed revision atomically. Retry or resume an interrupted settlement without double-counting.
5. Produce league totals and ranks from the same revision used on the points page. Specify ties, late joins and membership eligibility. Cache shared rankings, paginate them, and compute “my rank” efficiently. The current league service can load season-wide totals to serve a manager’s leagues. Include this endpoint in load tests. [League service][leaguesvc]
6. Give support a read-only explanation view and operators an audited correction workflow. Preview the impact, record the source, author, reason and previous revision, then publish. Issue a correction message if it changes a previously displayed result.

**Pass when:** Independent expected totals match by player, manager and league. Repeating settlement produces the same scores and rankings. A corrected assist changes only the affected contributions and aggregates. Support can answer “Why did my score drop?” using a saved explanation.

### Gate 8. Prove reliability under matchday and deadline load

**Owners:** Backend operations and mobile integration lead. **Output:** Two accepted gameweek reports, a load report and a timed recovery report.

Use real matchdays to establish provider timeliness. Use replay and load tests for rare combinations such as many goals during a transfer deadline. Keep forecast assumptions, dataset size and hardware with each result. Suggested starting targets:

| Measure | Initial acceptance target | Measure it at |
|---|---|---|
| Live update latency | p95 within 30 seconds of a measurable provider update during live coverage | Provider timestamp where reliable, worker observation, DB commit and public API availability. Report what can actually be observed. |
| API match reads | p95 under 500 ms at twice forecast launch traffic | Public API boundary with warm and cold cache results reported separately. |
| Squad confirmations | p95 under one second at twice forecast deadline traffic; zero lost accepted writes and zero duplicate charges | Client request receipt to durable success response, plus post-test ledger reconciliation. |
| Feed freshness | Alert on missed expected updates during active fixtures | Per-fixture polling heartbeat and last successful reconciliation. No recent goal is a normal match condition. |
| Recovery | Agree maximum data loss and recovery time in writing before rehearsal; record actual results | Restore into an isolated environment, reconcile squads and transfers, replay football facts, then verify standings. |
| Live service availability | Agree a monthly availability objective plus a match-window objective | Synthetic reads and authenticated test actions, with upstream and Touchline failures tagged separately. |

Run a deadline burst, simultaneous matches, stale caches, upstream quota exhaustion, worker restart, database connection pressure and a scoring interruption. Bound retries and queue size. Serve last-known match data with its age during feed failure. Require durable acceptance before acknowledging a squad write. Assign every page and alert to someone who can respond.

**Pass when:** Two consecutive PSL weeks complete; mobile integration passes using published contracts; all unexplained score discrepancies are resolved; recovery and rollback meet the agreed targets. A second operator can carry out the runbook.

### Gate 9. Complete independent security and privacy sign-off

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

## API handoff

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

## Reference gameweek and acceptance tests

Build a replay pack with permitted provider samples or synthetic equivalents, fixed kickoff times, a known rules version, two locked squads and expected player/team/league totals. Include full snapshots and the ordered changes that lead to them. The worker tests its mapping, the API tests scoring and the mobile developer consumes the resulting contract. Store provenance and the expected results with the pack. Exclude personal data and credentials.

### Worked example for the proposed FPL-style captain rule

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

### Regression and integration scenarios

| Scenario | Expected invariant |
|---|---|
| Worker ingests the same snapshot three times | One logical fixture and event set. The same player receives points once. |
| A confirmed scorer changes, scoreline stays 1–0 | Match timeline and player points change. Team result stays 1–0. Both public views use the new revision. |
| A red card or goal is rescinded | The affected contribution is removed or replaced. History identifies the original and correcting revisions. |
| A genuine 0–0 match has sparse events | The coverage policy distinguishes confirmed absence from missing data. The week cannot silently settle incomplete or stay pending without an owner. |
| A defender exits after 60 minutes before a later concession | Apply the chosen clean-sheet rule using their time on the pitch. Record the event times. |
| Captain misses one of two fixtures | Evaluate captain minutes across both fixtures before awarding the vice multiplier. |
| First bench player would invalidate the formation | Skip to the next eligible player under the published autosub rules. Test the goalkeeper-only replacement separately. |
| New manager joins in a later gameweek | Entry starts in the intended week, first-deadline transfer policy applies, and no historical points are awarded. |
| Two clients submit the same transfer | One durable outcome, one transfer charge and one new squad version. |
| Identical retry key with a different intended transfer | Reject the conflicting reuse. Preserve the result of the original operation. |
| Repricing occurs between preview and confirm | The manager receives a new quote or a stable conflict response. Bank and picks remain consistent. |
| A deadline coincides with an API restart | Server time and persisted entry determine the outcome. The app can recover the action receipt. |
| A fixture moves into another fantasy week | Points follow the approved assignment and locked entry. Published history records the change. |
| Finalisation stops midway | Previously published revision stays internally consistent. Resume or replay produces one complete new revision. |
| Account links to a second provider or changes name | Stable manager and team IDs retain all picks, points and memberships. |
| A private league fills while two people join | Enforced membership limit, one membership per team, clear responses for both callers. |
| Manager deletes an account while owning a league | Apply the approved ownership succession and retention policy; public standings disclose only the permitted information. |
| Worker or news provider is unavailable | API returns dated last-known data or a documented availability state. Queued work recovers without duplicate alerts or articles. |

### Running the existing suites

Run from the indicated repository in an isolated development or CI environment. These commands exercise existing suites. Cross-service replay, mobile acceptance, load and restore drills are additional release work.

**Sync worker.** Match Python 3.11 and the dummy configuration in the worker CI workflow. Install in an isolated virtual environment. [Worker CI configuration][workerci]

```sh
python -m pip install -r requirements-dev.txt
python -m pytest tests/ -v
```

**ASP.NET API.** Use the .NET 10 SDK with Docker available for the disposable PostgreSQL tests. CI requires database tests to execute. [API CI configuration][apici]

```sh
dotnet restore Touchline.sln
dotnet build Touchline.sln --no-restore --configuration Release
TOUCHLINE_REQUIRE_DB_TESTS=true dotnet test Touchline.sln --no-build --configuration Release
```

**News.** From `touchline-news-api/`, use the locked dependencies and test configuration. Verify an actual scheduled ingestion separately in staging. [News scripts][newsscripts]

```sh
npm ci
npm run build
npm test
```

## Matchday operations

### Before the first kickoff

- Confirm the correct PSL season, fixture list, published deadline, timezone and fixture assignments. Review postponements and any unresolved player identity or eligibility issues.
- Check recent successful provider requests, request allowance, queue backlog, DB capacity, service versions and migrations. Verify the active scheduler. A healthy server with a stale feed still needs attention.
- Verify synthetic match reads and a staging manager’s transfer preview. Use a separate test environment for actions that alter teams. Confirm the on-call primary, backup, communications channel and support escalation route.
- At the deadline, reconcile accepted action receipts with locked entries. Escalate mismatches before publishing competitive rankings.

### During play and settlement

| Signal or incident | Operator action | Evidence of recovery |
|---|---|---|
| Feed age rising during an active fixture | Check upstream response, quota, scheduler and commit times. Mark stale, preserve the last confirmed score and recover via a full active-fixture snapshot. | Match score, phase, lineup and events converge. Freshness returns to target. |
| One upstream response regresses a match | Quarantine or reconcile the ambiguous change against authoritative details. Preserve original observation and latest published revision. | Correct score and event history are visible to both football and fantasy consumers. |
| Duplicate or misleading pushes | Pause the affected event type, inspect pending deliveries and deduplication state, then resume from a known revision. | One intended delivery per subscription/device and a correction where appropriate. |
| Deadline write backlog | Monitor database pool, transaction duration and accepted receipts. Give clients a clear pending or failed state. Follow the published fairness policy for any exceptional deadline change. | Every accepted action has a durable result. Failed attempts can be explained. |
| Final whistles but insufficient scoring data | Keep the week under review. Inspect missing fixture facts and provider status. Route exceptions to a named operator and communicate the expected next update. | Each fixture has evidence of completeness or a recorded, authorised decision. |
| Correction after publication | Calculate the proposed change in isolation, compare affected players/teams/ranks and obtain the required approval. Publish the amendment as one revision. | Support can explain what changed, why, when and by how many points. |
| Release causes errors | Pause affected jobs, use the tested application rollback or forward-fix path compatible with the schema, restore normal work and reconcile pending operations. | Smoke tests pass, no accepted write is missing, and stale or delayed data have caught up. |

Capture scheduled versus actual deadline, final fixture time, settlement time, corrections, disputes, availability, quota use and peak load after each gameweek. Use that record to improve provider polling and identify sources of repeated support tickets.

## Release record

Fill this in for a specific release candidate. An unchecked box is pending evidence. A signed gate links to a dated report or reproducible test result for the named environment.

| Gate | Evidence to attach | Owner / reviewer | Result / date |
|---|---|---|---|
| 1. Coverage and rules | Coverage matrix, rights decisions, signed rules version, two reference teams | — | Pending |
| 2. Database and calendar | Clean/upgrade comparison, grants, active scheduler and reschedule test | — | Pending |
| 3. Live feed | Timed match replay, outage recovery and first-half regression | — | Pending |
| 4. Match centre and news | Consumer contract test, news filters, follows and corrected alert | — | Pending |
| 5. Accounts | Three-provider staging results, rename race, linking and deletion | — | Pending |
| 6. Fantasy writes | Parallel deadline submissions, rejected stale quote, reconciled receipts | — | Pending |
| 7. Settlement | Independently scored results, second run, late correction and rank check | — | Pending |
| 8. Operations | Two live gameweek reports, load test, timed restore and mobile acceptance | — | Pending |
| 9. Security | Role matrix results, reviewed findings and retests, privacy and release decisions | — | Pending |

- [ ] Record worker, API, news and mobile-consumer revisions, schema version, season rule version and release environment.
- [ ] Attach the provider coverage assumptions and operational limits used in rehearsal.
- [ ] Record the on-call staff, approver, launch window in SAST, fallback plan and escalation contacts.
- [ ] Record remaining accepted risks, owner and review date. Tell the mobile team which capabilities are enabled.
- [ ] Record **go**, **hold**, or **rollback**, who decided, and when. A hold identifies the next failing gate and the evidence needed to reopen it.

## First implementation tickets

| Order | Ticket | Deliverable for review |
|---|---|---|
| 1 | Pick a historical PSL gameweek and two reference squads | Permitted sample or replay data, timestamps, player mapping and independently calculated points. |
| 2 | Resolve the product rules that affect those scores | Signed captain/vice, bench, transfer, chip, sale value and correction policies, with rules version. |
| 3 | Identify the deployed scheduler and authoritative schema owner | Environment inventory, migration baseline and one owner per production job. |
| 4 | Fix the first-half and half-time mapping | One provider-state matrix exercised by match and scoring tests. |
| 5 | Agree the worker/API completeness contract | A finished but incomplete game stays under review. A verified quiet match can settle. Both have regression tests. |
| 6 | Publish a first frontend contract | Staging match and manager examples, ownership rules, proposed profile/team rename responses and freshness fields. |

Attach evidence from the actual candidate and environment to the release record. Each role guide includes its first tickets and ownership checklist.

[worker]: https://github.com/TouchlineZA-dev/touchline-sync/tree/4c550f7
[api]: https://github.com/TouchlineZA-dev/touchline-api/tree/053f047
[news]: https://github.com/TouchlineZA-dev/Touchline_Main/tree/790fc10/touchline-news-api
[migrations]: https://github.com/TouchlineZA-dev/touchline-sync/tree/4c550f7/db/migrations
[apimigrations]: https://github.com/TouchlineZA-dev/touchline-api/tree/053f047/docs/migrations
[codes]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Business/SportmonksCodes.cs
[workerci]: https://github.com/TouchlineZA-dev/touchline-sync/blob/4c550f7/.github/workflows/ci.yml
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
[mapper]: https://github.com/TouchlineZA-dev/touchline-sync/blob/4c550f7/app/mappers/fixture_mapper.py
[calendar]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Services/GameweekService.cs
[seed]: https://github.com/TouchlineZA-dev/touchline-sync/blob/4c550f7/db/migrations/V036__seed_gameweeks_2026_27.sql
[status]: https://github.com/TouchlineZA-dev/touchline-api/blob/053f047/src/Touchline.Api/Business/MatchStatus.cs
[smstates]: https://docs.sportmonks.com/v3/definitions/states
[smlive]: https://docs.sportmonks.com/v3/endpoints-and-entities/endpoints/livescores/get-latest-updated-livescores
[newsquery]: https://github.com/TouchlineZA-dev/Touchline_Main/blob/790fc10/touchline-news-api/src/services/news.service.ts
[fotmob]: https://www.fotmob.com/en-GB/topnews/3949-premier-league-is-back-2
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
[newsscripts]: https://github.com/TouchlineZA-dev/Touchline_Main/blob/790fc10/touchline-news-api/package.json
