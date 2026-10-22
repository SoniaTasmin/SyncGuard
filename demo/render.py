#!/usr/bin/env python3
"""Render demo/out/transcript.json (from demo/demo.py) into a captioned 1080p MP4.

    python3 demo/render.py [transcript.json] [output.mp4]
    python3 demo/render.py --stills          # PNG key frames only, for review
    python3 demo/render.py --narrated        # with the voiceover from demo/narrate.py -> syncguard-demo-narrated.mp4 + .srt

Terminal text is the recorded output, verbatim. Typing is animated; output appears at its recorded pace,
long waits are shortened and slow steps are sped up, and both are labelled on screen.
Requires Pillow and ffmpeg (libx264).
"""
import json
import re
import subprocess
import sys
import wave
from datetime import datetime
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from narrate import segments

ROOT = Path(__file__).resolve().parent.parent
W, H, FPS = 1920, 1080, 30
REPO_URL = "github.com/SoniaTasmin/SyncGuard"

BG, PANEL, PANEL_BAR = (11, 16, 32), (15, 22, 38), (30, 41, 59)
TEXT, DIM, WHITE = (226, 232, 240), (130, 145, 168), (248, 250, 252)
ACCENT, GREEN, AMBER, RED, CYAN = (56, 189, 248), (52, 211, 153), (251, 191, 36), (248, 113, 113), (103, 232, 249)

FONTS = "/usr/share/fonts/truetype/dejavu/"
font = lambda name, size: ImageFont.truetype(FONTS + name, size)
MONO, MONO_B = font("DejaVuSansMono.ttf", 21), font("DejaVuSansMono-Bold.ttf", 21)
SANS = {s: font("DejaVuSans.ttf", s) for s in (20, 22, 26, 28, 30, 34, 40)}
SANS_B = {s: font("DejaVuSans-Bold.ttf", s) for s in (20, 22, 26, 28, 30, 34, 44, 56, 120)}

TERM = (64, 96, 1856, 926)          # terminal window
TEXT_X, TEXT_Y, LINE_H = 92, 152, 27
CHAR_W = MONO.getlength("M")
COLS = int((TERM[2] - TEXT_X - 24) // CHAR_W)
ROWS = (TERM[3] - TEXT_Y - 10) // LINE_H
TYPE_CPS, MAX_GAP, FADE = 60, 1.0, 0.35

# Colour only the words that matter; everything else stays neutral.
KEYWORDS = [(re.compile(p), c) for p, c in [
    (r"\bDelivered\b", GREEN), (r"\bManualReview\b|manual review", AMBER), (r"\bWARN\b", AMBER), (r"\bERROR\b", RED),
    (r"^HTTP 20[01]\b|-> +HTTP 200\b", GREEN), (r"\(HTTP (4|5)\d\d\)", AMBER), (r"Retry-After(: True| \d+s)", CYAN),
    (r"\"replayed\":true", CYAN), (r"Duplicate request[^;]*", CYAN), (r"dropping the response", AMBER),
    (r"\bPassed\b|Passed: +\d+|Passed!", GREEN), (r"Failed: +[1-9]\d*|\bFailed\b(?!:)", RED),
    (r"consistent: yes", GREEN), (r"consistent: NO", AMBER), (r"\b(Missing|Mismatched)\b", RED), (r"\bUndelivered\b", AMBER),
    (r"(missing|mismatched|duplicated|unexpected) 0\b", GREEN), (r"(missing|mismatched|duplicated) [1-9]\d*", RED),
    (r"\bKilled\b", RED), (r"\bhealthy\b|\"Healthy\"", GREEN), (r"\bStarted\b", GREEN),
]]
DIM_LINE = re.compile(r"^(\[xUnit|\[testcontainers|  (Host|Server Version|Kernel|API Version|Operating System|Total Memory)|"
                      r"\(\d+ rows affected\)|-{4,}|Test run for|A total of| Container syncguard-(sqlserver|db-init))")
HEADER_LINE = re.compile(r"^(ORDER |TIME |SERVICE |OrderNumber|RowsForOrder)")


def wrap(text, width=COLS):
    text = text.replace("\t", "    ")
    return [text[i:i + width] for i in range(0, max(len(text), 1), width)]


def draw_line(d, x, y, text, base=TEXT, bold=False, keywords=True):
    colours = [base] * len(text)
    for rx, colour in KEYWORDS if keywords else []:
        for m in rx.finditer(text):
            for i in range(m.start(), m.end()):
                colours[i] = colour
    i = 0
    while i < len(text):
        j = i
        while j < len(text) and colours[j] == colours[i]:
            j += 1
        d.text((x + i * CHAR_W, y), text[i:j], font=MONO_B if bold else MONO, fill=colours[i])
        i = j


def pill(d, x, y, text, fg, bg, f=SANS_B[22], pad=14, anchor_right=False):
    w = f.getlength(text) + 2 * pad
    if anchor_right:
        x -= w
    d.rounded_rectangle((x, y, x + w, y + 38), radius=19, fill=bg)
    d.text((x + pad, y + 19), text, font=f, fill=fg, anchor="lm")
    return w


def wrap_words(text, f, max_w):
    lines, cur = [], ""
    for word in text.split():
        trial = f"{cur} {word}".strip()
        if f.getlength(trial) <= max_w:
            cur = trial
        else:
            lines.append(cur)
            cur = word
    return lines + [cur]


# ---------------------------------------------------------------- cards

def card_base(kicker):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, W, 6), fill=ACCENT)
    if kicker:
        d.text((140, 120), kicker.upper(), font=SANS_B[22], fill=ACCENT)
    return img, d


