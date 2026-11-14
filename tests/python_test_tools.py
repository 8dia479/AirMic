from pathlib import Path
import math
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from tools.udp_sender import AUDIO, ACK, pack_audio, unpack_ack

try:
    import numpy as np
    from tools.measure_latency import analyze, correlate
except ImportError:
    np = None


class ProtocolTools(unittest.TestCase):
    def test_independent_wire_vector(self):
        samples = [-32768, 32767, -1] + [0] * 477
        data = pack_audio(0x01020304, 0x05060708090A0B0C, samples, 2)
        self.assertEqual(len(data), 976)
        self.assertEqual(data[:22].hex(), "0102030405060708090a0b0c01e0000280007fffffff")
        with self.assertRaises(ValueError):
            pack_audio(1, 1, samples, 1)
        with self.assertRaises(ValueError):
            pack_audio(0, 1, samples[:2])

    def test_ack(self):
        self.assertEqual(unpack_ack(ACK.pack(b"AMAK", 7, 123456, 42, 1, 0)), (7, 123456, 42))
        self.assertIsNone(unpack_ack(b"AMAK"))
        self.assertIsNone(unpack_ack(ACK.pack(b"AMAK", 7, 123456, 42, 2, 0)))

    def test_real_udp_sender_ack_exchange(self):
        # Independent socket peer verifies Python wire framing and ACK correlation.
        # This test peer is not used in the Android/Windows production engines.
        seen = []
        stop = threading.Event()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
            server.bind(("127.0.0.1", 0))
            server.settimeout(0.1)
            port = server.getsockname()[1]

            def receive():
                while not stop.is_set():
                    try:
                        data, peer = server.recvfrom(2048)
                    except socket.timeout:
                        continue
                    fields = AUDIO.unpack(data)
                    seen.append(fields[:4])
                    server.sendto(ACK.pack(b"AMAK", fields[0], fields[1], len(seen), 1, 0), peer)

            worker = threading.Thread(target=receive)
            worker.start()
            try:
                result = subprocess.run([sys.executable, str(ROOT / "tools/udp_sender.py"), "--host", "127.0.0.1", "--port", str(port), "--seconds", "0.1"], capture_output=True, text=True, timeout=10)
            finally:
                stop.set()
                worker.join()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(len(seen), 10)
        for i, (seq, ts, count, flags) in enumerate(seen):
            self.assertEqual((seq, count, flags), (i, 480, 1 if i == 0 else 0))
            self.assertEqual(ts, seen[0][1] + i * 480)


@unittest.skipIf(np is None, "Install tools/requirements-latency.txt for WAV analysis tests")
class LatencyAnalysis(unittest.TestCase):
    def test_known_artificial_delay(self):
        rng = np.random.default_rng(414952)
        reference = rng.normal(0, 0.1, 48000)
        delay = 1733
        measured = np.zeros(48000 + 24000)
        measured[delay:delay + len(reference)] = reference * 0.4
        match = correlate(reference, measured, 0, 24000)
        self.assertEqual(match[0], delay)
        self.assertGreater(match[1], 0.99)

    def test_stereo_file_fixture(self):
        rng = np.random.default_rng(123)
        delay = 2160  # exactly 45ms, SYNTHETIC test, not an AirMic measurement
        reference = np.clip(rng.normal(0, 5000, 48000 * 5), -25000, 25000).astype("<i2")
        route = np.zeros_like(reference)
        route[delay:] = (reference[:-delay] * 0.5).astype("<i2")
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "artificial.wav"
            with wave.open(str(path), "wb") as output:
                output.setparams((2, 2, 48000, 0, "NONE", "not compressed"))
                output.writeframes(np.column_stack((reference, route)).astype("<i2").tobytes())
            result = analyze(path)
        self.assertEqual(result["matched_windows"], 2)
        self.assertAlmostEqual(result["median_ms"], 45, places=3)

    def test_silence_and_tone_not_measurements(self):
        self.assertIsNone(correlate(np.zeros(1000), np.zeros(2000), 0, 500))
        x = np.sin(np.arange(48000) * 2 * math.pi * 1000 / 48000) * 0.1
        y = np.sin((np.arange(72000) - 1200) * 2 * math.pi * 1000 / 48000) * 0.1
        match = correlate(x, y, 0, 24000)
        self.assertLess(match[2], 1.02)

    def test_reject_wrong_format(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "mono.wav"
            with wave.open(str(path), "wb") as output:
                output.setparams((1, 2, 44100, 0, "NONE", "not compressed"))
                output.writeframes(b"\0" * 1000)
            with self.assertRaises(ValueError):
                analyze(path)


if __name__ == "__main__":
    unittest.main()
