#!/usr/bin/env python3
import argparse
import asyncio
import json
import math
import os
import re
import signal
import subprocess
import threading
import time
import unicodedata
from collections import deque
from pathlib import Path
from typing import Optional

import numpy as np
import websockets
from vosk import KaldiRecognizer, Model, SetLogLevel

RATE = 16000
BLOCK = 512  # 32 ms
MAX_LAG = 12


def log(msg: str) -> None:
    print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)


def normalize(text: str) -> str:
    text = unicodedata.normalize("NFD", (text or "").lower())
    text = "".join(ch for ch in text if unicodedata.category(ch) != "Mn")
    text = re.sub(r"[^a-z0-9ñ ]+", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def levenshtein(a: str, b: str) -> int:
    if len(a) < len(b):
        a, b = b, a
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(cur[-1] + 1, prev[j] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def wake_matches(text: str, target: str, sensitivity: int) -> bool:
    if text == target or target in text:
        return True
    dist = levenshtein(text, target)
    if sensitivity >= 80:
        return dist <= max(2, len(target) // 5)
    if sensitivity >= 45:
        return dist <= max(1, len(target) // 8)
    return False


def gcc_phat(x: np.ndarray, ref: np.ndarray, max_lag: int = MAX_LAG) -> tuple[float, float]:
    n = 1 << int(np.ceil(np.log2(len(x) + len(ref))))
    X = np.fft.rfft(x, n)
    R = np.fft.rfft(ref, n)
    G = X * np.conj(R)
    G /= np.abs(G) + 1e-9
    f = np.fft.rfftfreq(n, 1 / RATE)
    G[(f < 300) | (f > 4000)] = 0
    cc = np.fft.irfft(G, n)
    cc = np.concatenate([cc[-max_lag:], cc[: max_lag + 1]])
    i = int(np.argmax(cc))
    frac = 0.0
    if 0 < i < len(cc) - 1:
        a, b, c = cc[i - 1], cc[i], cc[i + 1]
        den = a - 2 * b + c
        if den:
            frac = 0.5 * (a - c) / den
    sharp = float(cc[i] / (np.mean(np.abs(cc)) + 1e-9))
    return i - max_lag + frac, sharp


class RealtimeBeamformer:
    """Kinect delay-and-sum beamformer inspired by plateforme/bulle (MIT).

    Real-time blocks use integer delays. Direction estimates are refreshed from a
    rolling speech window using GCC-PHAT and smoothed so Vosk/uplink stay stable.
    """

    def __init__(self, channels: int, estimate_ms: int = 512, min_rms: float = 120.0):
        self.channels = channels
        self.tau = np.zeros(channels, dtype=np.float64)
        self.hist = np.zeros((BLOCK, channels), dtype=np.float32)
        self.rolling = deque(maxlen=max(2, math.ceil((estimate_ms / 1000) * RATE / BLOCK)))
        self.last_estimate = 0.0
        self.min_rms = min_rms
        self.estimates = 0

    def process(self, data: np.ndarray) -> np.ndarray:
        if data.ndim != 2 or data.shape[1] != self.channels:
            raise ValueError(f"expected Nx{self.channels}, got {data.shape}")
        if self.channels < 2:
            return data[:, 0].astype(np.int16, copy=False)

        f32 = data.astype(np.float32, copy=False)
        self.rolling.append(f32.copy())
        now = time.monotonic()
        if now - self.last_estimate >= 0.35 and len(self.rolling) == self.rolling.maxlen:
            self.last_estimate = now
            window = np.vstack(self.rolling)
            mono_rms = float(np.sqrt(np.mean(np.square(window.mean(axis=1), dtype=np.float64))))
            if mono_rms >= self.min_rms:
                self._estimate(window)

        buf = np.vstack([self.hist, f32])
        self.hist = buf[-BLOCK:].copy()
        delays = np.round(self.tau).astype(int)
        global_delay = int(delays.max())
        out = np.zeros(len(data), np.float32)
        for k in range(self.channels):
            start = BLOCK + int(delays[k]) - global_delay
            out += buf[start : start + len(data), k]
        return np.clip(out / self.channels, -32768, 32767).astype(np.int16)

    def _estimate(self, window: np.ndarray) -> None:
        new = np.zeros(self.channels, dtype=np.float64)
        sharpness = []
        ref = window[:, 0]
        for k in range(1, self.channels):
            delay, sharp = gcc_phat(window[:, k], ref)
            new[k] = delay
            sharpness.append(sharp)
        if sharpness and min(sharpness) > 3.5:
            self.tau = new if self.estimates == 0 else 0.75 * self.tau + 0.25 * new
            self.estimates += 1
            if self.estimates == 1 or self.estimates % 10 == 0:
                log("beamforming delays=" + ",".join(f"{v:.2f}" for v in self.tau))


class PulseCapture:
    def __init__(self, source: str, channels: int):
        self.source = source
        self.channels = channels
        self.proc: Optional[subprocess.Popen] = None

    def start(self) -> None:
        cmd = [
            "pacat", "--record", "--raw", "--format=s16le",
            f"--rate={RATE}", f"--channels={self.channels}",
        ]
        if self.source:
            cmd.append(f"--device={self.source}")
        log("capture: " + " ".join(cmd))
        self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)

    def read_block(self) -> np.ndarray:
        assert self.proc and self.proc.stdout
        need = BLOCK * self.channels * 2
        data = bytearray()
        while len(data) < need:
            chunk = self.proc.stdout.read(need - len(data))
            if not chunk:
                err = self.proc.stderr.read().decode("utf-8", "replace") if self.proc.stderr else ""
                raise RuntimeError("Pulse capture ended: " + err[-1000:])
            data.extend(chunk)
        return np.frombuffer(data, dtype="<i2").reshape(-1, self.channels)

    def stop(self) -> None:
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=1)
            except subprocess.TimeoutExpired:
                self.proc.kill()
            self.proc = None


class PulsePlayback:
    def __init__(self, sink: str):
        self.sink = sink
        self.proc: Optional[subprocess.Popen] = None
        self.lock = threading.Lock()

    def start(self) -> None:
        cmd = ["pacat", "--playback", "--raw", "--format=s16le", "--rate=16000", "--channels=1"]
        if self.sink:
            cmd.append(f"--device={self.sink}")
        log("playback: " + " ".join(cmd))
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)

    def write(self, pcm: bytes) -> None:
        with self.lock:
            if not self.proc or not self.proc.stdin:
                return
            try:
                self.proc.stdin.write(pcm)
                self.proc.stdin.flush()
            except (BrokenPipeError, OSError):
                pass

    def stop(self) -> None:
        with self.lock:
            if self.proc:
                try:
                    if self.proc.stdin:
                        self.proc.stdin.close()
                except OSError:
                    pass
                self.proc.terminate()
                try:
                    self.proc.wait(timeout=1)
                except subprocess.TimeoutExpired:
                    self.proc.kill()
                self.proc = None


def list_pulse(kind: str) -> list[tuple[str, str]]:
    noun = "sources" if kind == "source" else "sinks"
    try:
        text = subprocess.check_output(["pactl", "list", "short", noun], text=True, stderr=subprocess.STDOUT)
    except Exception as exc:
        log(f"pactl {noun} failed: {exc}")
        return []
    out = []
    for line in text.splitlines():
        parts = line.split("\t")
        if len(parts) >= 2:
            out.append((parts[1], line))
    return out


def resolve_pulse_device(configured: str, kind: str, prefer_kinect: bool) -> str:
    if configured and configured.lower() != "auto":
        return configured
    items = list_pulse(kind)
    if prefer_kinect:
        for name, line in items:
            if "kinect" in line.lower() or "xbox nui" in line.lower():
                log(f"auto-selected Kinect {kind}: {name}")
                return name
    log(f"using default PulseAudio {kind}; available={len(items)}")
    for _, line in items:
        log("  " + line)
    return ""


class Satellite:
    def __init__(self, cfg: dict):
        self.cfg = cfg
        self.state = "disconnected"
        self.session_id = ""
        self.revision = -1
        self.ws = None
        self.loop: Optional[asyncio.AbstractEventLoop] = None
        self.tx_queue: asyncio.Queue = asyncio.Queue(maxsize=200)
        self.stop_event = threading.Event()
        self.capture_thread: Optional[threading.Thread] = None
        self.capture: Optional[PulseCapture] = None
        self.playback: Optional[PulsePlayback] = None
        self.last_downlink = 0.0
        self.wake_last = 0.0
        self.first_word = ""
        self.first_word_at = 0.0
        self.audio_config_session = ""
        self.beam: Optional[RealtimeBeamformer] = None
        self.model: Optional[Model] = None
        self.wake_rec: Optional[KaldiRecognizer] = None
        self.end_rec: Optional[KaldiRecognizer] = None

    def load_model(self) -> None:
        SetLogLevel(-1)
        model_path = self.cfg["vosk_model"]
        log(f"loading Vosk model: {model_path}")
        self.model = Model(model_path)
        self._reset_wake_recognizer()
        self._reset_end_recognizer()

    def _grammar(self, phrases: list[str]) -> str:
        vals = []
        for p in phrases:
            p = normalize(p)
            if p and p not in vals:
                vals.append(p)
        vals.append("[unk]")
        return json.dumps(vals, ensure_ascii=False)

    def _reset_wake_recognizer(self) -> None:
        target = normalize(self.cfg["wake_word"])
        variants = [target]
        parts = target.split()
        if len(parts) == 2:
            variants.extend(parts)
        self.wake_rec = KaldiRecognizer(self.model, RATE, self._grammar(variants))

    def _reset_end_recognizer(self) -> None:
        phrases = [p.strip() for p in self.cfg.get("end_phrases", []) if p.strip()]
        self.end_rec = KaldiRecognizer(self.model, RATE, self._grammar(phrases)) if phrases else None

    async def run(self) -> None:
        self.loop = asyncio.get_running_loop()
        self.load_model()
        source = resolve_pulse_device(self.cfg.get("pulse_input", "auto"), "source", True)
        sink = resolve_pulse_device(self.cfg.get("pulse_output", "auto"), "sink", False)
        channels = int(self.cfg.get("channels", 4))
        self.capture = PulseCapture(source, channels)
        self.playback = PulsePlayback(sink)
        self.playback.start()
        self.capture.start()
        self.beam = RealtimeBeamformer(channels, min_rms=float(self.cfg.get("beam_min_rms", 120)))
        self.capture_thread = threading.Thread(target=self._capture_loop, name="KinectCapture", daemon=True)
        self.capture_thread.start()

        while not self.stop_event.is_set():
            try:
                await self._connect_once()
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                log(f"transport error: {type(exc).__name__}: {exc}")
            self.state = "disconnected"
            self.session_id = ""
            self.audio_config_session = ""
            await asyncio.sleep(float(self.cfg.get("reconnect_seconds", 2.0)))

    async def _connect_once(self) -> None:
        url = self.cfg["server_url"]
        log(f"connecting {url}")
        async with websockets.connect(url, max_size=None, ping_interval=20, ping_timeout=20) as ws:
            self.ws = ws
            await ws.send(json.dumps({"type": "hello", "protocol": 2, "name": "Raspberry Pi Kinect satellite"}))
            await ws.send(json.dumps({"type": "sync"}))
            log("connected to Codex Audio Remote")
            sender = asyncio.create_task(self._sender(ws))
            try:
                async for msg in ws:
                    if isinstance(msg, bytes):
                        self.last_downlink = time.monotonic()
                        if self.playback:
                            self.playback.write(msg)
                    else:
                        await self._handle_control(msg)
            finally:
                sender.cancel()
                try:
                    await sender
                except asyncio.CancelledError:
                    pass
                self.ws = None

    async def _sender(self, ws) -> None:
        while True:
            kind, payload = await self.tx_queue.get()
            if kind == "json":
                await ws.send(json.dumps(payload, ensure_ascii=False))
            else:
                await ws.send(payload)

    async def _handle_control(self, raw: str) -> None:
        try:
            msg = json.loads(raw)
        except json.JSONDecodeError:
            return
        typ = msg.get("type")
        if typ == "state":
            rev = int(msg.get("revision", -1))
            if self.revision >= 0 and rev >= 0 and rev < self.revision:
                return
            previous = self.state
            old_session = self.session_id
            self.revision = rev
            self.state = str(msg.get("state", "idle")).lower()
            self.session_id = str(msg.get("sessionId", ""))
            log(f"state {previous} -> {self.state} rev={rev} session={self.session_id} reason={msg.get('reason','')}")
            if self.state != "listening":
                self.audio_config_session = ""
            if self.state == "idle" and previous != "idle":
                self._reset_wake_recognizer()
            if self.state == "listening" and (previous != "listening" or old_session != self.session_id):
                self._reset_end_recognizer()
                await self._send_audio_config()
        elif typ == "audio_error":
            log("server audio_error: " + str(msg.get("reason", "unknown")))

    async def _send_audio_config(self) -> None:
        if not self.session_id or self.audio_config_session == self.session_id:
            return
        self.audio_config_session = self.session_id
        await self._enqueue_json({
            "type": "audio_config",
            "sessionId": self.session_id,
            "sampleRate": RATE,
            "channels": 1,
            "chunkMs": 32,
            "quality": 80,
            "latency": 70,
            "capture": "kinect_beamformer",
        })

    def _capture_loop(self) -> None:
        assert self.capture and self.beam and self.loop
        try:
            while not self.stop_event.is_set():
                multi = self.capture.read_block()
                mono = self.beam.process(multi)
                gain = float(self.cfg.get("mic_gain", 1.0))
                if gain != 1.0:
                    mono = np.clip(mono.astype(np.float32) * gain, -32768, 32767).astype(np.int16)
                pcm = mono.astype("<i2", copy=False).tobytes()
                state = self.state
                if state == "idle":
                    self._process_wake(pcm)
                elif state == "listening":
                    half_duplex = bool(self.cfg.get("half_duplex", True))
                    hang = float(self.cfg.get("speaker_hangover_ms", 220)) / 1000.0
                    speaker_active = time.monotonic() - self.last_downlink < hang
                    if not (half_duplex and speaker_active):
                        self._process_end_phrase(pcm)
                        self._enqueue_binary_threadsafe(pcm)
        except Exception as exc:
            log(f"capture loop stopped: {type(exc).__name__}: {exc}")
            self.stop_event.set()

    def _process_wake(self, pcm: bytes) -> None:
        if not self.wake_rec:
            return
        complete = self.wake_rec.AcceptWaveform(pcm)
        payload = self.wake_rec.Result() if complete else self.wake_rec.PartialResult()
        try:
            obj = json.loads(payload)
            text = normalize(obj.get("text") or obj.get("partial") or "")
        except Exception:
            return
        if not text:
            return
        target = normalize(self.cfg["wake_word"])
        sensitivity = int(self.cfg.get("wake_sensitivity", 60))
        match = wake_matches(text, target, sensitivity)
        now = time.monotonic()
        parts = target.split()
        if not match and len(parts) == 2:
            if text == parts[0]:
                self.first_word, self.first_word_at = parts[0], now
            elif text == parts[1] and self.first_word == parts[0] and now - self.first_word_at < 2.2:
                match = True
        if match and now - self.wake_last > 2.5:
            self.wake_last = now
            log(f"wake detected: {text!r}")
            self._enqueue_json_threadsafe({"type": "event", "event": "wake", "source": "kinect_vosk"})

    def _process_end_phrase(self, pcm: bytes) -> None:
        if not self.end_rec:
            return
        if not self.end_rec.AcceptWaveform(pcm):
            return
        try:
            text = normalize(json.loads(self.end_rec.Result()).get("text", ""))
        except Exception:
            return
        for phrase in self.cfg.get("end_phrases", []):
            p = normalize(phrase)
            if p and (text == p or text.endswith(" " + p)):
                log(f"end phrase detected: {text!r}")
                self._enqueue_json_threadsafe({
                    "type": "event", "event": "end", "reason": "voice_end_phrase", "sessionId": self.session_id
                })
                self._reset_end_recognizer()
                return

    async def _enqueue_json(self, payload: dict) -> None:
        await self.tx_queue.put(("json", payload))

    def _enqueue_json_threadsafe(self, payload: dict) -> None:
        if not self.loop or self.stop_event.is_set():
            return
        self.loop.call_soon_threadsafe(self._put_nowait, "json", payload)

    def _enqueue_binary_threadsafe(self, payload: bytes) -> None:
        if not self.loop or self.stop_event.is_set():
            return
        self.loop.call_soon_threadsafe(self._put_nowait, "binary", payload)

    def _put_nowait(self, kind: str, payload) -> None:
        if self.tx_queue.full():
            try:
                self.tx_queue.get_nowait()
            except asyncio.QueueEmpty:
                pass
        try:
            self.tx_queue.put_nowait((kind, payload))
        except asyncio.QueueFull:
            pass

    def stop(self) -> None:
        self.stop_event.set()
        if self.capture:
            self.capture.stop()
        if self.playback:
            self.playback.stop()


def load_config(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as fh:
        raw = json.load(fh)
    server = raw.get("server_url") or f"ws://{raw.get('server_ip', '192.168.1.100')}:{int(raw.get('server_port', 8765))}/ws/"
    model = raw.get("vosk_model", "/data/vosk-model-small-es-0.42")
    end_phrases = raw.get("end_phrases", ["gracias sol", "chau sol", "adios sol", "listo sol"])
    if isinstance(end_phrases, str):
        end_phrases = [p.strip() for p in end_phrases.split(",") if p.strip()]
    return {
        **raw,
        "server_url": server,
        "vosk_model": model,
        "wake_word": raw.get("wake_word", "hola sol"),
        "wake_sensitivity": int(raw.get("wake_sensitivity", 60)),
        "end_phrases": end_phrases,
        "channels": int(raw.get("channels", 4)),
        "pulse_input": raw.get("pulse_input", "auto"),
        "pulse_output": raw.get("pulse_output", "auto"),
        "half_duplex": bool(raw.get("half_duplex", True)),
    }


async def async_main(args) -> int:
    cfg = load_config(args.config)
    if not Path(cfg["vosk_model"]).exists():
        log(f"Vosk model not found: {cfg['vosk_model']}")
        return 2
    sat = Satellite(cfg)
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            loop.add_signal_handler(sig, sat.stop)
        except NotImplementedError:
            pass
    try:
        await sat.run()
    finally:
        sat.stop()
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Codex Audio Remote Raspberry Pi Kinect satellite")
    ap.add_argument("--config", default=os.environ.get("CODEX_KINECT_CONFIG", "/data/options.json"))
    args = ap.parse_args()
    return asyncio.run(async_main(args))


if __name__ == "__main__":
    raise SystemExit(main())
