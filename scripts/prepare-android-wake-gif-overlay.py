from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "WakeGifOverlayController wakeGifOverlay" not in source:
    marker = "    private OverlayController overlay;"
    if marker not in source:
        raise RuntimeError("RemoteService overlay field marker not found")
    source = source.replace(marker, marker + "\n    private WakeGifOverlayController wakeGifOverlay;", 1)

if "wakeGifOverlay = new WakeGifOverlayController(this);" not in source:
    marker = '''        overlay = new OverlayController(this, new Runnable() {
            @Override public void run() { sendEndEvent("overlay_tap"); }
        });'''
    if marker not in source:
        raise RuntimeError("RemoteService overlay init marker not found")
    source = source.replace(marker, marker + "\n        wakeGifOverlay = new WakeGifOverlayController(this);", 1)

if "wakeGifOverlay.showWake();" not in source:
    marker = '''        if (sent && "voice".equals(source)) {
            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);'''
    if marker not in source:
        raise RuntimeError("RemoteService voice wake cue marker not found")
    replacement = marker + '''
            if (wakeGifOverlay != null) wakeGifOverlay.showWake();'''
    source = source.replace(marker, replacement, 1)

if "wakeGifOverlay.onServerState(serverState);" not in source:
    marker = "        serverState = next;"
    if marker not in source:
        raise RuntimeError("RemoteService authoritative state assignment marker not found")
    source = source.replace(marker, marker + "\n        if (wakeGifOverlay != null) wakeGifOverlay.onServerState(serverState);", 1)

if "wakeGifOverlay.destroy();" not in source:
    marker = "        if (overlay != null) overlay.destroy();"
    if marker not in source:
        raise RuntimeError("RemoteService overlay destroy marker not found")
    source = source.replace(marker, marker + "\n        if (wakeGifOverlay != null) wakeGifOverlay.destroy();", 1)

required = [
    "WakeGifOverlayController wakeGifOverlay",
    "wakeGifOverlay = new WakeGifOverlayController(this)",
    "wakeGifOverlay.showWake()",
    "wakeGifOverlay.onServerState(serverState)",
    "wakeGifOverlay.destroy()",
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Wake GIF transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android fullscreen wake GIF overlay with state-driven fade-out.")
