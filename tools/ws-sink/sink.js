#!/usr/bin/env node
// Howl dev sink: a WebSocket server standing in for the overlay app. Receives Howl batches and reports throughput,
// end-to-end latency (emit -> receive), sequence gaps and the bridge's own stats, split by the bench.run info key.
//
// Usage: node sink.js [--port 7777] [--out summary.json] [--subscribe me,match_info,events] [--seconds N] [--quiet]

const { WebSocketServer } = require('ws');
const fs = require('fs');
const { performance } = require('perf_hooks');

const args = {};
for (let i = 2; i < process.argv.length; i++) {
  const a = process.argv[i];
  if (!a.startsWith('--')) continue;
  const next = process.argv[i + 1];
  if (next === undefined || next.startsWith('--')) args[a.slice(2)] = true;
  else { args[a.slice(2)] = next; i++; }
}

const port = Number(args.port || 7777);
const BUCKETS = 50000; // 0.1 ms buckets up to 5 s
const runs = new Map();
let currentRun = 'default';

const nowMs = () => performance.timeOrigin + performance.now();
const log = (...m) => { if (!args.quiet) console.log(new Date().toISOString().slice(11, 19), ...m); };

function stats(name) {
  if (!runs.has(name)) {
    runs.set(name, {
      name, messages: 0, events: 0, bytes: 0, gaps: 0, infoValues: 0,
      hist: new Uint32Array(BUCKETS + 1), maxLatency: 0, first: null, last: null, bridge: null, eventNames: {},
    });
  }
  return runs.get(name);
}

function percentile(hist, total, p) {
  if (total === 0) return null;
  const target = Math.ceil(total * p);
  let seen = 0;
  for (let i = 0; i < hist.length; i++) {
    seen += hist[i];
    if (seen >= target) return i / 10;
  }
  return BUCKETS / 10;
}

function summary() {
  const out = [];
  for (const s of runs.values()) {
    const seconds = s.first && s.last ? Math.max(0.001, (s.last - s.first) / 1000) : 0;
    out.push({
      run: s.name,
      seconds: +seconds.toFixed(1),
      messages: s.messages,
      events: s.events,
      events_per_s: seconds ? Math.round(s.events / seconds) : 0,
      kb_per_s: seconds ? +(s.bytes / 1024 / seconds).toFixed(1) : 0,
      seq_gaps: s.gaps,
      info_values: s.infoValues,
      latency_ms: {
        p50: percentile(s.hist, s.events, 0.5),
        p95: percentile(s.hist, s.events, 0.95),
        p99: percentile(s.hist, s.events, 0.99),
        max: +s.maxLatency.toFixed(1),
      },
      bridge: s.bridge,
      top_events: Object.entries(s.eventNames).sort((a, b) => b[1] - a[1]).slice(0, 8),
    });
  }
  return out;
}

function writeSummary() {
  if (args.out) fs.writeFileSync(args.out, JSON.stringify(summary(), null, 2));
}

const wss = new WebSocketServer({ host: '127.0.0.1', port, perMessageDeflate: false });
wss.on('listening', () => log(`howl sink listening on ws://127.0.0.1:${port}`));
wss.on('connection', (ws) => {
  log('game connected');
  let lastSeq = null;
  if (args.subscribe) ws.send(JSON.stringify({ subscribe: String(args.subscribe).split(',') }));

  ws.on('message', (data) => {
    const received = nowMs();
    let m;
    try { m = JSON.parse(data); } catch { log('bad json'); return; }

    if (m.info && m.info.bench && 'run' in m.info.bench) currentRun = m.info.bench.run ?? 'default';
    const s = stats(currentRun);
    s.messages++;
    s.bytes += data.length;
    if (s.first === null) s.first = received;
    s.last = received;
    if (lastSeq !== null && m.seq !== lastSeq + 1) s.gaps += m.seq - lastSeq - 1;
    lastSeq = m.seq;

    if (m.info) {
      for (const cat in m.info) s.infoValues += Object.keys(m.info[cat]).length;
      if (m.info.bridge) s.bridge = m.info.bridge;
    }
    if (m.events) {
      for (const e of m.events) {
        const latency = received - (m.t0 + e.t * 1000);
        s.events++;
        s.hist[Math.max(0, Math.min(BUCKETS, Math.round(latency * 10)))]++;
        if (latency > s.maxLatency) s.maxLatency = latency;
        s.eventNames[e.name] = (s.eventNames[e.name] || 0) + 1;
      }
    }
  });
  ws.on('close', () => log('game disconnected'));
});

setInterval(() => {
  writeSummary();
  if (args.quiet) return;
  const s = runs.get(currentRun);
  if (!s) return;
  log(`[${s.name}] msgs ${s.messages} events ${s.events} gaps ${s.gaps} p95 ${percentile(s.hist, s.events, 0.95)} ms`);
}, 2000);

if (args.seconds) setTimeout(() => { writeSummary(); process.exit(0); }, Number(args.seconds) * 1000);
for (const sig of ['SIGINT', 'SIGTERM']) process.on(sig, () => { writeSummary(); process.exit(0); });
