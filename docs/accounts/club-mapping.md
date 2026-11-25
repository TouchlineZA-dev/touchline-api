# Club ids and the web app's club slugs

**The stable club id** everywhere in the API is `football.teams.id`. It is used for the favourite club (`PATCH /api/v1/me { favouriteClubId }`) and for fixtures and standings. The Sportmonks id is returned alongside it by `GET /api/v1/clubs`, but nothing keys on it.

**Verification:** every id below was checked read-only against the production database on 2026-11-23. Each is a club with a 2026/27 PSL fixture (season id 15).

**For the web app:** existing web selections are stored as slugs. Map each one to the id below when the web client moves to `/api/v1/me`.

| Web slug | `teams.id` | Club (as stored) | Sportmonks id |
|---|---|---|---|
| `amazulu` | 69 | AmaZulu | 6973 |
| `chippa` | 83 | Chippa United | 13547 |
| `durban_city` | 96 | Durban City | 274102 |
| `golden_arrows` | 81 | Golden Arrows | 1555 |
| `chiefs` | 76 | Kaizer Chiefs | 2541 |
| `sundowns` | 28 | Mamelodi Sundowns | 6755 |
| `marumo` | 77 | Marumo Gallants FC | 143574 |
| `pirates` | 50 | Orlando Pirates | 9960 |
| `polokwane` | 62 | Polokwane City | 12946 |
| `richards_bay` | 72 | Richards Bay | 133378 |
| `sekhukhune` | 66 | Sekhukhune United | 251707 |
| `stellenbosch` | 60 | Stellenbosch | 3103 |
| `ts_galaxy` | 65 | TS Galaxy | 228707 |

**Current PSL clubs missing from the web list:** add them to the web picker.

| Club | `teams.id` | Sportmonks id |
|---|---|---|
| Kruger United | 90 | 274104 |
| Milford | 123 | 270332 |
| Siwelele | 146 | 277354 |

**Web slugs with no current PSL club:** these map to `null`. A user who chose one starts with no favourite club and is asked to choose again.

| Web slug | Reason |
|---|---|
| `supersport` | not in the 2026/27 PSL |
| `capetowncity` | not in the 2026/27 PSL |
| `magesi` | not in the 2026/27 PSL |

**Rules:**
- A favourite club must play in the current PSL season. `PATCH /me` rejects any other id with 400.
- `clearFavouriteClub: true` removes it.
- Short codes are missing for five clubs in `football.teams` (Polokwane City, Kruger United, Durban City, Milford, Siwelele). The sync team could fill them in.