def card_title(meta):
    img, d = card_base(None)
    d.text((140, 300), "PROOF-OF-WORK DEMONSTRATION", font=SANS_B[22], fill=ACCENT)
    d.text((134, 340), "SyncGuard", font=SANS_B[120], fill=WHITE)
    d.text((140, 500), "Reliable data synchronization between SQL Server", font=SANS[40], fill=TEXT)
    d.text((140, 552), "and external REST APIs, even when the API misbehaves.", font=SANS[40], fill=TEXT)
    d.rectangle((140, 640, 260, 644), fill=ACCENT)
    d.text((140, 670), "C# · .NET 10 · SQL Server 2022 · Docker Compose", font=SANS[28], fill=DIM)
    d.text((140, 960), f"Recorded from a live run on {meta['recorded']}. Terminal output is real and unedited;",
           font=SANS[22], fill=DIM)
    d.text((140, 992), "waiting time is shortened and labelled. Synthetic data only.", font=SANS[22], fill=DIM)
    return img


def card_problem(meta):
    img, d = card_base("The business problem")
    d.text((140, 165), "Two systems that must agree, joined by an unreliable API", font=SANS_B[44], fill=WHITE)
    d.text((140, 270), "A wholesaler's orders live in SQL Server. Every order must reach", font=SANS[30], fill=TEXT)
    d.text((140, 312), "an external fulfilment API, and that API sometimes:", font=SANS[30], fill=TEXT)
    for i, item in enumerate(["fails with HTTP 500 or 503", "rate-limits with HTTP 429", "times out or goes offline",
                              "saves the order, then loses the response"]):
        y = 390 + i * 64
        d.ellipse((150, y + 12, 164, y + 26), fill=AMBER)
        d.text((190, y), item, font=SANS[30], fill=TEXT)
    d.rounded_rectangle((1160, 270, 1780, 650), radius=18, fill=(40, 22, 30), outline=(110, 40, 50), width=2)
    d.text((1200, 300), "Without safeguards", font=SANS_B[30], fill=RED)
    for i, item in enumerate(["Orders silently lost", "Orders shipped twice", "No proof the two systems agree"]):
        d.text((1200, 375 + i * 80), "✗  " + item, font=SANS[30], fill=TEXT)
    d.text((140, 760), "These failures are routine for third-party APIs. The question is what happens next.", font=SANS[28], fill=DIM)
    return img


