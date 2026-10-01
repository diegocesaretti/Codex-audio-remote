# Codex Audio Remote v2

Android 6+ audio satellite + Windows companion for using Codex Voice remotely over the LAN.

The v2 rewrite has one design rule above everything else:

> **Windows is the only authority for conversation state. Android mirrors that state and derives its microphone/speaker policy from it.**

There are no build-time source patches and no independent client/server heuristics trying to guess whether a session is alive.

## Why v2 exists

The original prototype grew through incremental fixes. Eventually the same conversation could be represented by several unrelated signals:

- Android `streaming`, `conversationActive` and `activationPending` flags;
- local timeouts and overlay state;
- WebSocket lifecycle callbacks;
- Windows microphone-registry activity;
- delayed smart-close/graceful-end logic;
- build-time Python patches modifying source before compilation.

That made valid combinations such as “server says listening, Android wake thread still owns AudioRecord” possible.

v2 replaces all of that with one explicit state machine and idempotent synchronization.

## Authoritative session states

```text
IDLE
  ↓ wake
ACTIVATING
  ↓ Codex Voice confirmed
LISTENING
  ↓ explicit end event
ENDING
  ↓ cleanup/restore complete
IDLE
```

Every server state snapshot contains:

```json
{
  "type": "state",
  "protocol": 2,
  "state": "listening",
  "sessionId": "...",
  "revision": 12,
  "reason": "codex_ready"
}
```

`sessionId` identifies one conversation. `revision` is monotonic and prevents Android from applying an older state snapshot after a newer one.

## Android audio policy

Android has one coordinator: `RemoteService.reconcileAudioPolicy()`.

| Server state | Wake mic | Conversation mic | Downlink speaker |
|---|---:|---:|---:|
| disconnected | off | off | off |
| idle | on | off | off |
| activating | off | off | off |
| listening | off | on | on |
| ending | off | off | off |

No timeout, overlay tap, Vosk callback or AudioRecord thread changes conversation state locally. Those components only emit events. Android changes policy when Windows sends the next authoritative state snapshot.

### Wake word

- Vosk runs locally.
- Default phrase: `hola sol`.
- Android 6 / API23 uses direct `AudioRecord` instead of `SpeechService`.
- API23 capture preference order for wake is `DEFAULT`, `MIC`, `VOICE_RECOGNITION`, `CAMCORDER`.
- `hola` followed by `sol` is accepted as a two-part partial sequence.
- A real input heartbeat is tracked. If an API23 wake AudioRecord remains nominally open but stops producing buffers, it is recycled automatically.

### Conversation microphone

The conversation AudioRecord exists only in authoritative `LISTENING`.

Android sends one `audio_config` control frame for the current `sessionId`, followed by PCM16 mono binary frames. If the capture thread fails or stops producing buffers while the state is still `LISTENING`, it is reopened from the same policy rather than inventing a new state transition.

### Runtime indicators

The top of the Android app shows the state used by the actual audio coordinator:

- PC connected / connecting / disconnected;
- current authoritative server state and revision;
- wake listening + real RMS/audio heartbeat;
- conversation mic listening + uplink heartbeat.

These indicators are not a separate health model; they are rendered from the same variables used by `reconcileAudioPolicy()`.

## Windows state owner

`SessionServerV2` owns:

- current state;
- current `sessionId`;
- monotonic revision;
- current WebSocket generation;
- virtual-cable uplink;
- loopback downlink;
- session cancellation and cleanup.

A newly connected Android client immediately receives the current full state snapshot. This makes reconnect deterministic instead of treating every connection as a new conversation.

If Android disconnects during an active session, Windows keeps the authoritative session for a short reconnect grace period. A reconnect receives the existing `sessionId` and resumes the audio policy. If no client returns, Windows ends the session and restores audio routing.

## Codex Voice activation

Windows uses the Windows microphone capability registry for one purpose only: **confirming the initial `ACTIVATING → LISTENING` transition**.

It does **not** use temporary Codex microphone inactivity to decide that a conversation ended. Codex may think, speak, or temporarily release capture without changing the v2 session state.

The normal activation path is:

```text
wake event
  ├─ start optional Bluetooth reconnect in parallel
  ├─ save original Windows audio defaults
  ├─ default capture → CABLE Output
  └─ Ctrl+Q
       ↓
Codex mic becomes active
       ↓
server state = LISTENING
       ↓
Android opens conversation mic
```

