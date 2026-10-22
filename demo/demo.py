#!/usr/bin/env python3
"""One-command SyncGuard demo.

    python3 demo/demo.py                   # reset the stack, run the whole demo live, paced for reading
    python3 demo/demo.py --no-pause --yes  # same, unattended and without pauses (used to capture the video)
    python3 demo/demo.py --no-reset        # run against a freshly reset stack that is already up

The reset runs `docker compose down -v`, which DELETES the local demo databases (SyncGuardSource and
SyncGuardDestination). You are asked to confirm unless --yes is given.

Every step runs a real command against the running Docker Compose stack. Output is streamed to the terminal
and also saved with real timestamps to demo/out/transcript.json, which demo/render.py turns into an MP4.
Stops at the first failing command, so a broken run never turns into a video.
"""
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "demo" / "out"


def step(cmd, caption=None, hold=3.0, speed=None, clear=False, hidden=False, expect=(), say=None):
    """speed: playback factor for slow commands in the video (labelled on screen). hidden: run, don't show.
    expect: regexes the real output must contain, so a caption can never claim more than the output shows.
    say: voiceover for this step (used by demo/narrate.py); the video holds the step until it has been spoken."""
    return dict(cmd=cmd, caption=caption, hold=hold, speed=speed, clear=clear, hidden=hidden, expect=list(expect), say=say)


def card(id, duration, say=None):
    return dict(kind="card", id=id, duration=duration, say=say)


def scene(title, *steps):
    return dict(kind="term", title=title, steps=list(steps))


