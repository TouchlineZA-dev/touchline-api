# Reference week: GW9 2026/27 (26 Oct – 1 Nov 2026)

**Rules applied:** rulebook `2026-27.v1`, points table `2026-27.points.v1`.

**Why GW9:**
- It is the worker owner's reference week (runbook Part 1b), and their replay pack covers its headline fixture: **Orlando Pirates 2–1 Kaizer Chiefs**, Sportmonks 19781326, our fixture **1393**, kickoff 31 Oct 13:30 UTC.
- All eight GW9 fixtures are finished (state 5) and have full lineups (40–42 rows each) and events (11–17 each).
- It covers the edge cases the rules need:
  - a half-time substitution;
  - stoppage-time goals (90+3, 90+4);
  - a second yellow in stoppage (90+1);
  - a penalty goal (event 16);
  - an unattributed yellow card;
  - a 0–0.
- It is also the last complete week before the event sync stopped (1 Nov).

**Data:** `tests/db/reference/gw9.json`, a read-only snapshot of the eight fixtures as stored in production on 23 Nov 2026: fixtures, teams, lineups, events, and the players they name. The replay pack is `tests/db/replay/fixture_19781326/`.

**How to reproduce:**
1. From the rulebook alone, work out each player's points from the events listed below.
2. Or run the independent script `python tests/db/reference/tools/score_gw9.py`. It was written from the rulebook, not from the C# code.
3. The test `Reference/ReferenceWeekTests.cs` asserts that the production engine gives the same per-player points and team totals.

## GW9 results

| Fixture | Result |
|---|---|
| 1395 | Marumo Gallants 3–2 Siwelele |
| 1388 | Milford 1–0 Sekhukhune United |
| 1393 | Orlando Pirates 2–1 Kaizer Chiefs |
| 1392 | Polokwane City 0–0 TS Galaxy |
| 1391 | AmaZulu 1–0 Golden Arrows |
| 1394 | Stellenbosch 0–2 Chippa United |
| 1389 | Mamelodi Sundowns 4–1 Durban City |
| 1390 | Kruger United 3–1 Richards Bay |

## How each player's points are worked out

