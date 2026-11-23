"""
Independent scorer for the GW9 reference week (docs/rules/reference-week.md).

Written from the rulebook (docs/rules/rulebook-2026-27-v1.md) only, without the C# code, so a
second reviewer can rerun it: `python tests/db/reference/tools/score_gw9.py`.
Prints each reference player's points with the working, and both team totals.
"""
import json
import pathlib

DATA = json.loads((pathlib.Path(__file__).parent.parent / "gw9.json").read_text(encoding="utf-8"))
GOAL, OWN_GOAL, PEN_GOAL, SUB, YELLOW, RED, SECOND_YELLOW = 14, 15, 16, 18, 19, 20, 21
STARTER = 11
POS = {24: "GK", 25: "DEF", 26: "MID", 27: "ATT"}

players = {p["sportmonks_player_id"]: p for p in DATA["players"]}
fixtures = {f["id"]: f for f in DATA["fixtures"]}
teams = {t["id"]: t["name"] for t in DATA["teams"]}


def score(sm_id):
    """Rulebook §7–§8 for one player across GW9 (each player has one GW9 fixture)."""
    lineup = [l for l in DATA["lineups"] if l["sportmonks_player_id"] == sm_id]
    events = [e for e in DATA["events"] if sm_id in (e["sportmonks_player_id"], e["sportmonks_related_player_id"])]
    fixture_id = (lineup or events)[0]["fixture_id"]
    f = fixtures[fixture_id]
    team = lineup[0]["team_id"] if lineup else events[0]["team_id"]
    length = f["length"] or 90
    started = any(l["type_id"] == STARTER for l in lineup)
    came_on = went_off = None
    goals = assists = own_goals = yellows = 0
    red = second_yellow = False
    work = []
    for e in sorted(events, key=lambda e: (e["minute"] or 0, e["extra_minute"] or 0)):
        m = e["minute"] or 0
        if e["type_id"] == SUB:
            if e["sportmonks_player_id"] == sm_id and came_on is None:
                came_on = 46 if (m == 45 and (e["extra_minute"] or 0) > 0) else m
            if e["sportmonks_related_player_id"] == sm_id:
                went_off = m if went_off is None else min(went_off, m)
        elif e["type_id"] in (GOAL, PEN_GOAL):
            if e["sportmonks_player_id"] == sm_id:
                goals += 1; work.append(f"goal {m}'")
            elif e["sportmonks_related_player_id"] == sm_id:
                assists += 1; work.append(f"assist {m}'")
        elif e["type_id"] == OWN_GOAL and e["sportmonks_player_id"] == sm_id:
            own_goals += 1; work.append(f"own goal {m}'")
        elif e["type_id"] == YELLOW and e["sportmonks_player_id"] == sm_id:
            yellows += 1; work.append(f"yellow {m}'")
        elif e["type_id"] == RED and e["sportmonks_player_id"] == sm_id:
            red = True; went_off = m if went_off is None else min(went_off, m); work.append(f"red {m}'")
        elif e["type_id"] == SECOND_YELLOW and e["sportmonks_player_id"] == sm_id:
            second_yellow = True; went_off = m if went_off is None else min(went_off, m); work.append(f"second yellow {m}'")

    start = 0 if started else (came_on - 1 if came_on is not None else None)
    if start is None:
        minutes = 0
    else:
        start = max(0, min(start, length))
        end = max(start, min(went_off if went_off is not None else length, length))
        minutes = end - start
    full = started and went_off is None
    appearance = 2 if full else (1 if minutes >= 45 else 0)
    conceded = f["away_score"] if team == f["home_team_id"] else f["home_score"]
    position = POS.get(players[sm_id]["position_id"], "?")
    clean = position in ("GK", "DEF") and minutes >= 60 and conceded == 0
    if second_yellow:
        yellows = 0
    total = appearance + 3 * goals + 2 * assists + (2 if clean else 0) - yellows + (-2 if (red or second_yellow) else 0) - 2 * own_goals
    return {
        "name": players[sm_id]["display_name"], "position": position, "team": teams[team], "fixture": fixture_id,
        "minutes": minutes, "appearance": appearance, "goals": goals, "assists": assists, "clean": clean,
        "yellows": yellows, "red": red or second_yellow, "own_goals": own_goals, "total": total,
        "work": ", ".join(work) or "-",
    }


SQUADS = json.loads((pathlib.Path(__file__).parent.parent / "gw9-squads.json").read_text(encoding="utf-8"))
for squad in SQUADS["squads"]:
    print(f"\n## {squad['name']} (chip: {squad['chip']}, transfer cost: {squad['transferCost']})")
    rows = {sm: score(sm) for sm in squad["starters"] + squad["bench"]}
    captain_played = rows[squad["captain"]]["minutes"] > 0
    armband = 4 if squad["chip"] == "triple_captain" else 3
    total = 0
    for sm in squad["starters"] + squad["bench"]:
        r = rows[sm]
        starter = sm in squad["starters"]
        counted = starter or squad["chip"] == "bench_boost"
        mult = 0
        if counted:
            mult = 1
            if sm == squad["captain"]:
                mult = armband if captain_played else 1
            elif sm == squad["vice"]:
                mult = 2 if captain_played else armband
        total += r["total"] * mult
        role = "C" if sm == squad["captain"] else "V" if sm == squad["vice"] else ""
        print(f"{'XI ' if starter else 'BEN'} {role:1} {r['name']:<32} {r['position']:<3} {r['team']:<20} {r['minutes']:>3}' "
              f"app {r['appearance']} | {r['work']:<28} | cs {int(r['clean'])} | pts {r['total']:>3} x{mult} = {r['total'] * mult}")
    print(f"TOTAL before hits {total}, minus {squad['transferCost']} = {total - squad['transferCost']}")
