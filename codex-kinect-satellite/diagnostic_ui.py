import asyncio
import json
import math
import os
import subprocess
import time
import uuid
import wave
from pathlib import Path

import numpy as np
from aiohttp import ClientSession, ClientTimeout, web

RATE = 16000
WEB_PORT = 8099
RUNTIME_PATH = Path("/data/kinect-runtime.json")
MEDIA_DIR = Path("/media/codex_kinect_satellite")
HA_API = "http://supervisor/core/api"


def _cmd(args):
    try:
        return subprocess.check_output(args, text=True, stderr=subprocess.STDOUT, timeout=3).strip()
    except Exception as exc:
        return f"ERROR: {exc}"


def pulse_devices(kind):
    noun = "sources" if kind == "source" else "sinks"
    text = _cmd(["pactl", "list", "short", noun])
    if text.startswith("ERROR:"):
        return []
    out = []
    for line in text.splitlines():
        parts = line.split("\t")
        if len(parts) >= 2:
            out.append({
                "index": parts[0],
                "name": parts[1],
                "module": parts[2] if len(parts) > 2 else "",
                "format": parts[3] if len(parts) > 3 else "",
                "state": parts[4] if len(parts) > 4 else "",
                "raw": line,
            })
    return out


def rms(samples):
    if samples is None or getattr(samples, "size", 0) == 0:
        return 0.0
    f = samples.astype(np.float64, copy=False)
    return float(np.sqrt(np.mean(f * f)))


def dbfs(value):
    if value <= 0:
        return -90.0
    return max(-90.0, min(0.0, 20.0 * math.log10(value / 32768.0)))


def _write_wav(path: Path, pcm: bytes):
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(RATE)
        wf.writeframes(pcm)


