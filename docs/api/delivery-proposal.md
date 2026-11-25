# Proposal: devices, follows and goal alerts (decision for the team)

**Status:** proposal. Nothing is built.

**Owners:** the mobile client registers devices and handles opening links. The backend stores preferences and queues pushes.

## Contracts (proposed)

| Route | Purpose |
|---|---|
| `POST /api/v1/me/devices` `{ platform: "ios" \| "android" \| "web", token, appVersion }` | Register or refresh a device (idempotent by token) |
| `DELETE /api/v1/me/devices/{id}` | Retire a device. Soft: sets `retired_at`, never deletes |
| `GET/PUT /api/v1/me/follows` `{ clubs: [teamId], players: [playerId], fixtures: [fixtureId] }` | What the manager follows |
| `GET/PATCH /api/v1/me/notifications` | Already built (Sprint 3): goal alerts, deadline reminders, league updates |
| `GET /api/v1/me/sync-state?since=` | For reconnection: the latest match revisions of followed fixtures, the squad version, the newest correction message and the server time, so a phone coming back online fetches only what changed |

## Goal alerts

1. **A Postgres-driven worker** (not the API request path) reads **committed** football facts. When a fixture's event-set revision changes, it diffs the events against the last processed revision, stored per fixture.
2. **Queueing:** for each new goal, it writes one pending notification per (device, event) in the same transaction as the processed revision. A unique key on (device_id, sportmonks_event_id, kind) means a manager following both a club and its fixture still gets **one** alert per device.
3. **Dispatch** is asynchronous:
   - transient failures are retried with backoff;
   - invalid device tokens are expired;
   - muted or opted-out preferences are respected at queue time.
4. **Corrections:** a goal that is rescinded or re-attributed queues a **correction** notification for devices that received the original alert. The kind is `goal_correction`, keyed separately, so it isn't suppressed as a duplicate.

## Tables (proposed migration)

| Table | Columns |
|---|---|
| `app.devices` | id, user_id, platform, token (unique), app_version, last_seen_at, retired_at |
| `app.follows` | user_id, kind, target_id, created_at; unique per (user, kind, target) |
| `app.alert_cursors` | fixture_id, last_revision, processed_at |
| `app.notifications_outbox` | id, device_id, kind, sportmonks_event_id, payload jsonb, status, attempts, created_at, sent_at; unique (device_id, sportmonks_event_id, kind) |

## Decisions needed

1. Which push provider (FCM, APNs direct, or Expo push).
2. Who runs the dispatcher: a separate worker process. The API itself runs no background jobs.
3. Retention of the outbox and device tokens. These belong in the privacy inventory (Sprint 7).