For each player, apply these steps in order:
1. **Minutes.** A starter plays from 0. A substitute coming on in minute m starts at m − 1. Going off (sub or red) in minute m ends at m. Otherwise the player plays to 90. Stoppage minutes count as their base minute.
2. **Appearance.** +2 if he started and was on the pitch at the final whistle; otherwise +1 if he played 45+ minutes.
3. **Goals and assists.** +3 per goal (events 14 and 16). +2 per assist (he is the related player of a goal).
4. **Clean sheet.** +2 if he is a GK or DEF with 60+ minutes and his team conceded 0.
5. **Cards and own goals.** −1 per yellow; −2 for a red or second yellow (the yellow isn't also deducted); −2 per own goal.

## Squad A: captain doesn't play (4-4-2, −4 transfer hit)

| | Player | Pos | Club (fixture) | Raw facts | Min | App | G/A | CS | Cards | Pts | × | Counts |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| XI | Siphamandla Hleza | GK | Milford (1388, won 1–0) | started, not subbed | 90 | 2 | – | ✓ +2 | | 4 | 1 | 4 |
| XI | Thabang Matuludi | DEF | Sundowns (1389, won 4–1) | goals 34', 66'; yellow 85' | 90 | 2 | +6 | conceded 1 | −1 | 7 | 1 | 7 |
| XI | Imraan Jones | DEF | Chippa (1394, won 2–0) | goal 83' | 90 | 2 | +3 | ✓ +2 | | 7 | 1 | 7 |
| XI | Ebrahim Seedat | DEF | Kruger (1390, won 3–1) | assist 3'; penalty goal 22' (event 16); off 46' | 46 | 1 | +3 +2 | conceded 1 | | 6 | 1 | 6 |
| XI | Nkosikhona Radebe | DEF | AmaZulu (1391, won 1–0) | assist 65'; off 76' | 76 | 1 | +2 | ✓ +2 | | 5 | 1 | 5 |
| XI | Thapelo Mokobodi | MID | Pirates (1393) | assists 24', 55' | 90 | 2 | +4 | | | 6 | 1 | 6 |
| XI | Ndabayithethwa Ndlondlo | MID | Marumo (1395) | goal 32' | 90 | 2 | +3 | | | 5 | 1 | 5 |
| XI | Marcelo Allende | MID | Sundowns (1389) | assist 34' | 90 | 2 | +2 | | | 4 | 1 | 4 |
| XI | Luyanda Khumalo | MID | Richards Bay (1390) | goal 39'; off 74' | 74 | 1 | +3 | | | 4 | 1 | 4 |
| XI **C** | Oageng Phiri | ATT | Sekhukhune (1388) | unused substitute | 0 | 0 | – | | | 0 | 1 | 0 |
| XI **V** | Samkele Sihlali | ATT | Marumo (1395) | goals 7', 49'; off 71' | 71 | 1 | +6 | | | 7 | **3** | 21 |
| Bench | Olwethu Mzimela | GK | AmaZulu (1391) | started | 90 | 2 | | ✓ +2 | | 4 | 0 | 0 |
| Bench | Mnqobi Mbhele | DEF | Milford (1388) | started | 90 | 2 | | ✓ +2 | | 4 | 0 | 0 |
| Bench | Menzi Chili | MID | Milford (1388) | assist 24'; off 62' | 62 | 1 | +2 | | | 3 | 0 | 0 |
| Bench | Jean Lwamba | ATT | Durban City (1389) | goal 86' | 90 | 2 | +3 | | | 5 | 0 | 0 |

**Team total:**
- The captain played 0 minutes in all of GW9's fixtures, so the vice-captain takes ×3 (rulebook §6).
- There are no automatic substitutions, so the bench doesn't count.
- Starters 4+7+7+6+5+6+5+4+4+0 = **48**. Vice 7 × 3 = **21**. Before hits **69**.
- Minus the 4-point hit: **65**.

## Squad B: Triple Captain (3-4-3, no hit)

| | Player | Pos | Club (fixture) | Raw facts | Min | App | G/A | CS | Pts | × | Counts |
|---|---|---|---|---|---|---|---|---|---|---|---|
| XI | Stanley Nwabali | GK | Chippa (1394, won 2–0) | started | 90 | 2 | | ✓ +2 | 4 | 1 | 4 |
| XI | Harold Majadibodu | DEF | Chippa (1394) | goal 27'; off 76' | 76 | 1 | +3 | ✓ +2 | 6 | 1 | 6 |
| XI | Aubrey Modiba | DEF | Sundowns (1389, conceded 1) | assist 66' | 90 | 2 | +2 | | 4 | 1 | 4 |
| XI | Omega Mdaka | DEF | AmaZulu (1391, won 1–0) | started | 90 | 2 | | ✓ +2 | 4 | 1 | 4 |
| XI | Maliele Vincent Pule | MID | Siwelele (1395) | on 46' (plays 90 − 46 + 1 = 45); goal 55' | 45 | 1 | +3 | | 4 | 1 | 4 |
| XI | Mancoba Zwane | MID | Kruger (1390) | on 89' (plays 2); goal 90+3 (counts as 90) | 2 | 0 | +3 | | 3 | 1 | 3 |
| XI | Givemore Khupe | MID | Sekhukhune (1388) | started | 90 | 2 | | | 2 | 1 | 2 |
| XI | Brooklyn Poggenpoel | MID | Durban City (1389) | started | 90 | 2 | | | 2 | 1 | 2 |
| XI **C (TC)** | Tshepang Moremi | ATT | Pirates (1393) | goal 55' | 90 | 2 | +3 | | 5 | **4** | 20 |
| XI **V** | Yanela Mbuthuma | ATT | Pirates (1393) | goal 24'; off 75' | 75 | 1 | +3 | | 4 | **2** | 8 |
| XI | Pule Mmodi | ATT | Chiefs (1393) | goal 30'; off 82' | 82 | 1 | +3 | | 4 | 1 | 4 |
| Bench | Oscarine Masuluke | GK | Polokwane (1392, 0–0) | started | 90 | 2 | | ✓ +2 | 4 | 0 | 0 |
| Bench | Tlou Nkwe | DEF | Polokwane (1392) | started | 90 | 2 | | ✓ +2 | 4 | 0 | 0 |
| Bench | Sifiso Mdluli | DEF | Milford (1388) | started | 90 | 2 | | ✓ +2 | 4 | 0 | 0 |
| Bench | Ayabulela Maxwele | MID | Golden Arrows (1391) | started | 90 | 2 | | | 2 | 0 | 0 |

**Team total:**
- The captain played, so Triple Captain gives him ×4 and the vice keeps ×2.
- Starters except captain and vice: 4+6+4+4+4+3+2+2+4 = **33**. Captain 5 × 4 = **20**. Vice 4 × 2 = **8**. Total **61**, with no hit: **61**.

## Fixture 1393 replay (the worker's pack), checked step by step in `Reference/ReplayTests.cs`

| Step | What arrives | What the API shows and scores |
|---|---|---|
| 0 (13:00) | Lineups, state 1 | Phase `Scheduled`; score null (not 0–0); `coverage.lineups = available`, `events = pending`; nobody scores |
| 1 (14:00) | Mbuthuma 24', assist Mokobodi; 1–0 | `FirstHalf`, 1–0; the goal's related player shows as the assist; Mbuthuma +3 provisional |
| 2 (15:00) | Mmodi 30'; Lungu on for Ndaba 46'; Moremi 55'; 2–1 | `SecondHalf`; substitution shown at 46 with player on and off |
| 3 (16:00) | Full time 2–1 | Mbuthuma 5, Mokobodi 6, Moremi 5, Ndaba 46 min → 1, Lungu 45 min → 1, Mmodi 5. The pack has only 6 lineup rows and no 75' sub, which is why Mbuthuma gets the full-match +2 here. No completeness row exists, so the week isn't marked complete |
| 4a | The 24' goal re-attributed to Mokobodi, **same** event id | Revision changes; still 3 goals; Mbuthuma 5 → 2; Mokobodi 6 → 7 (goal +3, his 24' assist removed, his 55' assist kept) |
| 4b | The goal reissued under a **new** id; after the worker fix the stale one is rescinded (or deleted) | 3 goals, not 4; score still 2–1; Mokobodi 7, Mbuthuma 2. Tested both ways |

## Notes for the reviewer

- **Prices:** the snapshot has no prices, so squad legality is checked for positions, club cap, formation and bench, with a flat R6.0m price (15 × 6.0 = R90.0m ≤ R100.0m).
- **Mancoba Zwane's 90+3 goal** counts at minute 90, with no effect on points beyond the goal. His 2 minutes are below 45, so he gets no appearance point.
- **Unattributed yellow:** Richards Bay's 62' yellow card has no player (fixture 1390). It changes nothing above, and gives nobody −1.
- **Phatu's handbook reference** uses FPL rules and gives 42 and 34. Our rules give 44 and 41 for the same inputs: see the Sprint 2 report, "Conflicts".
