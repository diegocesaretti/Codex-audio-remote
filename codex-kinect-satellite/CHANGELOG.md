# Changelog

## 0.3.2

- Migrate stale persisted `auto_null.monitor` input selections back to `auto`.
- Distinguish Kinect USB missing, pre-firmware, partial-enumeration and PulseAudio-waiting states.
- Auto-detect `045e:02ad` after a hot reconnect and re-upload UAC firmware without restarting the app.
- Resume waiting for the 4-channel PulseAudio source after firmware recovery.
- Surface explicit USB/power guidance in the diagnostic UI.


## 0.3.1

- Prevent Kinect auto input from falling back to `auto_null.monitor` when the USB PulseAudio source temporarily disappears.
- Keep the last known-good Kinect source sticky across capture recovery.
- Wait for the Kinect PulseAudio source to return instead of opening a default/null source.
- Hide monitor/null sources from the input selector.
- Skip manual capture restarts while the live Kinect stream is healthy.
- Avoid restarting capture when the selected input did not actually change.


## 0.3.0

- Add Home Assistant `media_player.*` entities to the audio output selector.
- Use the internal Home Assistant API through `SUPERVISOR_TOKEN` with `homeassistant_api: true`.
- Buffer Codex PCM responses into temporary 16 kHz mono WAV files under the Home Assistant `/media` directory.
- Play buffered responses through `media_player.play_media` using `media-source://` URLs.
- Add Home Assistant entity refresh, state labels, playback count and playback error diagnostics.
- Make the existing speaker-test button work with selected Home Assistant media players.
- Extend half-duplex suppression across remote media-player playback to reduce Kinect self-triggering.


## 0.2.0

- Add Home Assistant Ingress diagnostic dashboard.
- Add live four-channel Kinect mic meters, beamforming status and wake/session state.
- Add runtime input/output selectors and speaker test.
- Add manual wake/end test controls.
- Add supervised PulseAudio capture with automatic restart and error counters.
- Skip redundant firmware uploads when Kinect USB Audio is already enumerated.


## 0.1.1

- Fix Vosk on Raspberry Pi/aarch64 by adding the required libatomic runtime.
- Replace the stale Debian Kinect SDK MD5 gate with pinned SHA-256 verification of the current Microsoft-hosted SDK and extracted UAC firmware.
- Add USB, PulseAudio card/source/sink, and Vosk import diagnostics for first-hardware validation.


## 0.1.0

- Initial Raspberry Pi 5 / Home Assistant OS Kinect 360 satellite.
- Kinect 4-channel 16 kHz capture through Home Assistant PulseAudio.
- GCC-PHAT delay estimation and delay-and-sum beamforming.
- Local Vosk wake phrase detection compatible with the Android wake logic.
- Protocol v2 wake/end/audio_config and PCM uplink.
- 16 kHz PCM downlink playback.
- Half-duplex response suppression to avoid speaker-to-Kinect feedback during validation.
