# Howl SDK

Zero-cost gameplay event emitter for Unity, feeding an Overwolf overlay.

## Install

Package Manager → **+** → **Install package from git URL…**

```
https://github.com/devomage/howl-sdk.git?path=/com.focus.howl.installer
```

The installer checks your setup, installs the core package and opens **Tools → Howl → SDK Manager**, which handles integrations (Opsive UCC) and updates. The installer then removes itself.

## Packages

| Package | Contents |
|---|---|
| `com.focus.howl.installer` | Bootstrapper (Editor only) |
| `com.focus.howl` | Emitter runtime + Howl SDK Manager |
| `com.focus.howl.opsive` | Opsive Ultimate Character Controller 3.3+ integration |
| `com.focus.howl.tools` | Perf recorder + HUD for baseline A/B runs (development) |

All packages are versioned together and released as one tag (`v0.5.0`).

## How it works

The game calls `Howl.Emit(...)` / `Howl.Set(...)`: a struct write into a lock-free ring or a seqlock-guarded info slot, with no allocation, locks or I/O on the main thread. A background worker drains it every ~8 ms, diffs info, and sends GEP-shaped NDJSON batches to:

- **WebSocket** (game is the client, default `ws://127.0.0.1:7777`): the overlay app's server.
- **JSONL file** (optional): replay, debugging, forensics.

With nothing listening, `Emit` returns after one bool check.

Settings: `Resources/HowlSettings` (Create → Howl → Settings), or command line `-howl-ws <url>`, `-howl-no-ws`, `-howl-file`, `-howl-file-dir <dir>`, `-howl-tick <ms>`, `-howl-off`, `-howl-launch` / `-howl-no-launch`.

If no app connects within ~1.5 s, the game opens the app via `howl://launch?ws=…` (only when the protocol is registered, so machines without the app see nothing).

## Dev sink

`tools/ws-sink` stands in for the overlay app and reports throughput, end-to-end latency and sequence gaps:

```
cd tools/ws-sink && npm install
node sink.js --out summary.json
```