## Windows audio routing

The companion stores the original capture/render endpoints before changing them and restores them when the session ends or the companion recovers from an unclean previous run.

Default virtual devices:

- recording endpoint used by Codex: `CABLE Output`;
- render endpoint receiving Android PCM: `CABLE Input`.

### Bluetooth output

The selected response device is configured from **Audio de respuesta / Downlink…** in the Windows tray menu.

If it is offline, `btcom` A2DP reconnect runs in parallel with Codex activation. A render handoff watcher switches the Windows default playback roles as soon as the selected endpoint becomes Active. The loopback downlink is rebound after the Bluetooth task completes.

The companion requests only A2DP service `110b`. `btcom.exe` is not bundled.

## Protocol v2

Android → Windows control frames:

```json
{"type":"hello","protocol":2,"name":"Android satellite"}
{"type":"sync"}
{"type":"event","event":"wake","source":"voice"}
{"type":"event","event":"end","reason":"overlay_tap","sessionId":"..."}
{"type":"audio_config","sessionId":"...","sampleRate":48000,"channels":1,"chunkMs":45,"quality":80,"latency":55}
```

Windows → Android:

```json
{"type":"hello","protocol":2,"server":"CodexAudioRemote"}
{"type":"state","protocol":2,"state":"idle","sessionId":"","revision":0,"reason":"startup"}
```

After `audio_config`, Android microphone audio is sent as PCM16 mono binary WebSocket frames while the authoritative state is `LISTENING`. Windows downlink PCM is sent as binary frames in the opposite direction.

The server still accepts the old `wake`, `end_session`, `audio_start` and `audio_stop` controls temporarily so an old APK can connect during migration, but v2 clients do not use them.

## Build

CI builds source directly. No Python script rewrites Java or C# before compilation.

### Windows

```powershell
cd windows/CodexAudioRemote.Server
dotnet publish -c Release -r win-x64 --self-contained false
```

The experimental OAuth Realtime build also bundles a patched `codex.exe`. Codex
Realtime V3 requires WebRTC for ChatGPT OAuth, and the ChatGPT backend assigns
the realtime model server-side. The patch in
`patches/openai-codex-oauth-webrtc.patch` omits the unsupported `session.model`
field only for ChatGPT-authenticated WebRTC calls. It does not read, copy or
store OAuth tokens; Codex continues to own login and token refresh.

Run the **Build OAuth Realtime Windows** workflow manually to produce the
`CodexAudioRemote-Windows-OAuthRealtime` artifact. The Windows companion uses
the bundled `codex.exe` when present and falls back to the installed Codex CLI
for ordinary source builds.

Useful options:

```text
--port 8765
--shortcut ctrl+q
--activation-timeout 6000
--virtual-mic "CABLE Output"
--cable-input "CABLE Input"
--end-restore-timeout 2500
--list-devices
```

### Android

The Android app is Java/XML and has `minSdk 23`. CI bundles `vosk-model-small-es-0.42.zip` into the APK; `CodexRemoteApp` installs it into app storage on first launch.

On a fresh API23 install the conversation capture default is Android `DEFAULT`, because that is the most reliable mode on the target Android 6 device. The setting remains user-configurable.

## Main source files

- `windows/CodexAudioRemote.Server/SessionServerV2.cs` — authoritative protocol/session state machine.
- `windows/CodexAudioRemote.Server/AudioPlatformV2.cs` — Windows capture/render switching + virtual-cable sink.
- `windows/CodexAudioRemote.Server/BtcomBluetoothReconnect.cs` — optional A2DP reconnect.
- `windows/CodexAudioRemote.Server/LoopbackDownlink.cs` — PC response loopback to Android.
- `android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java` — v2 transport + deterministic audio coordinator.
- `android/app/src/main/java/com/bwa3d/codexremote/RuntimeStatusPanel.java` — live state/audio health view.

## Home Assistant adapter

The existing `HomeAssistantApiServer`, `ExternalConversationHub` and context-injection sources are still present while the v2 core is being stabilized, but **the v2 entrypoint does not currently start that adapter**. It will be reattached through the same authoritative state machine rather than being allowed to own a parallel session lifecycle.

That separation is intentional: external integrations must request transitions from the v2 state owner; they must never create a second source of session truth.