def card_approach(meta):
    img, d = card_base("The approach")
    d.text((140, 165), "How SyncGuard keeps the systems consistent", font=SANS_B[44], fill=WHITE)
    boxes = [("1", "Transactional outbox", "Order and its delivery event saved in one SQL transaction"),
             ("2", "Retrying sync worker", "Backoff with jitter, honours Retry-After, survives crashes"),
             ("3", "Idempotent delivery", "One stable key per event and a unique constraint: no duplicates"),
             ("4", "Reconciliation", "Finds missing, altered or stuck orders; status endpoint for monitoring")]
    bw, gap, x0, y0 = 380, 40, 140, 320
    for i, (n, title, text) in enumerate(boxes):
        x = x0 + i * (bw + gap)
        d.rounded_rectangle((x, y0, x + bw, y0 + 300), radius=18, fill=PANEL, outline=PANEL_BAR, width=2)
        d.ellipse((x + 28, y0 + 28, x + 76, y0 + 76), fill=ACCENT)
        d.text((x + 52, y0 + 52), n, font=SANS_B[26], fill=BG, anchor="mm")
        d.text((x + 28, y0 + 110), title, font=SANS_B[28], fill=WHITE)
        for j, line in enumerate(wrap_words(text, SANS[22], bw - 56)):
            d.text((x + 28, y0 + 170 + j * 36), line, font=SANS[22], fill=TEXT)
        if i < 3:
            ax = x + bw + 6
            d.polygon([(ax, y0 + 140), (ax + 28, y0 + 150), (ax, y0 + 160)], fill=ACCENT)
    d.rounded_rectangle((140, 720, 1780, 820), radius=16, fill=(14, 40, 52))
    d.text((180, 770), "Guarantee: at-least-once delivery with durable idempotency. Nothing silently lost, nothing booked twice.",
           font=SANS_B[26], fill=WHITE, anchor="lm")
    d.text((140, 870), "Not \"exactly-once\": a request may be sent more than once, and the receiving side makes that harmless.",
           font=SANS[26], fill=DIM)
    return img


def card_close(meta):
    img, d = card_base("Summary")
    d.text((140, 165), "What this demonstration showed", font=SANS_B[44], fill=WHITE)
    items = ["Orders synchronized from SQL Server to a REST API, with one transaction per order",
             "HTTP 503 and 429 handled automatically; invalid data isolated for review",
             "Lost responses and deliberate re-sends caught by idempotency: no duplicates",
             "A killed worker replaced mid-run, with the backlog resumed from SQL",
             f"Reconciliation found tampered data; {meta['tests']} automated tests passed"]
    for i, item in enumerate(items):
        y = 280 + i * 70
        d.text((150, y), "✓", font=SANS_B[34], fill=GREEN)
        d.text((205, y + 2), item, font=SANS[30], fill=TEXT)
    d.text((140, 660), "Limits of this version (v1)", font=SANS_B[28], fill=AMBER)
    d.text((140, 708), "One active worker at a time · demo-grade security (no auth or TLS) · synthetic data, local Docker",
           font=SANS[26], fill=TEXT)
    d.rectangle((140, 820, 260, 824), fill=ACCENT)
    d.text((140, 850), "Source code, tests and documentation", font=SANS[28], fill=DIM)
    d.text((140, 896), REPO_URL, font=SANS_B[44], fill=WHITE)
    return img


CARDS = {"title": card_title, "problem": card_problem, "approach": card_approach, "close": card_close}


# ---------------------------------------------------------------- terminal scenes

