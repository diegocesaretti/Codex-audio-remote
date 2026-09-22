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
