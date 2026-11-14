#!/usr/bin/env python3
"""Independent AirMic v1 synthetic/WAV sender for receiver diagnostics.

This sends generated/prerecorded PCM, NOT a phone microphone. Python scheduling
is not a low-latency capture engine. No third-party modules are required.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import math
from pathlib import Path
import select
import socket
import statistics
import struct
import time
import wave

RATE = 48_000
SAMPLES = 480
AUDIO = struct.Struct("!IQHH480h")
ACK = struct.Struct("!4sIQIHH")


def pack_audio(sequence: int, timestamp: int, samples: list[int], flags: int = 0) -> bytes:
    if len(samples) != SAMPLES or flags & ~3 or (flags & 1 and sequence != 0):
        raise ValueError("480 samples and valid v1 flags are required")
    return AUDIO.pack(sequence & 0xFFFFFFFF, timestamp, SAMPLES, flags, *samples)


def unpack_ack(data: bytes) -> tuple[int, int, int] | None:
    if len(data) != ACK.size:
        return None
    magic, sequence, timestamp, accepted, version, reserved = ACK.unpack(data)
    if magic != b"AMAK" or version != 1 or reserved != 0:
        return None
    return sequence, timestamp, accepted


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="Receiver numeric IPv4")
    parser.add_argument("--port", type=int, default=49000)
    parser.add_argument("--seconds", type=float, default=10)
    parser.add_argument("--tone-hz", type=float, default=997)
    parser.add_argument("--wav", type=Path, help="PCM16 mono 48 kHz WAV (stops at EOF)")
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    ipaddress.IPv4Address(args.host)
    if not 1 <= args.port <= 65535 or not math.isfinite(args.seconds) or not 0 < args.seconds <= 86400:
        parser.error("port must be 1..65535, seconds must be 0..86400")
    if not math.isfinite(args.tone_hz) or not 20 <= args.tone_hz < 20000:
        parser.error("tone-hz must be 20..19999")
    source = wave.open(str(args.wav), "rb") if args.wav else None
    if source and (source.getnchannels(), source.getsampwidth(), source.getframerate(), source.getcomptype()) != (1, 2, RATE, "NONE"):
        source.close()
        parser.error("WAV must be uncompressed PCM16, mono, 48000 Hz")
    sent = errors = accepted = 0
    rtts: list[float] = []
    pending: dict[int, tuple[int, int]] = {}
    start = time.monotonic_ns() + 100_000_000
    base_ticks = start * RATE // 1_000_000_000
    print("DIAGNOSTIC SOURCE: generated/prerecorded PCM, not Android capture; Ctrl+C stops.")
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.connect((args.host, args.port))
            sock.setblocking(False)
            for number in range(math.ceil(args.seconds * 100)):
                remaining = (start + number * 10_000_000 - time.monotonic_ns()) / 1e9
                if remaining > 0:
                    time.sleep(remaining)
                if source:
                    block = source.readframes(SAMPLES)
                    if not block:
                        break
                    block = block.ljust(SAMPLES * 2, b"\0")
                    samples = list(struct.unpack("<480h", block))
                else:
                    samples = [round(5000 * math.sin(2 * math.pi * args.tone_hz * (number * SAMPLES + i) / RATE)) for i in range(SAMPLES)]
                sequence = number & 0xFFFFFFFF
                timestamp = base_ticks + number * SAMPLES
                payload = pack_audio(sequence, timestamp, samples, 1 if number == 0 else 0)
                sent_at = time.monotonic_ns()
                try:
                    if sock.send(payload) == len(payload):
                        sent += 1
                        pending[sequence] = (timestamp, sent_at)
                except OSError:
                    errors += 1
                if len(pending) > 1024:
                    del pending[next(iter(pending))]
                # Wait only within this 10 ms send period. No audio thread is involved.
                budget = min(0.007, max(0.0, (start + (number + 1) * 10_000_000 - time.monotonic_ns()) / 1e9))
                try:
                    if select.select([sock], [], [], budget)[0]:
                        reply = unpack_ack(sock.recv(2048))
                        if reply:
                            seq, stamp, accepted = reply
                            record = pending.get(seq)
                            if record and record[0] == stamp:
                                rtts.append((time.monotonic_ns() - record[1]) / 1e6)
                except OSError:
                    # Windows/Linux report asynchronous ICMP differently; keep retrying.
                    pass
    except KeyboardInterrupt:
        print("Stopped.")
    finally:
        if source:
            source.close()
    sorted_rtts = sorted(rtts)
    report = {
        "source": "diagnostic generated/prerecorded PCM; not phone capture",
        "sent_packets": sent,
        "send_errors": errors,
        "receiver_last_accepted_count": accepted,
        "ack_samples": len(rtts),
        "udp_ack_rtt_p50_ms": statistics.median(rtts) if rtts else None,
        "udp_ack_rtt_p95_ms": sorted_rtts[math.ceil(len(rtts) * 0.95) - 1] if rtts else None,
        "hardware_end_to_end_latency_ms": None,
    }
    print(json.dumps(report, indent=2))
    if args.json:
        args.json.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    return 0 if rtts else 1


if __name__ == "__main__":
    raise SystemExit(main())