SCENES = [
    card("title", 5.5, say="SyncGuard: reliable data synchronization between SQL Server and external APIs."),
    card("problem", 8.5, say="Orders in SQL Server must reach a partner's API, and those APIs routinely fail: errors, rate limits, timeouts, lost responses. The result is lost orders, duplicates, and no proof that the systems agree."),
    card("approach", 9.0, say="SyncGuard saves each order and its delivery event together, retries failures with growing delays, tags every event with a stable key so repeats are ignored, and reconciles both sides."),
    scene("The demo environment",
          step("sg reset", hidden=True),
          step("docker compose ps --format 'table {{.Service}}\\t{{.Image}}\\t{{.Status}}'",
               "Four containers: SQL Server 2022, the source API, the sync worker and a mock fulfilment API.", hold=3.5,
               expect=[r"sqlserver .*healthy", r"source-api .*Up", r"worker .*Up", r"destination-api .*Up"],
               say="A live run on one machine: SQL Server, the source application, the sync worker, and a mock partner API.")),
    scene("Normal synchronization",
          step("sg order ORD-1001 && sg wait ORD-1001",
               "A new order: it and its outbox event are saved in one SQL transaction, then the worker delivers it.", hold=2,
               expect=[r"HTTP 201", r"ORD-1001 +Delivered"],
               say="A new order is created and delivered."),
          step("sg show ORD-1001", "Delivered on the first attempt (HTTP 201 Created).", hold=2.5,
               expect=[r"ORD-1001 +Delivered +1 +201"]),
          step('sg sql destination "SELECT OrderNumber, CustomerCode, TotalAmount, LEFT(IdempotencyKey, 8) AS IdempotencyKey FROM ReceivedOrders"',
               "The fulfilment database now holds the order, keyed by the source event id.", hold=3, expect=[r"ORD-1001 CUST-042 37\.50"],
               say="It's in the fulfilment database on the first attempt, tagged with its event key.")),
    scene("API failures and automatic retries",
          step("""sg fault '{"fault":"Http503","count":3,"orderNumber":"ORD-2001"}'""",
               "Make the API fail on purpose (a demo-only switch): three HTTP 503 errors for one order…", hold=1.5,
               say="Now the API fails on purpose: three server errors for one order,"),
          step("""sg fault '{"fault":"Http429","retryAfterSeconds":10,"orderNumber":"ORD-2002"}'""",
               "…an HTTP 429 rate limit asking us to wait 10 seconds for another…", hold=1.5,
               say="a rate limit asking another to wait ten seconds,"),
          step("sg order ORD-2001 && sg order ORD-2002 && sg order ORD-2003 'BAD SKU'",
               "…and a third order with data the API will never accept.", hold=2,
               say="and a third with data the API will never accept."),
          step("sg wait ORD-2001 ORD-2002 ORD-2003", "Waiting while the worker retries. No operator involvement.", hold=1.5,
               say="The worker retries on its own."),
          step("sg logs ORD-2001 ORD-2002 ORD-2003",
               "Logs from both systems, linked by event id: growing backoff with jitter, and Retry-After honoured exactly.", hold=5.5,
               expect=[r"Attempt 3 failed \(HTTP 503\)", r"retrying in 10000 ms, honoring Retry-After: True"],
               say="Both systems log the same event ID. The delay grows after each error, and the rate-limited order waits exactly ten seconds."),
          step("sg show ORD-2001 ORD-2002 ORD-2003",
               "Valid orders delivered. The invalid one is parked for manual review after one attempt, not retried forever.", hold=4,
               expect=[r"ORD-2001 +Delivered +4", r"ORD-2002 +Delivered +2", r"ORD-2003 +ManualReview +1 +422"],
               say="The valid orders arrive. The invalid one is set aside for review after one attempt, not retried forever.")),
    scene("Lost responses and duplicate prevention",
          step("""sg fault '{"fault":"LostResponse","orderNumber":"ORD-3001"}'""",
               "The hardest case: the API saves the order, then the response never arrives.", hold=2,
               say="The hardest case: the API saves the order, but the response is lost."),
          step("sg order ORD-3001 && sg wait ORD-3001", hold=1),
          step("sg logs ORD-3001",
               "The worker cannot know it arrived, so it retries with the same key. The API returns the original result.", hold=5.5,
               expect=[r"persisted, dropping the response", r"Duplicate request for order ORD-3001", r"Delivered after 2 attempt"],
               say="The worker can't know it arrived, so it retries with the same key, and the API returns its original result."),
          step("sg replay ORD-3001 2", "Deliberately sending the same event twice more: the same record comes back each time.", hold=3.5,
               expect=[r"send #2 .*HTTP 200 .*\"replayed\":true"],
               say="Sending it twice more gives back the same record."),
          step("""sg sql destination "SELECT COUNT(*) AS RowsForOrder FROM ReceivedOrders WHERE OrderNumber = 'ORD-3001'\"""",
               "Exactly one order in fulfilment, guaranteed by a unique constraint in SQL Server.", hold=2.5, expect=[r"^1$"],
               say="Exactly one copy, guaranteed by a unique constraint.")),
    scene("1,000 orders, 150 injected faults and a crashed worker",
          step("sg chaos", "Arm 150 faults spread across the next requests: 503s, 429s, lost responses and 500s.", hold=2,
               expect=[r"Http503 x60", r"Http429 x30", r"LostResponse x40", r"Http500 x20"],
               say="Next: a thousand orders, with a hundred and fifty failures mixed in."),
          step("sg seed 1000 2026", "Create 1,000 synthetic orders.", hold=2, expect=[r'"created":1000']),
          step("sleep 2 && docker compose kill -s SIGKILL worker && docker compose up -d --no-deps worker",
               "Two seconds in, kill the worker process outright and start a replacement.", hold=1.5, expect=[r"Killed", r"Started"],
               say="Two seconds in, the worker is killed outright."),
          step("sg watch", "Status turns Stale once the heartbeat stops. The replacement takes over the expired lease and clears the backlog.",
               hold=4, speed=5, expect=[r"\bStale\b", r"Healthy .* 0 +1004 +1 "],
               say="Its status turns stale. When the old lease expires, a replacement takes over and clears the backlog from the database.")),
    scene("Monitoring and reconciliation",
          step("sg status", "Operational status, read from SQL Server: worker health, backlog, retries, last successful sync.", hold=4.5, clear=True,
               say="The status endpoint shows worker health, the backlog, retries, and the last sync."),
          step("sg reconcile",
               "None missing, none duplicated. The one discrepancy is expected: ORD-2003, the invalid order parked for manual review.",
               hold=5.5, clear=True, expect=[r"matched 1004 \| in flight 0 \| missing 0 \| mismatched 0 \| undelivered 1 \| unexpected 0 \| duplicated 0"],
               say="Reconciliation compares every order. None are missing, none duplicated. The one discrepancy is expected: the invalid order awaiting review."),
          step("""sg sql destination "DELETE ReceivedOrders WHERE OrderNumber = 'SEED2026-00001'; UPDATE ReceivedOrders SET TotalAmount = TotalAmount + 1 WHERE OrderNumber = 'SEED2026-00002'\"""",
               "Now tamper with the fulfilment database: delete one order and change another's total.", hold=2.5,
               say="Now we delete one order downstream, and change another's total."),
          step("sg reconcile", "Both problems are found and named.", hold=5.5,
               expect=[r"Missing +SEED2026-00001", r"Mismatched +SEED2026-00002 +Differs in: totalAmount"],
               say="Both are found and named.")),
    scene("Automated tests",
          step("sg test", "34 automated tests run against a real SQL Server 2022 started just for the test run.", hold=3, speed=15, clear=True,
               expect=[r"Total tests: 34", r"Passed: 34"],
               say="Finally, the automated tests run against a real SQL Server. All thirty-four pass."),
          step(r"grep -oP 'Passed SyncGuard\.Tests\.AcceptanceTests\.\K\w+' demo/out/test.log | sort",
               "From that run's log: the 15 end-to-end scenarios that passed (the other 19 are unit tests).", hold=6, clear=True,
               expect=[r"S5_lost_response", r"S6_restarting_the_worker", r"S9_one_thousand_orders"])),
    card("close", 9.0, say="SyncGuard delivers at least once, and idempotency keys prevent double bookings. This version runs one active worker with demo-grade security. The code and tests are on GitHub."),
]

