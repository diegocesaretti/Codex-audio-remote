# Codex Audio Remote

Prototype Android satellite + Windows companion for using Codex Voice remotely over the LAN.

## Goals

- Android 6.0+ (`minSdk 23`)
- lightweight foreground microphone service
- local wake-word with Vosk
- WebSocket link to a Windows PC
- trigger the Codex Voice shortcut (`Ctrl+Q` by default)
- confirm activation by watching the Windows microphone capability registry for `OpenAI.Codex_*`
- stream Android microphone audio to the PC
- later: stream PC/Codex audio back to Android and show a lightweight overlay over a Home Assistant dashboard

## Repository layout

- `windows/CodexAudioRemote.Server` — .NET 8 console prototype
- `android` — Android/Java satellite prototype

## Current prototype state

The first milestone implements signaling end-to-end:

1. Android connects to the PC over WebSocket.
2. Android sends a `wake` message (manual test button initially; Vosk service scaffold included).
3. Windows sends `Ctrl+Q`.
4. Windows polls `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone` for `OpenAI.Codex_*`.
5. When `LastUsedTimeStart > 0 && LastUsedTimeStop == 0`, Windows replies `codex_listening`.
6. Android displays the confirmed state and can begin PCM microphone streaming.

The microphone registry behavior is intentionally treated as an observed Windows behavior rather than a guaranteed public API contract.

## Windows quick start

Requirements: Windows, .NET 8 SDK, Codex/ChatGPT Windows application installed.

```powershell
cd windows/CodexAudioRemote.Server
dotnet run -- --port 8765
```

Allow TCP port 8765 through Windows Firewall for the local/private network.

Optional arguments:

```text
--port 8765
--shortcut ctrl+q
--activation-timeout 6000
```

The server binds to `http://+:8765/ws/`.

## Android quick start

Open `android/` in Android Studio, set the PC IP in the app, then press **Conectar** and **Probar wake**.

The app targets Android 6.0+ and deliberately uses a small Java/XML stack instead of Compose.

### Vosk model

The code expects a Vosk model at `app/src/main/assets/model/`. The model itself is not committed because it is large. For the first transport test you can use the manual wake button without a model.

## Audio format (prototype)

Android uplink binary WebSocket frames are raw PCM:

- signed PCM16 little-endian
- mono
- 16 kHz

The Windows v0.1 server currently receives and meters these frames. Routing them into a virtual microphone and the PC-audio downlink are the next milestone.

## Protocol

Text frames are JSON.

Android → PC:

```json
{"type":"hello","name":"Kitchen tablet"}
{"type":"wake"}
{"type":"audio_start","sampleRate":16000,"channels":1}
{"type":"audio_stop"}
```

PC → Android:

```json
{"type":"hello","server":"CodexAudioRemote"}
{"type":"activating"}
{"type":"codex_listening"}
{"type":"codex_idle"}
{"type":"activation_failed"}
```

Binary frames are microphone PCM while a voice session is active.

## Next milestone

- route Android PCM to a selectable Windows playback endpoint / virtual audio cable
- WASAPI loopback capture for Codex output → Android speaker
- functional Vosk wake-word model loading
- overlay states: Activating / Listening / Speaking / Error
- reconnect + boot persistence
