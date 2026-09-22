#!/usr/bin/env bash
set -euo pipefail

CONFIG=/data/options.json
FW_DIR=/data/kinect-firmware
MODEL_DIR=/data/vosk-model-small-es-0.42
MODEL_ZIP=/data/vosk-model-small-es-0.42.zip
MODEL_URL=https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip

SDK_URL=https://download.microsoft.com/download/F/9/9/F99791F2-D5BE-478A-B77A-830AD14950C3/KinectSDK-v1.0-beta2-x86.msi
SDK_SHA256=817764591cff7acc3d678c5bc65dc8724b3d243611c1010dab2c18d0dedd4221
FW_SHA256=efd1dd2fd2610f69d07af7aeff56b26551ac6ec4251159c50027769545b6f7a5

mkdir -p "$FW_DIR"

json_bool() {
  python3 - "$1" <<'PY'
import json,sys
with open('/data/options.json', encoding='utf-8') as f: o=json.load(f)
v=o.get(sys.argv[1], True)
print('true' if bool(v) else 'false')
PY
}

fetch_kinect_firmware() {
  local tmp msi fw
  tmp="$(mktemp -d)"
  msi="$tmp/KinectSDK-v1.0-beta2-x86.msi"

  echo "[kinect] Downloading Microsoft Kinect SDK firmware source..."
  wget -q --show-progress -O "$msi" "$SDK_URL"

  echo "[kinect] Verifying SDK SHA-256..."
  echo "$SDK_SHA256  $msi" | sha256sum -c -

  echo "[kinect] Extracting UAC firmware..."
  (
    cd "$tmp"
    7z e -y -r "$msi" "UACFirmware.*" >/dev/null
  )
  fw="$(find "$tmp" -maxdepth 1 -type f -name 'UACFirmware.*' -print -quit)"
  if [[ -z "$fw" ]]; then
    echo "[kinect] ERROR: UACFirmware was not found in the verified SDK archive." >&2
    rm -rf "$tmp"
    return 1
  fi

  echo "[kinect] Verifying extracted UAC firmware SHA-256..."
  echo "$FW_SHA256  $fw" | sha256sum -c -
  if [[ "$(stat -c '%s' "$fw")" != "132096" ]]; then
    echo "[kinect] ERROR: unexpected UACFirmware size." >&2
    rm -rf "$tmp"
    return 1
  fi

  install -m 0644 "$fw" "$FW_DIR/UACFirmware"
  rm -rf "$tmp"
  echo "[kinect] Verified UACFirmware installed in persistent storage."
}

echo "[kinect] USB before firmware upload:"
lsusb || true

if [[ "$(json_bool auto_fetch_firmware)" == "true" && ! -f "$FW_DIR/UACFirmware" ]]; then
  if ! fetch_kinect_firmware; then
    echo "[kinect] Firmware fetch/verification failed."
  fi
fi

if [[ -f "$FW_DIR/UACFirmware" ]]; then
  if echo "$FW_SHA256  $FW_DIR/UACFirmware" | sha256sum -c -; then
    echo "[kinect] Uploading temporary audio firmware if a pre-firmware Kinect is present..."
    kinect_upload_fw "$FW_DIR/UACFirmware" || true
    sleep 4
  else
    echo "[kinect] WARNING: stored UACFirmware failed verification; removing it for a clean retry."
    rm -f "$FW_DIR/UACFirmware"
  fi
else
  echo "[kinect] WARNING: $FW_DIR/UACFirmware missing; Kinect microphone array may not enumerate."
fi

echo "[kinect] USB after firmware upload:"
lsusb || true

if [[ ! -d "$MODEL_DIR" ]]; then
  echo "[vosk] Downloading Spanish small model (first run only)..."
  rm -f "$MODEL_ZIP"
  wget -O "$MODEL_ZIP" "$MODEL_URL"
  unzip -q "$MODEL_ZIP" -d /data
  rm -f "$MODEL_ZIP"
fi

echo "[vosk] Verifying native library dependencies..."
python3 - <<'PY'
import vosk
print("[vosk] import OK:", vosk.__file__)
PY

echo "[audio] PULSE_SERVER=\${PULSE_SERVER:-<unset>}"
echo "[audio] PulseAudio server:"
pactl info || true
echo "[audio] PulseAudio cards:"
pactl list short cards || true
echo "[audio] PulseAudio sources:"
pactl list short sources || true
echo "[audio] PulseAudio sinks:"
pactl list short sinks || true

exec python3 /app/satellite.py --config "$CONFIG"