def term_frame(scene, rows, caption, badge):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle((0, 0, W, 6), fill=ACCENT)
    w = pill(d, 64, 30, f"{scene['number']:02d}", BG, ACCENT, SANS_B[22])
    d.text((64 + w + 16, 49), scene["title"], font=SANS_B[30], fill=WHITE, anchor="lm")
    d.text((W - 64, 49), "SyncGuard  ·  live recording", font=SANS[22], fill=DIM, anchor="rm")

    x0, y0, x1, y1 = TERM
    d.rounded_rectangle(TERM, radius=14, fill=PANEL)
    d.rounded_rectangle((x0, y0, x1, y0 + 42), radius=14, fill=PANEL_BAR)
    d.rectangle((x0, y0 + 28, x1, y0 + 42), fill=PANEL_BAR)
    for i, c in enumerate([(248, 113, 113), (251, 191, 36), (52, 211, 153)]):
        d.ellipse((x0 + 20 + i * 26, y0 + 14, x0 + 34 + i * 26, y0 + 28), fill=c)
    d.text(((x0 + x1) / 2, y0 + 21), "bash: SyncGuard demo", font=SANS[20], fill=DIM, anchor="mm")
    if badge:
        pill(d, x1 - 16, y0 + 2, badge, BG, AMBER, SANS_B[20], pad=12, anchor_right=True)

    for i, (kind, text) in enumerate(rows[-ROWS:]):
        y = TEXT_Y + i * LINE_H
        if kind == "cmd":
            d.text((TEXT_X, y), "$", font=MONO_B, fill=GREEN)
            draw_line(d, TEXT_X + 2 * CHAR_W, y, text, WHITE, bold=True)
        elif kind == "cmd+":
            draw_line(d, TEXT_X + 2 * CHAR_W, y, text, WHITE, bold=True)
        elif kind == "cursor":
            d.text((TEXT_X, y), "$", font=MONO_B, fill=GREEN)
            draw_line(d, TEXT_X + 2 * CHAR_W, y, text, WHITE, bold=True)
            cx = TEXT_X + (2 + len(text)) * CHAR_W
            d.rectangle((cx, y + 3, cx + CHAR_W - 2, y + LINE_H - 4), fill=TEXT)
        else:
            header = bool(HEADER_LINE.search(text))
            draw_line(d, TEXT_X, y, text, DIM if DIM_LINE.search(text) else (CYAN if header else TEXT), keywords=not header)

    if caption:
        lines = wrap_words(caption, SANS[30], 1700)[:2]
        d.rounded_rectangle((64, 946, 1856, 1052), radius=12, fill=(22, 32, 52))
        d.rectangle((64, 946, 72, 1052), fill=ACCENT)
        top = 999 - (len(lines) * 40) / 2
        for i, line in enumerate(lines):
            d.text((100, top + i * 40), line, font=SANS[30], fill=WHITE)
    return img


def term_timeline(scene, voice=None, index=0, clips=None):
    """Yield (t, rows, caption, badge) states for a terminal scene; t is scene-local seconds.
    With narration (voice), a step that has a voice clip starts only once the previous clip has finished,
    and the clip's start time is appended to clips; the final state waits for the last clip to end."""
    rows, caption, t, audio_free = [], None, 0.6, 0.0
    yield 0.0, [], None, None
    for j, st in enumerate(scene["steps"]):
        seg = f"{index:02d}-{j + 1:02d}"
        if voice and seg in voice:
            t = max(t, audio_free)
            clips.append((t, seg))
            audio_free = t + voice[seg]["duration"] + 0.25
        if st["clear"]:
            rows = []
        if st["caption"]:
            caption = st["caption"]
        badge = None
        cmd_rows = [st["cmd"][i:i + COLS - 2] for i in range(0, len(st["cmd"]), COLS - 2)]
        typing = min(max(len(st["cmd"]) / TYPE_CPS, 0.4), 1.6)
        for n in range(1, len(st["cmd"]) + 1):
            typed = st["cmd"][:n]
            parts = [typed[i:i + COLS - 2] for i in range(0, len(typed), COLS - 2)]
            yield t + typing * n / len(st["cmd"]), rows + [("cursor" if k == len(parts) - 1 else ("cmd" if k == 0 else "cmd+"), p)
                                                           for k, p in enumerate(parts)], caption, None
        t += typing + 0.25
        rows = rows + [("cmd" if k == 0 else "cmd+", p) for k, p in enumerate(cmd_rows)]
        yield t, rows, caption, None

        speed = st["speed"] or 1
        prev_real, shortened = 0.0, False
        for real, text in st["lines"]:
            gap = (real - prev_real) / speed
            if gap > MAX_GAP and not st["speed"]:
                gap, shortened = MAX_GAP, True
            prev_real = real
            t += gap
            if st["speed"]:
                badge = f"{speed:g}× speed  ·  {st['duration']:.0f} s real time"
            elif shortened and st["duration"] >= 3:
                badge = f"wait shortened  ·  {st['duration']:.0f} s real time"
            rows = rows + [("out", r) for r in wrap(text)]
            yield t, rows, caption, badge
        reading = len(caption.split()) / 3.2 if st["caption"] else 0
        hold = st["hold"] * (0.7 if voice else 1)  # with a voiceover, the narration sets the pace
        t += max(hold, reading - typing - 0.5)
        yield t, rows, caption, badge
    yield max(t, audio_free), rows, caption, None


