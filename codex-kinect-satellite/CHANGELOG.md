# Changelog

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
