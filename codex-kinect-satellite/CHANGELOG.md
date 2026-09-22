# Changelog

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