def scenes_with_durations(transcript, voice=None):
    """[(scene, duration, states or None for cards, [(local start, voice segment id)])]"""
    out = []
    for i, sc in enumerate(transcript["scenes"]):
        clips = []
        if sc["kind"] == "card":
            seg = f"{i:02d}-00"
            duration = sc["duration"]
            if voice and seg in voice:
                clips.append((0.5, seg))
                duration = max(duration, 0.5 + voice[seg]["duration"] + 0.9)
            out.append((sc, duration, None, clips))
        else:
            states = list(term_timeline(sc, voice, i, clips))
            out.append((sc, states[-1][0] + 0.4, states, clips))
    return out


def build_voice_track(plan, voice, voice_dir, total, wav_path, srt_path):
    """Place every clip at its scene offset in one mono PCM track and write matching subtitles."""
    rate = next(iter(voice.values()))["rate"]
    track = bytearray(2 * int(total * rate + rate))
    cues, offset = [], 0.0
    for _, duration, _, clips in plan:
        for local, seg in clips:
            with wave.open(str(voice_dir / voice[seg]["file"])) as w:
                assert (w.getframerate(), w.getnchannels(), w.getsampwidth()) == (rate, 1, 2), seg
                pcm = w.readframes(w.getnframes())
            at = 2 * int((offset + local) * rate)
            track[at:at + len(pcm)] = pcm
            cues.append((offset + local, offset + local + voice[seg]["duration"], voice[seg]["text"]))
        offset += duration
    with wave.open(str(wav_path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(rate)
        w.writeframes(bytes(track[: 2 * int(total * rate)]))

    stamp = lambda x: f"{int(x // 3600):02d}:{int(x % 3600 // 60):02d}:{int(x % 60):02d},{int(round(x % 1 * 1000)) % 1000:03d}"
    srt_path.write_text("\n".join(f"{n}\n{stamp(a)} --> {stamp(b)}\n{text}\n" for n, (a, b, text) in enumerate(cues, 1)))
    return cues


def mux_narration(video_name, wav_name, dst_name, cwd, total):
    """Two-pass EBU R128 loudness normalisation (-16 LUFS, -1.5 dBTP), then AAC audio muxed with the copied video."""
    target = "I=-16:TP=-1.5:LRA=11"
    probe = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-i", wav_name, "-af", f"loudnorm={target}:print_format=json",
                            "-f", "null", "-"], cwd=cwd, capture_output=True, text=True, check=True)
    m = json.loads(probe.stderr[probe.stderr.rindex("{"):probe.stderr.rindex("}") + 1])
    measured = (f"measured_I={m['input_i']}:measured_TP={m['input_tp']}:measured_LRA={m['input_lra']}:"
                f"measured_thresh={m['input_thresh']}:offset={m['target_offset']}:linear=true")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", video_name, "-i", wav_name,
                    "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-af", f"loudnorm={target}:{measured},aresample=48000",
                    "-c:a", "aac", "-b:a", "160k", "-t", f"{total:.3f}", "-movflags", "+faststart", dst_name],
                   cwd=cwd, check=True)


