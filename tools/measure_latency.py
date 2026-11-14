#!/usr/bin/env python3
"""Measure delay from a real, synchronous two-channel PCM16/48k recording.

Channel 0: direct reference; channel 1: AirMic route. Both channels must share
one recording clock. The result includes fixture/acoustic/recording delay.
Never interpret UDP ACK RTT or an artificial fixture as hardware E2E latency.
Requires: pip install -r tools/requirements-latency.txt
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import statistics
import wave

import numpy as np


def correlate(reference: np.ndarray, measured: np.ndarray, minimum: int, maximum: int) -> tuple[int, float, float] | None:
    """Return (positive lag in samples, normalized score, peak/runner-up ratio)."""
    x = reference.astype(np.float64)
    y = measured.astype(np.float64)
    if len(x) < 2 or len(y) < len(x) + maximum:
        return None
    x -= np.mean(x)
    y -= np.mean(y)
    energy = float(x @ x)
    if energy / len(x) < 1e-8:  # below -80 dBFS is not a usable reference
        return None
    fft_size = 1 << (len(x) + len(y) - 1).bit_length()
    cross = np.fft.irfft(np.conj(np.fft.rfft(x, fft_size)) * np.fft.rfft(y, fft_size), fft_size)
    cumulative = np.concatenate(([0.0], np.cumsum(y * y)))
    indices = np.arange(minimum, maximum + 1)
    window_energy = cumulative[indices + len(x)] - cumulative[indices]
    scores = np.abs(cross[indices]) / np.sqrt(np.maximum(energy * window_energy, 1e-30))
    best = int(np.argmax(scores))
    lag = int(indices[best])
    score = float(min(1.0, scores[best]))
    # Exclude +/-1ms of the winning peak; periodic tones are intentionally rejected.
    competitors = scores[np.abs(indices - lag) > 48]
    runner_up = float(np.max(competitors)) if competitors.size else 0.0
    ratio = score / max(runner_up, 1e-12)
    return lag, score, ratio


def analyze(path: Path, minimum_ms: float = 0, maximum_ms: float = 500, window_seconds: float = 2) -> dict:
    if not all(math.isfinite(v) for v in (minimum_ms, maximum_ms, window_seconds)):
        raise ValueError("Arguments must be finite")
    if not 0 <= minimum_ms < maximum_ms <= 1000 or not 0.1 <= window_seconds <= 10:
        raise ValueError("Require 0 <= min < max <= 1000 ms and 0.1..10 second windows")
    matches: list[dict] = []
    skipped = 0
    with wave.open(str(path), "rb") as source:
        if (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != (2, 2, 48000, "NONE"):
            raise ValueError("Recording must be uncompressed PCM16 stereo at 48000 Hz")
        rate = source.getframerate()
        minimum = round(minimum_ms * rate / 1000)
        maximum = round(maximum_ms * rate / 1000)
        window = round(window_seconds * rate)
        for start in range(0, source.getnframes() - window - maximum + 1, window):
            source.setpos(start)
            raw = source.readframes(window + maximum)
            samples = np.frombuffer(raw, dtype="<i2").reshape(-1, 2).astype(np.float64) / 32768.0
            match = correlate(samples[:window, 0], samples[:, 1], minimum, maximum)
            if not match or match[1] < 0.2 or match[2] < 1.02:
                skipped += 1
                continue
            lag, confidence, ratio = match
            matches.append({"at_seconds": start / rate, "delay_ms": lag * 1000 / rate,
                            "normalized_correlation": confidence, "peak_ratio": ratio})
    if not matches:
        raise ValueError("No unambiguous correlation. Check reference/route channels, signal level, lag range; do not use a pure tone.")
    delays = sorted(float(m["delay_ms"]) for m in matches)
    return {
        "measurement": "synchronous two-channel recorded route delay; includes fixture delay",
        "input": str(path),
        "sample_rate": 48000,
        "matched_windows": len(matches),
        "skipped_windows": skipped,
        "median_ms": statistics.median(delays),
        "p95_ms": delays[math.ceil(len(delays) * 0.95) - 1],
        "min_ms": delays[0],
        "max_ms": delays[-1],
        "range_ms": delays[-1] - delays[0],
        "windows": matches,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("recording", type=Path)
    parser.add_argument("--min-ms", type=float, default=0)
    parser.add_argument("--max-ms", type=float, default=500)
    parser.add_argument("--window-seconds", type=float, default=2)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    try:
        result = analyze(args.recording, args.min_ms, args.max_ms, args.window_seconds)
    except (ValueError, OSError, wave.Error) as error:
        parser.exit(1, f"Measurement failed: {error}\n")
    output = json.dumps(result, indent=2)
    print(output)
    if args.json:
        args.json.write_text(output + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
