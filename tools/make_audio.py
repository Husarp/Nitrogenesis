#!/usr/bin/env python3
"""Makes the sounds in app/audio/ from nothing (plain maths, no samples from anywhere), so they are ours and license-free.

    python3 tools/make_audio.py            # writes app/audio/click.wav and app/audio/engine_loop.wav

click.wav        a short soft blip for button presses (60 ms)
engine_loop.wav  one second of engine hum that loops without a seam; the game changes its pitch with the car's speed
"""
import math
import random
import struct
import sys
import wave
from pathlib import Path

RATE = 22050


def write(path, samples):
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", max(-32767, min(32767, int(s * 32767)))) for s in samples))


def click():
    n = int(RATE * 0.06)
    out = []
    for i in range(n):
        t = i / RATE
        env = math.exp(-t * 70) * min(1.0, i / 40)  # 40 samples of attack so it does not pop
        tone = math.sin(2 * math.pi * 1250 * t) * 0.6 + math.sin(2 * math.pi * 1900 * t) * 0.3
        out.append(tone * env * 0.55)
    return out


def engine():
    # One second at a 55 Hz base: a whole number of cycles, so the end meets the start. The "firing" pulse runs at
    # 4 x the base (also whole), and a slow 5 Hz wobble keeps it from sounding like a pure organ note.
    base, seconds = 55, 1.0
    n = int(RATE * seconds)
    rng = random.Random(7)
    harmonics = [(k, 1.0 / (k ** 1.15) * (1.6 if k in (2, 3, 4) else 1.0)) for k in range(1, 15)]
    phases = {k: rng.uniform(0, 2 * math.pi) for k, _ in harmonics}
    out = []
    for i in range(n):
        t = i / RATE
        s = sum(a * math.sin(2 * math.pi * base * k * t + phases[k]) for k, a in harmonics)
        pulse = 0.72 + 0.28 * math.sin(2 * math.pi * base * 4 * t)
        wobble = 1.0 + 0.06 * math.sin(2 * math.pi * 5 * t)
        out.append(s * pulse * wobble)
    peak = max(abs(v) for v in out)
    return [v / peak * 0.6 for v in out]


def main():
    target = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "app" / "audio"
    target.mkdir(parents=True, exist_ok=True)
    write(target / "click.wav", click())
    write(target / "engine_loop.wav", engine())
    print("wrote", target / "click.wav", target / "engine_loop.wav")


if __name__ == "__main__":
    main()