def check(transcript):
    """Refuse to render a recording whose output no longer supports its captions."""
    for sc in transcript["scenes"]:
        for st in sc.get("steps", []):
            output = "\n".join(text for _, text in st["lines"])
            for rx in st.get("expect", []):
                if not re.search(rx, output, re.MULTILINE):
                    sys.exit(f"Transcript does not support caption for '{st['cmd']}': missing /{rx}/")


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    src = Path(args[0]) if args else ROOT / "demo/out/transcript.json"
    narrated = "--narrated" in sys.argv
    dst = Path(args[1]) if len(args) > 1 else ROOT / ("demo/out/syncguard-demo-narrated.mp4" if narrated else "demo/out/syncguard-demo.mp4")
    transcript = json.loads(src.read_text())
    check(transcript)
    voice_dir = src.parent / "voice"
    voice = json.loads((voice_dir / "voice.json").read_text()) if narrated else None
    if narrated:
        expected = {seg["id"]: seg["text"] for seg in segments(transcript)}
        if {k: v["text"] for k, v in voice.items()} != expected:
            sys.exit("Voice clips do not match this transcript's narration; run demo/narrate.py again.")

    all_out = "\n".join(t for sc in transcript["scenes"] for st in sc.get("steps", []) for _, t in st["lines"])
    tests = re.search(r"Passed: +(\d+)", all_out)
    total = re.search(r"Total tests: +(\d+)", all_out)
    recorded = datetime.fromisoformat(transcript["recordedAtUtc"])
    meta = {"recorded": f"{recorded:%d %b %Y} (UTC)", "tests": f"{tests[1]}/{total[1]}" if tests and total else "all"}

    plan = scenes_with_durations(transcript, voice)
    length = sum(d for _, d, _, _ in plan)
    print(f"video length: {length:.1f} s ({int(length // 60)}:{length % 60:04.1f})")

    if "--stills" in sys.argv:
        out_dir = dst.parent / "stills"
        out_dir.mkdir(parents=True, exist_ok=True)
        for i, (sc, dur, states, _) in enumerate(plan):
            img = CARDS[sc["id"]](meta) if states is None else term_frame(sc, *states[-2][1:])
            img.save(out_dir / f"{i:02d}-{sc.get('id') or sc['title'][:20].replace(' ', '_')}.png")
        print(f"stills written to {out_dir}")
        return

    dst.parent.mkdir(parents=True, exist_ok=True)
    video_name = f"{dst.stem}.video.mp4" if narrated else dst.name
    ffmpeg = subprocess.Popen(
        ["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}",
         "-r", str(FPS), "-i", "-", "-c:v", "libx264", "-preset", "slow", "-crf", "20", "-tune", "stillimage",
         "-pix_fmt", "yuv420p", "-movflags", "+faststart", video_name],
        stdin=subprocess.PIPE, cwd=dst.parent)
    black = Image.new("RGB", (W, H), BG)
    frames = 0
    for sc, dur, states, _ in plan:
        n = round(dur * FPS)
        cache_key, cache = None, None
        state_i = 0
        for f in range(n):
            t = f / FPS
            if states is None:
                key = "card"
            else:
                while state_i + 1 < len(states) and states[state_i + 1][0] <= t:
                    state_i += 1
                key = state_i
            fade = min(1.0, t / FADE, (dur - t) / FADE)
            fade_key = round(fade, 2)
            if (key, fade_key) != cache_key:
                if key != (cache_key or (None,))[0]:
                    base = CARDS[sc["id"]](meta) if states is None else term_frame(sc, *states[key][1:])
                frame = base if fade >= 1 else Image.blend(black, base, max(fade, 0))
                cache, cache_key = frame.tobytes(), (key, fade_key)
            ffmpeg.stdin.write(cache)
            frames += 1
    ffmpeg.stdin.close()
    if ffmpeg.wait() != 0:
        sys.exit("ffmpeg failed")
    if narrated:
        total = frames / FPS
        wav_name = f"{dst.stem}.voice.wav"
        cues = build_voice_track(plan, voice, voice_dir, total, dst.parent / wav_name, dst.with_suffix(".srt"))
        mux_narration(video_name, wav_name, dst.name, dst.parent, total)
        (dst.parent / video_name).unlink()
        (dst.parent / wav_name).unlink()
        print(f"narration: {len(cues)} clips, {sum(b - a for a, b, _ in cues):.1f} s of speech; subtitles: {dst.with_suffix('.srt')}")
    print(f"wrote {dst} ({frames} frames)")


if __name__ == "__main__":
    main()
