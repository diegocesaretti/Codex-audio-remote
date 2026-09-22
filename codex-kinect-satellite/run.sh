#!/usr/bin/env bash
set -euo pipefail

CONFIG=/data/options.json
FW_DIR=/data/kinect-firmware
MODEL_DIR=/data/vosk-model-small-es-0.42
MODEL_ZIP=/data/vosk-model-small-es-0.42.zip
MODEL_URL=https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip

mkdir -p "$FW_DIR"

json_bool() {
  python3 - "$1" <<'PY'
import json,sys
with open('/data/options.json', encoding='utf-8') as f: o=json.load(f)
v=o.get(sys.argv[1], True)
print('true' if bool(v) else 'false')
PY
}

if [[ "$(json_bool auto_fetch_firmware)" == "true" && ! -f "$FW_DIR/UACFirmware" ]]; then
  echo "[kinect] UAC firmware is not bundled. Fetching it locally using kinect_fetch_fw..."
  if ! kinect_fetch_fw "$FW_DIR"; then
    echo "[kinect] Firmware download failed. Check the add-on log and Microsoft source availability."
  fi
fi

if [[ -f "$FW_DIR/UACFirmware" ]]; then
  echo "[kinect] Uploading temporary audio firmware if a pre-firmware Kinect is present..."
  kinect_upload_fw "$FW_DIR/UACFirmware" || true
  sleep 4
else
  echo "[kinect] WARNING: $FW_DIR/UACFirmware missing; Kinect microphone array may not enumerate."
fi

if [[ ! -d "$MODEL_DIR" ]]; then
  echo "[vosk] Downloading Spanish small model (first run only)..."
  rm -f "$MODEL_ZIP"
  wget -O "$MODEL_ZIP" "$MODEL_URL"
  unzip -q "$MODEL_ZIP" -d /data
  rm -f "$MODEL_ZIP"
fi

echo "[audio] PulseAudio sources:"
pactl list short sources || true
echo "[audio] PulseAudio sinks:"
pactl list short sinks || true

exec python3 /app/satellite.py --config "$CONFIG"
