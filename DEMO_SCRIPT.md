# SyncGuard — three-minute demo

The published video is on the [v0.1.0 release](https://github.com/SoniaTasmin/SyncGuard/releases/tag/v0.1.0).
It, and the live version below, come from one scripted, deterministic run against the real Docker Compose stack.

| Goal | Command | Result |
|---|---|---|
| Present live or screen-record yourself | `python3 demo/demo.py` | The scenes in your terminal, with captions and reading pauses |
| Capture a run unattended | `python3 demo/demo.py --no-pause --yes` | `demo/out/transcript.json` (every output line with its real timestamp) |
| Captioned video (silent) | `python3 demo/render.py` | `demo/out/syncguard-demo.mp4` |
| Voiceover clips | `python3 demo/narrate.py` | `demo/out/voice/*.wav` |
| Narrated video | `python3 demo/render.py --narrated` | `demo/out/syncguard-demo-narrated.mp4` and a matching `.srt` |

> ⚠️ **`demo.py` begins with `docker compose down -v`, which deletes the local demo databases** (`SyncGuardSource` and
> `SyncGuardDestination`) and rebuilds the stack. It asks for confirmation unless you pass `--yes`.
> `--no-reset` skips this, but then expects a freshly reset stack (existing order numbers make steps fail, by design).

**Requirements:** Docker with Compose, Python 3.10+, and a `.env` file (`cp .env.example .env`, set a password, keep
`FAULT_INJECTION_ENABLED=true`). Rendering also needs Pillow, the DejaVu fonts (`fonts-dejavu`) and `ffmpeg` with libx264.
A capture takes about 5 minutes: roughly 1 for the reset, 2 for the scenes and 1.5 for the test suite.
All output under `demo/out/` is generated and git-ignored.

## How the video stays honest

* Every on-screen command ran against the live system. `demo/sg` is a thin wrapper around HTTP calls, `docker compose` and
  `sqlcmd`; it reformats responses for readability but never generates results.
* Each caption has `expect` patterns that the real output must contain. If the output doesn't support the caption, `demo.py`
  stops and `render.py` refuses to render. The closing card's test count is read from the recorded test output.
* Output is shown verbatim. Waits of 3 s or more are shortened and labelled, and the crash recovery (5×) and test run (15×)
  are sped up with an on-screen label. The title card shows the recording date and states that the data is synthetic.
* The failures are injected deliberately into a mock API that is part of this repository (see the README section
  *What was demonstrated, what was tested, and what the mock does not prove*).

**The expected discrepancy.** Reconciliation shows `consistent: NO (1 discrepancy)` *before* any tampering. That is
correct and intended: `ORD-2003` was created with an invalid SKU, the destination rejected it (HTTP 422), and it is
waiting in manual review. Reconciliation reports it as `Undelivered` because the systems really do differ until someone
fixes the order. Everything else matches: 1,004 matched, 0 missing, 0 mismatched, 0 duplicated.

## Storyboard (narrated cut, 3:12)

| Time | Scene | Real commands | Point for the viewer |
|---|---|---|---|
| 0:00 | Title | – | What SyncGuard is |
| 0:07 | The business problem | – | Unreliable partner APIs: lost orders, duplicates, no proof |
| 0:23 | The approach | – | Outbox, retrying worker, idempotent delivery, reconciliation |
| 0:35 | 01 Environment | `docker compose ps` | SQL Server 2022 plus three .NET services, live |
| 0:43 | 02 Normal sync | `sg order`, `sg wait`, `sg show`, `sg sql destination` | Delivered on attempt 1; the row is in fulfilment |
| 0:59 | 03 Failures and retries | `sg fault` (503×3; 429 with Retry-After 10 s), `sg order`, `sg wait`, `sg logs`, `sg show` | Growing backoff; Retry-After honoured exactly; invalid data parked after 1 attempt |
| 1:31 | 04 Lost response | `sg fault LostResponse`, `sg logs`, `sg replay`, `COUNT(*)` | The API saved it and the response was lost; the retry is a replay; one row |
| 1:54 | 05 Scale and crash | `sg chaos`, `sg seed 1000`, `docker compose kill -s SIGKILL worker`, `sg watch` | 150 faults plus a killed worker: Stale at 25 s, lease takeover, backlog drains to 0 |
| 2:20 | 06 Monitoring and reconciliation | `sg status`, `sg reconcile`, tamper SQL, `sg reconcile` | The one expected discrepancy; then deleted and altered rows are named |
| 2:44 | 07 Tests | `sg test`, `grep … test.log` | 34/34 pass; the 15 end-to-end scenario names |
| 3:00 | Summary | – | What was shown, v1 limits, repository link |

## Voiceover

The narration text sits next to each step in `demo/demo.py` (`say=`). `render.py --narrated` holds each step on screen
until its sentence has finished, so speech cannot drift ahead of or behind the visuals. A timestamped copy is written to
`syncguard-demo-narrated.srt` and published with the video.

* **Voice:** Kokoro-82M (Apache-2.0 model weights), voice `af_heart`, speed 1.08, run on the CPU by `kokoro-onnx` inside a
  throwaway container with networking disabled.
* **Audio:** mono speech, no music, two-pass EBU R128 loudness normalisation to −16 LUFS (true peak −1.5 dBTP), AAC 160 kb/s.
* **Checks:** every clip was transcribed back with Whisper (`small.en`). The only mismatches were number and spelling
  formatting ("ten" heard as "10", "fulfilment" written "fulfillment"). One line that came back as a different word
  ("enforced" heard as "and forced") was reworded and regenerated.

To record the narration yourself instead, read the `.srt` (or the `say=` lines) over the silent `syncguard-demo.mp4`, or
over `syncguard-demo-narrated.mp4` with its audio muted; the cue times match the narrated cut.

## Recording manually

1. `cp .env.example .env` and set `MSSQL_SA_PASSWORD`. Run `docker compose build` once beforehand so the reset is quick.
2. Terminal: a dark theme, a monospace font at 16–18 pt, about 140×40 characters, and notifications hidden.
3. Start a 1920×1080 screen recorder (OBS, QuickTime, Windows Game Bar or similar) and run `python3 demo/demo.py`.
   Confirm the reset, then trim the first minute (the rebuild) in editing.
4. Each caption is printed as a dim `#` line above its command, followed by a pause long enough to read the output.
5. In editing: add title and closing cards (text in the storyboard), speed up the `sg watch` and `sg test` segments, and
   label them as sped up.

## Talking points (if presenting live)

* "The order and its delivery event commit together, so there is never an order without a pending delivery."
* "We don't claim exactly-once. Requests can repeat; the idempotency key makes repeats harmless, and the database proves it."
* "The worker keeps nothing important in memory. Kill it at any point and the replacement resumes from SQL."
* "Reconciliation is the evidence: it shows the systems agree, and names exactly what's wrong when they don't."
* Limits: one active worker in v1, no auth or TLS, and the API is a mock we control in order to inject faults.
  Many real partner APIs don't accept idempotency keys; those need a different way to detect repeats.