BOLD, DIM, CYAN, GREEN, RESET = "\033[1m", "\033[2m", "\033[36m", "\033[32m", "\033[0m"


def run(cmd, echo):
    env = {**os.environ, "PATH": f"{ROOT / 'demo'}{os.pathsep}{os.environ['PATH']}", "PYTHONUNBUFFERED": "1"}
    start = time.monotonic()
    lines = []
    with subprocess.Popen(["bash", "-c", cmd], cwd=ROOT, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True) as p:
        for line in p.stdout:
            lines.append([round(time.monotonic() - start, 3), line.rstrip("\n")])
            if echo:
                print(line, end="", flush=True)
    if p.returncode != 0:
        sys.exit(f"\nDemo stopped: command failed with exit code {p.returncode}: {cmd}")
    return lines, round(time.monotonic() - start, 3)


def main():
    pause = "--no-pause" not in sys.argv
    reset = "--no-reset" not in sys.argv
    if reset and "--yes" not in sys.argv:
        answer = input("This resets the Docker stack and DELETES the local demo databases. Continue? [y/N] ")
        if answer.strip().lower() not in ("y", "yes"):
            sys.exit("Cancelled. Nothing was changed.")
    transcript = {"recordedAtUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"), "scenes": []}
    number = 0
    for sc in SCENES:
        if sc["kind"] == "card":
            transcript["scenes"].append(sc)
            continue
        number += 1
        print(f"\n{BOLD}{CYAN}== {number:02d} · {sc['title']} =={RESET}")
        recorded = []
        for st in sc["steps"]:
            if st["hidden"]:
                if reset or st["cmd"] != "sg reset":
                    print(f"{DIM}(setup: {st['cmd']}){RESET}", flush=True)
                    run(st["cmd"], echo=True)
                continue
            if st["caption"]:
                print(f"{DIM}# {st['caption']}{RESET}")
            print(f"{GREEN}${RESET} {BOLD}{st['cmd']}{RESET}", flush=True)
            lines, duration = run(st["cmd"], echo=True)
            output = "\n".join(text for _, text in lines)
            missing = [rx for rx in st["expect"] if not re.search(rx, output, re.MULTILINE)]
            if missing:
                sys.exit(f"\nDemo stopped: output of '{st['cmd']}' does not show {missing}; its caption would be untrue.")
            recorded.append({**st, "lines": lines, "duration": duration})
            if pause:
                time.sleep(st["hold"])
        transcript["scenes"].append({**sc, "number": number, "steps": recorded})

    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "transcript.json").write_text(json.dumps(transcript, indent=1))
    print(f"\n{BOLD}Demo complete.{RESET} Transcript: {OUT / 'transcript.json'}  (render: python3 demo/render.py)")


if __name__ == "__main__":
    main()