class DiagnosticServer:
    def __init__(self, sat):
        self.sat = sat
        self.runner = None
        self.ha_session = None
        self._usb_at = 0.0
        self._usb = {}
        self._dev_at = 0.0
        self._dev = {"sources": [], "sinks": []}
        self._ha_players_at = 0.0
        self._ha_players = []
        self._ha_error = ""

    async def start(self):
        @web.middleware
        async def ingress_only(request, handler):
            remote = request.remote or ""
            if remote not in {"172.30.32.2", "127.0.0.1", "::1"}:
                return web.Response(status=403, text="Ingress only")
            return await handler(request)

        token = os.environ.get("SUPERVISOR_TOKEN", "")
        headers = {"Authorization": f"Bearer {token}", "Content-Type": "application/json"} if token else {}
        self.ha_session = ClientSession(headers=headers, timeout=ClientTimeout(total=8))
        MEDIA_DIR.mkdir(parents=True, exist_ok=True)

        app = web.Application(middlewares=[ingress_only])
        app.router.add_get("/", self.index)
        app.router.add_get("/api/status", self.status)
        app.router.add_post("/api/output", self.set_output)
        app.router.add_post("/api/input", self.set_input)
        app.router.add_post("/api/test-speaker", self.test_speaker)
        app.router.add_post("/api/restart-capture", self.restart_capture)
        app.router.add_post("/api/wake", self.force_wake)
        app.router.add_post("/api/end", self.end_session)
        app.router.add_post("/api/refresh-ha", self.refresh_ha)

        self.runner = web.AppRunner(app, access_log=None)
        await self.runner.setup()
        site = web.TCPSite(self.runner, "0.0.0.0", WEB_PORT)
        await site.start()
        self.sat.log(f"diagnostic UI listening on ingress port {WEB_PORT}")

    async def close(self):
        if self.ha_session:
            await self.ha_session.close()
            self.ha_session = None
        if self.runner:
            await self.runner.cleanup()
            self.runner = None

    async def index(self, request):
        return web.Response(text=UI_HTML, content_type="text/html")

    def _usb_snapshot(self):
        now = time.monotonic()
        if self._usb and now - self._usb_at < 2:
            return self._usb
        raw = _cmd(["lsusb"])
        low = raw.lower()
        self._usb = {
            "raw": raw,
            "motor": "045e:02b0" in low,
            "camera": "045e:02ae" in low,
            "audio": "045e:02bb" in low,
            "pre_audio": "045e:02ad" in low,
        }
        self._usb_at = now
        return self._usb

    def _devices(self):
        now = time.monotonic()
        if now - self._dev_at < 1.5:
            return self._dev
        self._dev = {"sources": pulse_devices("source"), "sinks": pulse_devices("sink")}
        self._dev_at = now
        return self._dev

    async def ha_media_players(self, force=False):
        now = time.monotonic()
        if not force and self._ha_players and now - self._ha_players_at < 5:
            return self._ha_players
        token = os.environ.get("SUPERVISOR_TOKEN", "")
        if not token:
            self._ha_error = "SUPERVISOR_TOKEN is not available"
            self._ha_players = []
            return []

        try:
            async with self.ha_session.get(f"{HA_API}/states") as resp:
                if resp.status != 200:
                    body = await resp.text()
                    raise RuntimeError(f"Home Assistant API HTTP {resp.status}: {body[:300]}")
                states = await resp.json()

            players = []
            for item in states:
                entity_id = str(item.get("entity_id", ""))
                if not entity_id.startswith("media_player."):
                    continue
                attrs = item.get("attributes") or {}
                players.append({
                    "entity_id": entity_id,
                    "name": str(attrs.get("friendly_name") or entity_id),
                    "state": str(item.get("state", "unknown")),
                    "supported_features": int(attrs.get("supported_features") or 0),
                    "device_class": str(attrs.get("device_class") or ""),
                })
            players.sort(key=lambda x: (x["state"] == "unavailable", x["name"].lower()))
            self._ha_players = players
            self._ha_players_at = now
            self._ha_error = ""
            return players
        except Exception as exc:
            self._ha_error = str(exc)
            self._ha_players_at = now
            return self._ha_players

    def snapshot(self):
        sat = self.sat
        usb = self._usb_snapshot()
        dev = self._devices()
        source = next((x for x in dev["sources"] if x["name"] == sat.source_name), None)
        sink = next((x for x in dev["sinks"] if x["name"] == sat.sink_name), None)
        source_format = source["format"] if source else ""
        kinect_source = bool(source and "kinect" in source["raw"].lower())
        four_channel = "4ch" in source_format and "16000hz" in source_format.lower()
        only_null = bool(dev["sinks"]) and all("auto_null" in x["name"] for x in dev["sinks"])
        beam = sat.beam
        output_is_ha = sat.output_setting.startswith("ha:")

        return {
            "version": "0.3.1",
            "uptime_s": int(time.time() - sat.started_at),
            "kinect": {
                "ready": bool(usb["audio"] and kinect_source and four_channel and sat.capture_alive),
                "firmware_present": Path("/data/kinect-firmware/UACFirmware").exists(),
                "usb": usb,
                "source_is_kinect": kinect_source,
                "four_channel_16k": four_channel,
            },
            "audio": {
                "input_setting": sat.input_setting,
                "source_name": sat.source_name,
                "source_format": source_format,
                "output_setting": sat.output_setting,
                "output_is_ha": output_is_ha,
                "sink_name": sat.sink_name,
                "sink_format": sink["format"] if sink else "",
                "only_null_sink": only_null,
                "sources": [x for x in dev["sources"] if ".monitor" not in x["name"].lower() and "auto_null" not in x["name"].lower()],
                "sinks": dev["sinks"],
                "ha_api_ok": not bool(self._ha_error),
                "ha_api_error": self._ha_error,
                "ha_last_error": sat.ha_output_last_error,
                "ha_last_play_at": sat.ha_output_last_play_at,
                "ha_play_count": sat.ha_output_play_count,
                "ha_buffer_bytes": len(sat.ha_output_buffer),
            },
            "capture": {
                "alive": sat.capture_alive,
                "restarts": sat.capture_restarts,
                "last_error": sat.last_capture_error,
                "last_audio_at": sat.last_capture_at,
                "waiting_for_source": sat.capture_waiting_for_source,
                "channel_rms": [round(v, 1) for v in sat.channel_rms],
                "channel_dbfs": [round(v, 1) for v in sat.channel_dbfs],
                "mono_rms": round(sat.mono_rms, 1),
                "mono_dbfs": round(sat.mono_dbfs, 1),
            },
            "beam": {
                "delays": [round(float(v), 2) for v in beam.tau] if beam else [],
                "estimates": beam.estimates if beam else 0,
            },
            "wake": {
                "word": sat.cfg.get("wake_word", "hola sol"),
                "last_heard": sat.last_wake_heard,
                "last_detected": sat.last_wake_text,
                "last_detected_at": sat.last_wake_at,
                "vosk_loaded": sat.vosk_loaded,
            },
            "transport": {
                "connected": sat.ws_connected,
                "state": sat.state,
                "revision": sat.revision,
                "session_id": sat.session_id,
                "server_url": sat.cfg["server_url"],
                "uplink_blocks": sat.uplink_blocks,
                "downlink_packets": sat.downlink_packets,
                "last_downlink_at": sat.last_downlink_at,
            },
        }

    async def status(self, request):
        snap = await asyncio.to_thread(self.snapshot)
        snap["audio"]["ha_players"] = await self.ha_media_players()
        snap["audio"]["ha_api_ok"] = not bool(self._ha_error)
        snap["audio"]["ha_api_error"] = self._ha_error
        return web.json_response(snap)

    async def _body(self, request):
        try:
            return await request.json()
        except Exception:
            return {}

    async def set_output(self, request):
        body = await self._body(request)
        try:
            await asyncio.to_thread(self.sat.set_output, str(body.get("sink", "auto")))
            self._dev_at = 0
            return web.json_response({"ok": True})
        except Exception as exc:
            return web.json_response({"ok": False, "error": str(exc)}, status=400)

    async def set_input(self, request):
        body = await self._body(request)
        try:
            await asyncio.to_thread(self.sat.set_input, str(body.get("source", "auto")))
            self._dev_at = 0
            return web.json_response({"ok": True})
        except Exception as exc:
            return web.json_response({"ok": False, "error": str(exc)}, status=400)

    async def test_speaker(self, request):
        try:
            await self.sat.test_output()
            return web.json_response({"ok": True})
        except Exception as exc:
            return web.json_response({"ok": False, "error": str(exc)}, status=500)

    async def restart_capture(self, request):
        await asyncio.to_thread(self.sat.request_capture_restart)
        return web.json_response({"ok": True})

    async def force_wake(self, request):
        if not self.sat.ws_connected or self.sat.state != "idle":
            return web.json_response({"ok": False, "error": f"state={self.sat.state}"}, status=409)
        await self.sat.enqueue_json({"type": "event", "event": "wake", "source": "diagnostic_ui"})
        return web.json_response({"ok": True})

    async def end_session(self, request):
        if not self.sat.ws_connected or self.sat.state not in {"activating", "listening"}:
            return web.json_response({"ok": False, "error": f"state={self.sat.state}"}, status=409)
        await self.sat.enqueue_json({
            "type": "event", "event": "end", "reason": "diagnostic_ui", "sessionId": self.sat.session_id
        })
        return web.json_response({"ok": True})

    async def refresh_ha(self, request):
        self._ha_players_at = 0
        players = await self.ha_media_players(force=True)
        return web.json_response({"ok": not bool(self._ha_error), "players": len(players), "error": self._ha_error})

    async def play_pcm_on_ha(self, entity_id: str, pcm: bytes, title="Sol"):
        if not entity_id.startswith("media_player."):
            raise ValueError("Invalid Home Assistant media_player entity")
        if not pcm:
            return

        MEDIA_DIR.mkdir(parents=True, exist_ok=True)
        filename = f"sol_{int(time.time())}_{uuid.uuid4().hex[:8]}.wav"
        path = MEDIA_DIR / filename
        await asyncio.to_thread(_write_wav, path, pcm)
        await asyncio.to_thread(self._cleanup_media)

        media_id = f"media-source://media_source/local/codex_kinect_satellite/{filename}"
        payload = {
            "entity_id": entity_id,
            "media_content_id": media_id,
            "media_content_type": "audio/wav",
            "announce": False,
            "extra": {"title": title},
        }

        try:
            await self._call_play_media(payload)
        except Exception:
            fallback = {
                "entity_id": entity_id,
                "media_content_id": media_id,
                "media_content_type": "music",
            }
            await self._call_play_media(fallback)

    async def _call_play_media(self, payload):
        if not os.environ.get("SUPERVISOR_TOKEN"):
            raise RuntimeError("Home Assistant API token is unavailable")
        async with self.ha_session.post(f"{HA_API}/services/media_player/play_media", json=payload) as resp:
            body = await resp.text()
            if resp.status < 200 or resp.status >= 300:
                raise RuntimeError(f"media_player.play_media HTTP {resp.status}: {body[:400]}")

    def _cleanup_media(self):
        cutoff = time.time() - 900
        try:
            files = sorted(MEDIA_DIR.glob("sol_*.wav"), key=lambda p: p.stat().st_mtime, reverse=True)
            for idx, path in enumerate(files):
                try:
                    if idx >= 12 or path.stat().st_mtime < cutoff:
                        path.unlink(missing_ok=True)
                except OSError:
                    pass
        except OSError:
            pass


