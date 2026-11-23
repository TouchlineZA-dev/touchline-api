// Deadline and matchday load test (k6). NEVER run against production: point BASE_URL at staging,
// with staging tokens for test managers. Usage:
//   BASE_URL=https://staging-api.example TOKENS=tok1,tok2,... LEAGUE_ID=17 GAMEWEEK_ID=12 FIXTURE_ID=1393 \
//   k6 run docs/load/k6-deadline.js
//
// Targets (handbook Gate 8): match reads p95 < 500 ms (warm and cold cache reported separately),
// squad confirmations p95 < 1 s with zero lost or duplicate writes (check the lock report's
// "reconciled" and the receipts count afterwards), standings reads included as the expensive request.
import http from 'k6/http';
import { check } from 'k6';
import { Trend, Counter } from 'k6/metrics';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

const BASE = __ENV.BASE_URL;
const TOKENS = (__ENV.TOKENS || '').split(',').filter(Boolean);
const LEAGUE = __ENV.LEAGUE_ID || 'overall';
const GW = Number(__ENV.GAMEWEEK_ID);
const FIXTURE = __ENV.FIXTURE_ID || '1393';

if (!BASE || BASE.includes('prod')) throw new Error('Set BASE_URL to a staging API (never production).');

const coldMatch = new Trend('match_read_cold_ms', true);
const warmMatch = new Trend('match_read_warm_ms', true);
const confirmMs = new Trend('transfer_confirm_ms', true);
const confirmed = new Counter('transfers_confirmed');
const conflicts = new Counter('transfer_conflicts');

export const options = {
  scenarios: {
    matchReads: { executor: 'constant-arrival-rate', rate: 200, timeUnit: '1s', duration: '5m', preAllocatedVUs: 100, exec: 'matchRead' },
    standings: { executor: 'constant-arrival-rate', rate: 20, timeUnit: '1s', duration: '5m', preAllocatedVUs: 40, exec: 'standings' },
    deadlineWrites: { executor: 'per-vu-iterations', vus: Math.max(1, TOKENS.length), iterations: 3, maxDuration: '5m', exec: 'transfer' },
  },
  thresholds: {
    match_read_warm_ms: ['p(95)<500'],
    match_read_cold_ms: ['p(95)<500'],
    transfer_confirm_ms: ['p(95)<1000'],
    http_req_failed: ['rate<0.01'],
  },
};

export function matchRead() {
  // Cold: a cache-busting query parameter; warm: the same URL with If-None-Match.
  const cold = http.get(`${BASE}/api/v1/fixtures/${FIXTURE}?eventsPage=1&cb=${uuidv4()}`);
  coldMatch.add(cold.timings.duration);
  const warm = http.get(`${BASE}/api/v1/fixtures/${FIXTURE}`, { headers: { 'If-None-Match': cold.headers.Etag || '' } });
  warmMatch.add(warm.timings.duration);
  check(warm, { 'match read ok': (r) => r.status === 200 || r.status === 304 });
}

export function standings() {
  const token = TOKENS[Math.floor(Math.random() * TOKENS.length)];
  const r = http.get(`${BASE}/api/v1/fantasy/leagues/${LEAGUE}/standings?pageSize=50`, { headers: { Authorization: `Bearer ${token}` } });
  check(r, { 'standings ok': (x) => x.status === 200 || x.status === 429 });
}

export function transfer() {
  const token = TOKENS[(__VU - 1) % TOKENS.length];
  const auth = { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' };
  const squad = http.get(`${BASE}/api/v1/fantasy/team/squad`, { headers: auth }).json();
  const outId = squad.players.find((p) => p.position === 'MID').playerId;
  const pool = http.get(`${BASE}/api/v1/players?page=1&pageSize=500`).json().players;
  const inId = pool.find((p) => p.position === 'MID' && p.available && !squad.players.some((s) => s.playerId === p.id)).id;
  const body = { gameweekId: GW, out: [outId], in: [inId] };
  const quote = http.post(`${BASE}/api/v1/fantasy/team/transfers/preview`, JSON.stringify(body), { headers: auth }).json();
  const key = uuidv4();
  const payload = JSON.stringify({ ...body, squadVersion: quote.squadVersion, priceRevision: quote.priceRevision, quotedAtUtc: quote.quotedAtUtc });
  const r = http.post(`${BASE}/api/v1/fantasy/team/transfers/confirm`, payload, { headers: { ...auth, 'Idempotency-Key': key } });
  confirmMs.add(r.timings.duration);
  if (r.status === 200) confirmed.add(1); else if (r.status === 409) conflicts.add(1);
  // Retry with the same key, as a client that lost the response: must return the same receipt, no second charge.
  const retry = http.post(`${BASE}/api/v1/fantasy/team/transfers/confirm`, payload, { headers: { ...auth, 'Idempotency-Key': key } });
  check(retry, { 'retry replays the receipt': (x) => x.status !== 200 || x.json('replayed') === true });
}
