# Codex Kinect Satellite

Experimental Raspberry Pi / Home Assistant OS satellite for **Codex Audio Remote Protocol v2**.

## What it does

- uses the Xbox 360 Kinect four-microphone USB array at 16 kHz;
- performs GCC-PHAT + delay-and-sum beamforming locally;
- runs Vosk locally for the wake phrase (default `hola sol`);
- sends the existing v2 wake event to the Windows Companion;
- when Windows becomes authoritative `listening`, streams PCM16 mono 16 kHz over the existing WebSocket;
- plays the existing 16 kHz PCM downlink through the Home Assistant audio output;
- defaults to half-duplex while Codex audio is playing to prevent the Kinect hearing Sol's own speaker output;
- recognizes configurable voice end phrases and requests the normal v2 end transition.

No change to the Windows session owner or Codex runtime is required.

## Hardware

This initial build targets **Raspberry Pi 5 / aarch64 + Home Assistant OS** and the **Xbox 360 Kinect (v1) with its separate power supply/USB adapter**.

The Kinect UAC firmware is not bundled. `kinect-audio-setup` downloads the firmware locally on first run and uploads it to the Kinect RAM. Power-cycling the Kinect removes it; this does not permanently modify the sensor.

## Install

1. In Home Assistant, add the beta branch as an app repository: `https://github.com/diegocesaretti/Codex-audio-remote#feat/rpi-kinect-satellite`. Home Assistant supports selecting a repository branch with the `#branch` suffix, so this can be tested without merging the experiment into `main`.
2. Install **Codex Kinect Satellite**.
3. Set `server_ip` to the Windows PC running Codex Audio Remote. Port `8765` matches the current v2 default.
4. Connect and power the Kinect, then start the add-on.
5. The log should show the firmware step, PulseAudio devices, Vosk model load, 4-channel capture and a WebSocket connection.

The first start downloads the Vosk Spanish small model (~39 MB) and the Kinect audio firmware from its upstream source.

## Expected flow

```text
Kinect 4ch @ 16 kHz
  -> GCC-PHAT / delay-and-sum
  -> mono PCM16
  -> Vosk wake while server is IDLE
  -> {event:wake}
  -> Windows ACTIVATING -> LISTENING
  -> audio_config 16 kHz mono
  -> binary PCM uplink

Windows downlink (PCM16 mono 16 kHz / 20 ms)
  -> Home Assistant PulseAudio output
```

## Audio device selection

`pulse_input: auto` prefers a PulseAudio source whose description contains `Kinect` or `Xbox NUI`. If no such source is visible it uses the default input and prints every available source in the log.

`pulse_output: auto` uses the Home Assistant add-on's selected/default output. Set an explicit PulseAudio source/sink name only when auto selection is wrong.

## First-version limitation: one satellite connection

The current Windows `SessionServerV2` has a single `currentPeer`. Therefore the Pi satellite **replaces the Android satellite while connected**; connecting both at once causes the latest client to supersede the previous one. Multi-room/multi-satellite arbitration is intentionally not part of this first Kinect validation build.

## Echo / barge-in

`half_duplex: true` suppresses Kinect uplink while response audio is arriving plus `speaker_hangover_ms`. This avoids a feedback/self-trigger loop but means you cannot interrupt Sol while she is speaking yet. AEC3/SpeexDSP is the next step after validating real-room Kinect quality.

## Diagnostics

Useful log lines:

- `auto-selected Kinect source` — the four-mic source was found;
- `beamforming delays=...` — GCC-PHAT obtained a reliable direction estimate;
- `wake detected` — Vosk matched the wake phrase;
- `state idle -> activating -> listening` — Windows remains session authority;
- `server audio_error` — virtual CABLE input on Windows was not opened.

If PulseAudio only exposes a mono Kinect source, beamforming cannot work correctly. Do not silently continue with mono for the final design; inspect the HA audio source/channel exposure first.


## Diagnostic Web UI

Version 0.2.0 adds an authenticated Home Assistant Ingress dashboard available through **OPEN WEB UI**.

It shows:

- Kinect USB/firmware/audio readiness;
- the actual PulseAudio input format and four-channel availability;
- live RMS/dBFS meters for all four Kinect microphones plus beamformed mono;
- GCC-PHAT beam delays and estimate count;
- wake-word activity;
- WebSocket and authoritative Codex session state;
- capture health, restart count, last capture error and audio heartbeat;
- available PulseAudio inputs and outputs.

The panel can switch input/output devices at runtime, persist the selection under `/data`, play a short speaker test tone, restart capture, send a synthetic wake event and end the current session.

If the only output is `auto_null`, Home Assistant Audio currently exposes no physical sink to the app. The panel flags this explicitly; configure a real audio output in Home Assistant before expecting Codex response audio from the Pi.

Capture is supervised in 0.2.0. If `pacat` exits, the satellite records the error, increments a restart counter and reopens the Kinect source instead of terminating the whole satellite.


## Home Assistant media-player outputs

Version 0.3.0 adds every available `media_player.*` entity from Home Assistant to the **Salida / parlante** selector in the diagnostic Web UI.

The selector now has two groups:

- **Local · PulseAudio** for HDMI, USB audio, Bluetooth sinks and other local outputs exposed by Home Assistant Audio.
- **Home Assistant · media_player** for entities such as Google Cast speakers/displays, TVs and other integrations exposing `media_player`.

The app uses the internal Home Assistant API proxy with `homeassistant_api: true` and the runtime `SUPERVISOR_TOKEN`; no user access token is stored in app options.

Remote Home Assistant outputs are intentionally **buffered** in this version. Codex downlink PCM16 mono 16 kHz is collected until the response pauses, written as a temporary WAV under `/media/codex_kinect_satellite`, and sent to the selected entity with `media_player.play_media` using a `media-source://media_source/local/... ` identifier.

This is not as low-latency as a local PulseAudio sink. It is intended to make existing Home Assistant speakers easy to test and use without additional Linux audio configuration. Compatibility still depends on the selected media-player integration being able to play local WAV media.

The **Probar parlante** button follows the selected output type. If a Home Assistant media player is selected, the test tone is also delivered through `media_player.play_media`.

Temporary WAV files are automatically cleaned up. The app keeps only a small recent set and removes older files.

While a buffered Home Assistant response is playing, Kinect wake/listening processing is suppressed for the estimated playback duration plus a safety margin to reduce self-triggering.