UI_HTML = r'''<!doctype html>
<html lang="es"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Kinect Satellite</title>
<style>
:root{color-scheme:dark;font-family:Inter,system-ui,sans-serif;background:#101318;color:#eef2f7}body{margin:0;padding:18px}.wrap{max-width:1050px;margin:auto}.top{display:flex;justify-content:space-between;align-items:center;gap:12px;margin-bottom:16px}h1{font-size:22px;margin:0}.badge{padding:6px 10px;border-radius:999px;background:#2a3038;font-size:12px}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px}.card{background:#181d24;border:1px solid #2b323c;border-radius:14px;padding:15px}.card h2{font-size:15px;margin:0 0 12px}.row{display:flex;justify-content:space-between;gap:12px;margin:7px 0;font-size:13px}.muted{color:#9ca8b5}.mono{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;word-break:break-all}.meter{height:10px;background:#272e37;border-radius:99px;overflow:hidden}.bar{height:100%;width:0;background:linear-gradient(90deg,#4ecb71,#d9c849,#e36262);transition:width .12s}.mic{display:grid;grid-template-columns:45px 1fr 62px;gap:8px;align-items:center;margin:8px 0;font-size:12px}select,button{background:#242b34;color:#eef2f7;border:1px solid #39424e;border-radius:9px;padding:9px 10px}select{width:100%;margin-bottom:8px}button{cursor:pointer;margin:3px 4px 3px 0}button:hover{background:#303945}.alert{padding:10px;border-radius:10px;background:#4b3b13;color:#ffe093;font-size:13px;margin:8px 0}.error{background:#4a1f25;color:#ffb5be}.small{font-size:12px}.wide{grid-column:1/-1}pre{white-space:pre-wrap;font-size:11px;max-height:180px;overflow:auto;background:#11151a;padding:10px;border-radius:9px}.dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#888;margin-right:6px}.green{background:#49d17d}.red{background:#ef6370}.sectionLabel{font-size:11px;text-transform:uppercase;letter-spacing:.08em;color:#7f8b98;margin:7px 0}
</style></head><body><div class="wrap">
<div class="top"><h1>Codex Kinect Satellite</h1><span id="version" class="badge">...</span></div>
<div id="alerts"></div>
<div class="grid">
<div class="card"><h2>Kinect</h2><div class="row"><span>Estado</span><b id="kready">...</b></div><div class="row"><span>USB Audio</span><span id="usbAudio">...</span></div><div class="row"><span>Pulse source</span><span id="sourceFmt">...</span></div><div class="row"><span>Firmware</span><span id="firmware">...</span></div></div>
<div class="card"><h2>Codex Remote</h2><div class="row"><span>WebSocket</span><b id="ws">...</b></div><div class="row"><span>Estado</span><b id="state">...</b></div><div class="row"><span>Session</span><span id="session" class="mono">...</span></div><div class="row"><span>Uplink / downlink</span><span id="packets">...</span></div><button onclick="postApi('api/wake')">Forzar wake</button><button onclick="postApi('api/end')">Terminar sesión</button></div>
<div class="card"><h2>Captura</h2><div class="row"><span>pacat</span><b id="capAlive">...</b></div><div class="row"><span>Reinicios</span><span id="restarts">0</span></div><div class="row"><span>Último audio</span><span id="lastAudio">...</span></div><div id="capError" class="small muted">Sin errores</div><button onclick="postApi('api/restart-capture')">Reiniciar captura</button></div>
<div class="card"><h2>Wake word</h2><div class="row"><span>Objetivo</span><b id="wakeWord">...</b></div><div class="row"><span>Último oído</span><span id="lastHeard">...</span></div><div class="row"><span>Último detectado</span><span id="lastWake">...</span></div><div class="row"><span>Vosk</span><span id="vosk">...</span></div></div>
<div class="card wide"><h2>Micrófonos Kinect — 4 canales</h2><div id="mics"></div><div class="mic"><span>Mono</span><div class="meter"><div id="monoBar" class="bar"></div></div><span id="monoDb">-90 dB</span></div><div class="row"><span>Beam delays</span><span id="delays" class="mono">...</span></div><div class="row"><span>Estimaciones</span><span id="estimates">0</span></div></div>
<div class="card"><h2>Entrada</h2><select id="inputSel"></select><button onclick="applyInput()">Aplicar entrada</button><div class="small muted" id="inputName"></div></div>
<div class="card"><h2>Salida / parlante</h2><select id="outputSel"></select><button onclick="applyOutput()">Aplicar salida</button><button onclick="postApi('api/test-speaker')">🔊 Probar parlante</button><button onclick="postApi('api/refresh-ha')">↻ Actualizar HA</button><div class="small muted" id="outputName"></div><div class="small muted" id="haStatus"></div></div>
<div class="card wide"><h2>USB Kinect</h2><pre id="usbRaw"></pre></div>
</div></div>
<script>
let lastSources='',lastOutputs='';
function pct(db){return Math.max(0,Math.min(100,(Number(db)+60)*100/60));}
function ago(ts){if(!ts)return '—';let s=Math.max(0,Math.round(Date.now()/1000-ts));return s<60?s+' s':Math.floor(s/60)+' min';}
function led(ok,yes,no){return '<span class="dot '+(ok?'green':'red')+'"></span>'+(ok?yes:no);}
function fillInput(items,setting){let key=JSON.stringify(items.map(x=>x.name));if(lastSources===key)return;lastSources=key;let el=document.getElementById('inputSel');el.innerHTML='';let a=document.createElement('option');a.value='auto';a.textContent='Auto · Kinect preferido';el.appendChild(a);items.forEach(x=>{let o=document.createElement('option');o.value=x.name;o.textContent=x.name+'  ['+x.format+']';el.appendChild(o);});el.value=setting||'auto';}
function fillOutput(sinks,players,setting){let key=JSON.stringify([sinks.map(x=>x.name),players.map(x=>[x.entity_id,x.name,x.state])]);if(lastOutputs===key){document.getElementById('outputSel').value=setting||'auto';return;}lastOutputs=key;let el=document.getElementById('outputSel');el.innerHTML='';
let g1=document.createElement('optgroup');g1.label='Local · PulseAudio';let a=document.createElement('option');a.value='auto';a.textContent='Auto';g1.appendChild(a);sinks.forEach(x=>{let o=document.createElement('option');o.value=x.name;o.textContent=x.name+'  ['+x.format+']';g1.appendChild(o);});el.appendChild(g1);
let g2=document.createElement('optgroup');g2.label='Home Assistant · media_player';players.forEach(x=>{let o=document.createElement('option');o.value='ha:'+x.entity_id;o.textContent=x.name+' · '+x.state+'  ('+x.entity_id+')';g2.appendChild(o);});el.appendChild(g2);el.value=setting||'auto';}
async function postApi(url,body){let r=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body||{})});let j=await r.json().catch(()=>({}));if(!r.ok)alert(j.error||('HTTP '+r.status));setTimeout(refresh,250);return j;}
function applyInput(){return postApi('api/input',{source:document.getElementById('inputSel').value});}
function applyOutput(){return postApi('api/output',{sink:document.getElementById('outputSel').value});}
async function refresh(){try{let r=await fetch('api/status',{cache:'no-store'});let s=await r.json();
document.getElementById('version').textContent='v'+s.version;
document.getElementById('kready').innerHTML=led(s.kinect.ready,'LISTO','NO LISTO');
document.getElementById('usbAudio').textContent=s.kinect.usb.audio?'045e:02bb ✓':'No';
document.getElementById('sourceFmt').textContent=s.audio.source_format||'—';
document.getElementById('firmware').textContent=s.kinect.firmware_present?'OK':'Falta';
document.getElementById('ws').innerHTML=led(s.transport.connected,'Conectado','Desconectado');
document.getElementById('state').textContent=s.transport.state;
document.getElementById('session').textContent=s.transport.session_id||'—';
document.getElementById('packets').textContent=s.transport.uplink_blocks+' / '+s.transport.downlink_packets;
document.getElementById('capAlive').innerHTML=led(s.capture.alive,'Activo',s.capture.waiting_for_source?'Esperando Kinect':'Caído');
document.getElementById('restarts').textContent=s.capture.restarts;
document.getElementById('lastAudio').textContent=ago(s.capture.last_audio_at);
let ce=document.getElementById('capError');ce.textContent=s.capture.last_error||'Sin errores';ce.className='small '+(s.capture.last_error?'error':'muted');
document.getElementById('wakeWord').textContent=s.wake.word;
document.getElementById('lastHeard').textContent=s.wake.last_heard||'—';
document.getElementById('lastWake').textContent=(s.wake.last_detected||'—')+(s.wake.last_detected_at?' · '+ago(s.wake.last_detected_at):'');
document.getElementById('vosk').textContent=s.wake.vosk_loaded?'OK':'Cargando';
let m=document.getElementById('mics');if(!m.children.length){for(let i=0;i<s.capture.channel_dbfs.length;i++){let row=document.createElement('div');row.className='mic';row.innerHTML='<span>Mic '+(i+1)+'</span><div class="meter"><div id="m'+i+'" class="bar"></div></div><span id="md'+i+'">-90 dB</span>';m.appendChild(row);}}
s.capture.channel_dbfs.forEach((v,i)=>{let b=document.getElementById('m'+i),d=document.getElementById('md'+i);if(b)b.style.width=pct(v)+'%';if(d)d.textContent=v.toFixed(1)+' dB';});
document.getElementById('monoBar').style.width=pct(s.capture.mono_dbfs)+'%';document.getElementById('monoDb').textContent=s.capture.mono_dbfs.toFixed(1)+' dB';
document.getElementById('delays').textContent=s.beam.delays.join(', ');document.getElementById('estimates').textContent=s.beam.estimates;
fillInput(s.audio.sources,s.audio.input_setting);fillOutput(s.audio.sinks,s.audio.ha_players||[],s.audio.output_setting);
document.getElementById('inputName').textContent='Activo: '+(s.audio.source_name||'—');
let outText=s.audio.output_is_ha?s.audio.output_setting.replace(/^ha:/,''):(s.audio.sink_name||'—');document.getElementById('outputName').textContent='Activo: '+outText;
document.getElementById('haStatus').textContent=s.audio.ha_api_ok?('HA media_player: '+(s.audio.ha_players||[]).length+' · reproducciones: '+s.audio.ha_play_count):('HA API: '+(s.audio.ha_api_error||'error'));
document.getElementById('usbRaw').textContent=s.kinect.usb.raw||'';
let alerts=[];if(s.audio.only_null_sink&&!s.audio.output_is_ha)alerts.push('<div class="alert">⚠️ Sólo existe <b>auto_null</b>. Podés elegir abajo una entidad <b>media_player</b> de Home Assistant como salida.</div>');
if(s.audio.output_is_ha)alerts.push('<div class="alert">ℹ️ Salida Home Assistant activa: la respuesta se bufferiza y se reproduce vía <b>media_player.play_media</b>. Tiene más latencia que una salida local.</div>');
if(!s.audio.ha_api_ok)alerts.push('<div class="alert error">Home Assistant API: '+String(s.audio.ha_api_error||'error').replace(/</g,'&lt;')+'</div>');
if(s.audio.ha_last_error)alerts.push('<div class="alert error">Último error de salida HA: '+String(s.audio.ha_last_error).replace(/</g,'&lt;')+'</div>');
if(!s.kinect.four_channel_16k)alerts.push('<div class="alert error">Kinect no está expuesto como 4ch / 16 kHz.</div>');
if(s.capture.last_error)alerts.push('<div class="alert error">Último error de captura: '+String(s.capture.last_error).replace(/</g,'&lt;')+'</div>');
document.getElementById('alerts').innerHTML=alerts.join('');
}catch(e){document.getElementById('alerts').innerHTML='<div class="alert error">No se pudo leer estado: '+e+'</div>';}}
refresh();setInterval(refresh,800);
</script></body></html>'''
