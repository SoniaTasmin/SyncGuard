#!/usr/bin/env python3
"""Generate the voiceover for a recorded demo (demo/out/transcript.json) as one WAV file per narrated segment.

    python3 demo/narrate.py            # writes demo/out/voice/<segment>.wav and demo/out/voice/voice.json
    python3 demo/render.py --narrated  # then renders demo/out/syncguard-demo-narrated.mp4

Text-to-speech is Kokoro-82M (Apache-2.0 model weights) via kokoro-onnx, run on the CPU inside a throwaway
python:3.12-slim container, so nothing is installed on the host. The model files (~340 MB) are downloaded once
into demo/out/tts-model/. The narration text lives next to each step in demo/demo.py (the `say=` fields).
"""
import json
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "demo/out"
VOICE_DIR, MODEL_DIR = OUT / "voice", OUT / "tts-model"
VOICE, SPEED = "af_heart", 1.08
MODEL_URL = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/"
MODEL_FILES = ["kokoro-v1.0.onnx", "voices-v1.0.bin"]
IMAGE = "syncguard-tts:local"
DOCKERFILE = "FROM python:3.12-slim\nRUN pip install --no-cache-dir kokoro-onnx==0.4.9 soundfile==0.13.1\nWORKDIR /work\n"

TTS_SCRIPT = """
import json, soundfile
from kokoro_onnx import Kokoro
kokoro = Kokoro("/model/kokoro-v1.0.onnx", "/model/voices-v1.0.bin")
segments = json.load(open("/voice/segments.json"))
manifest = {}
for seg in segments:
    samples, rate = kokoro.create(seg["text"], voice=seg["voice"], speed=seg["speed"], lang="en-us")
    soundfile.write(f"/voice/{seg['id']}.wav", samples, rate, subtype="PCM_16")
    manifest[seg["id"]] = {"text": seg["text"], "file": f"{seg['id']}.wav", "duration": round(len(samples) / rate, 3), "rate": rate}
    print(f"{seg['id']}  {manifest[seg['id']]['duration']:5.2f} s  {seg['text'][:70]}", flush=True)
json.dump(manifest, open("/voice/voice.json", "w"), indent=1)
"""


def segments(transcript):
    """Stable segment ids: <scene index>-<step index> (cards use step 00)."""
    for i, sc in enumerate(transcript["scenes"]):
        if sc["kind"] == "card":
            if sc.get("say"):
                yield {"id": f"{i:02d}-00", "text": sc["say"]}
        else:
            for j, st in enumerate(sc["steps"]):
                if st.get("say"):
                    yield {"id": f"{i:02d}-{j + 1:02d}", "text": st["say"]}


def main():
    transcript = json.loads((OUT / "transcript.json").read_text())
    segs = [{**s, "voice": VOICE, "speed": SPEED} for s in segments(transcript)]
    if not segs:
        sys.exit("The transcript has no narration (say=) fields. Re-record with the current demo/demo.py.")

    MODEL_DIR.mkdir(parents=True, exist_ok=True)
    for name in MODEL_FILES:
        if not (MODEL_DIR / name).exists():
            print(f"downloading {name} …", flush=True)
            urllib.request.urlretrieve(MODEL_URL + name, MODEL_DIR / f"{name}.part")
            (MODEL_DIR / f"{name}.part").rename(MODEL_DIR / name)

    VOICE_DIR.mkdir(parents=True, exist_ok=True)
    for old in VOICE_DIR.glob("*.wav"):
        old.unlink()
    (VOICE_DIR / "segments.json").write_text(json.dumps(segs, indent=1))
    subprocess.run(["docker", "build", "-q", "-t", IMAGE, "-"], input=DOCKERFILE, text=True, check=True, capture_output=True)
    subprocess.run(["docker", "run", "--rm", "--network", "none", "-v", f"{VOICE_DIR}:/voice", "-v", f"{MODEL_DIR}:/model:ro",
                    IMAGE, "python", "-c", TTS_SCRIPT], check=True)
    total = sum(v["duration"] for v in json.loads((VOICE_DIR / "voice.json").read_text()).values())
    print(f"{len(segs)} segments, {total:.1f} s of speech -> {VOICE_DIR}")


if __name__ == "__main__":
    main()
