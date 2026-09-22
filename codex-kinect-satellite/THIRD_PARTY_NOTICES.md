# Third-party notes

The real-time Kinect beamforming implementation in `satellite.py` is an independent adaptation of the GCC-PHAT + delay-and-sum approach used by **plateforme/bulle**:

- Project: https://github.com/plateforme/bulle
- Copyright (c) 2026 Gregory Fabre
- License: MIT

The Microsoft Kinect USB Audio Class firmware is **not included** in this repository or image. The add-on uses Debian's `kinect-audio-setup` helper to fetch it locally at runtime under the upstream terms.

Vosk is Apache-2.0 licensed. The default `vosk-model-small-es-0.42` model is listed by Vosk as Apache-2.0.
