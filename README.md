# Codex Audio Remote

Prototype Android satellite + Windows companion for using Codex Voice remotely over the LAN.

## Goals

- Android 6.0+ (`minSdk 23`)
- lightweight foreground microphone service
- local wake-word with Vosk
- WebSocket link to a Windows PC
- trigger the Codex Voice shortcut (`Ctrl+Q` by default)
- confirm activation by watching the Windows microphone capability registry for `OpenAI.Codex_*`
- temporarily switch the Windows default capture endpoint to the remote/virtual microphone during a Codex Voice session
- restore the user's original microphone automatically when Voice ends, fails, disconnects, or the companion restarts after an unclean shutdown
- stream Android microphone audio to the PC
- later: stream PC/Codex audio back to Android and show a lightweight overlay over a Home Assistant dashboard

## Repository layout

- `windows/CodexAudioRemote.Server` — .NET 8 console prototype
- `android` — Android/Java satellite prototype

## Current prototype state

The signaling + temporary microphone switching milestone is implemented:

1. Android connects to the PC over WebSocket.
2. Android sends a `wake` message (manual test button initially; Vosk service scaffold included).
3. Windows saves the current default capture endpoint IDs for Console, Multimedia and Communications.
4. Windows changes those defaults to the configured virtual microphone (default name match: `CABLE Output`).
5. Windows sends `Ctrl+Q`.
6. Windows polls `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\CapabilityAccessManager\\ConsentStore\\microphone` for `OpenAI.Codex_*`.
7. When `LastUsedTimeStart > 0 && LastUsedTimeStop == 0`, Windows replies `codex_listening`.
8. Android can begin PCM microphone streaming.
9. When Codex stops using the microphone, the companion waits a short debounce period and restores the original Windows defaults.

The microphone registry behavior is intentionally treated as an observed Windows behavior rather than a guaranteed public API contract. The default-endpoint setter is also isolated because Windows exposes public APIs for discovering/default endpoint routing, but not a supported public setter equivalent for desktop apps.

## Windows quick start

Requirements: Windows, .NET 8 SDK, Codex/ChatGPT Windows application installed.

For the prototype, install a virtual audio cable such as VB-CABLE. Its recording endpoint is normally named something containing `CABLE Output`.

First inspect the capture devices Windows exposes:

```powershell
cd windows/CodexAudioRemote.Server
dotnet run -- --list-devices
```

Then start the server:

```powershell
dotnet run -- --port 8765 --virtual-mic "CABLE Output"
```

Allow TCP port 8765 through Windows Firewall for the local/private network.

Optional arguments:

```text
--port 8765
--shortcut ctrl+q
--activation-timeout 6000
--virtual-mic "CABLE Output"
--restore-delay 800
--list-devices
```

The server binds to `http://+:8765/ws/`.

### Optional Bluetooth reconnect with btcom

When the saved response output is a paired Bluetooth device that is currently
offline, the Windows companion can ask `btcom` to enable the A2DP sink service
(`110b`) before opening Codex Voice. It then waits for the saved audio endpoint
to become `Active`. If either the command or the wait fails, the existing safe
fallback is used and the saved selection is left untouched.

`btcom.exe` is detected from the configured path, the
`CODEX_AUDIO_REMOTE_BTCOM` environment variable, `PATH`, and the usual
`Program Files\\Bluetooth Command Line Tools` install folders. The path and the
Active wait (1–15 seconds, 6 by default) can be changed from
**Audio de respuesta / Downlink…** in the tray menu.

Only A2DP `110b` is requested. The companion does not enable HFP/HSP. If it
connected an offline Bluetooth output for the conversation, it disconnects
that A2DP service before restoring the playback device that was active before
the conversation. A Bluetooth output that was already connected is left alone.

Bluetooth Command Line Tools 1.2 can return error 87 when A2DP is already
registered but inactive. In that specific case only, the companion refreshes
service `110b` (`-r` followed immediately by `-c`). It does not unpair the
device and does not touch any hands-free service.

`btcom.exe` is deliberately not bundled. Its publisher allows personal and
commercial use but does not grant redistribution rights without express
authorization. Install Bluetooth Command Line Tools separately from the
publisher if you want this optional reconnect behavior.

### Safety / recovery behavior

Before changing defaults, the companion writes `audio-restore.json` beside the executable. It stores the original endpoint IDs for the three Windows audio roles.

Normal session end, activation timeout, client disconnect and Ctrl+C all restore the original microphone. If the process crashes while the virtual microphone is still default, the next launch sees `audio-restore.json` and restores the saved endpoints before accepting clients.

The restore delay defaults to 800 ms so a short close/reopen transition inside Codex Voice does not cause the default input to flap between devices.

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

The Windows server currently receives and meters these frames. Feeding that PCM into the render side of the virtual cable (`CABLE Input`) and implementing the PC-audio downlink are the next milestone.

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
{"type":"activation_failed","reason":"virtual_mic_not_found"}
{"type":"activation_failed","reason":"codex_mic_timeout"}
```

Binary frames are microphone PCM while a voice session is active.

## Next milestone

- feed Android PCM into a selectable Windows render endpoint / virtual audio cable (`CABLE Input`)
- WASAPI loopback capture for Codex output → Android speaker
- functional Vosk wake-word model loading
- overlay states: Activating / Listening / Speaking / Error
- reconnect + boot persistence
