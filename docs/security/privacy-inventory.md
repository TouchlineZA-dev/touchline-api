# Personal information inventory (for the POPIA review)

**Status:** a draft for the person responsible for privacy. **Flagged for a POPIA review** before launch.

| Data | Where | Purpose | Retention (proposed) | Recipients |
|---|---|---|---|---|
| Supabase subject (`auth_user_id`) | `app.users` (Supabase Postgres, region of the project) | the permanent account key | life of the account; kept after deletion, without other personal data, so the old login can't recreate an account | none |
| Email (optional) | `app.users.email`; also Supabase Auth | conflict detection, support contact | until deletion (cleared on `DELETE /me`); Supabase Auth copy until support deletes the Auth user (within 30 days) | Supabase (processor) |
| Username and team name (public) | `app.users`, `app.fantasy_teams` | leaderboards | life of the account; anonymised on deletion | other managers (public leaderboards) |
| Username and team-name history | `app.username_history`, `app.fantasy_team_name_history` | moderation | 12 months, then purge by the database owner (the API never deletes) | support |
| Favourite club, notification preferences | `app.users`, `app.notification_preferences` | personalisation | until changed or deleted (cleared on deletion) | none |
| Squads, transfers, chips, points, receipts, league memberships | `app.fantasy_*`, `app.points_results`, `app.league_members`, `app.fantasy_receipts`, `app.score_revision_teams` | the game, its audit trail and history | season + 2 seasons (historical standings), anonymised by the team rename on deletion | league members see points and ranks only |
| Admin audit log (actor ids) | `app.admin_audit_log` | accountability for privileged actions | 24 months | operators |
| Account suspension reason | `app.users` | abuse handling | while suspended plus 12 months | support |
| Device tokens, follows (proposal only) | none yet (`docs/api/delivery-proposal.md`) | push alerts | to be decided | push provider |
| Server logs | hosting provider | operations, incidents | provider default (set it to 30 days) | ops |

## Rights and flows (Sprint 3)

| Right | How it's met |
|---|---|
| Access / export | `GET /api/v1/me/export` (recent sign-in required) |
| Correction | `PATCH /api/v1/me` (username, favourite club), `PATCH /fantasy/team` |
| Deletion | `DELETE /api/v1/me` anonymises: username, email, preferences and favourite club are cleared; teams are renamed; league ownership is passed on. Then support deletes the Supabase Auth user (`docs/accounts/policies.md`) |
| Opt-out | `PATCH /api/v1/me/notifications` |

## Backups and historical standings

- **Backups:** Supabase backups keep pre-deletion rows until they expire (the plan's retention, e.g. 7 days daily or the PITR window). After a restore, re-apply deletions recorded since the backup: the `deleted_at` rows identify them, and the anonymisation is idempotent.
- **Historical standings:** these keep the anonymised names. Points stay, because other managers' ranks depend on them.

## Open items for the reviewer

1. Lawful basis, and the privacy notice text in the apps.
2. Data residency of the Supabase project and of the hosting.
3. Breach notification procedure (the Information Regulator and affected users), linked to `docs/ops/release-runbook.md` § Incidents.
4. Retention periods above are proposals; confirm or change them.
5. The worker role currently has SELECT on `app.users` (`docs/security/access-matrix.md`); it should be revoked.
