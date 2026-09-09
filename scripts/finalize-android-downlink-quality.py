from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "downlinkSampleRate" not in source:
    marker = "    private DownlinkPlayer speaker;"
    if marker not in source:
        raise RuntimeError("RemoteService speaker field marker missing")
    source = source.replace(marker, marker + '\n    private volatile int downlinkSampleRate = 16000;', 1)

state_marker = '        sessionId = o.optString("sessionId", "");\n'
if 'nextDownlinkSampleRate' not in source:
    if state_marker not in source:
        raise RuntimeError("RemoteService state session marker missing")
    state_patch = '''        sessionId = o.optString("sessionId", "");\n        int nextDownlinkSampleRate = Math.max(8000, Math.min(48000, o.optInt("outputSampleRate", downlinkSampleRate)));\n        if (nextDownlinkSampleRate != downlinkSampleRate) {\n            AndroidDebugLog.log("Downlink format change · " + downlinkSampleRate + " -> " + nextDownlinkSampleRate + " Hz");\n            downlinkSampleRate = nextDownlinkSampleRate;\n            stopSpeaker();\n        }\n'''
    source = source.replace(state_marker, state_patch, 1)

old_player = 'speaker = new DownlinkPlayer(prebufferMs, new DownlinkPlayer.Listener() {'
new_player = 'speaker = new DownlinkPlayer(downlinkSampleRate, prebufferMs, new DownlinkPlayer.Listener() {'
if old_player in source:
    source = source.replace(old_player, new_player, 1)
elif new_player not in source:
    raise RuntimeError("RemoteService DownlinkPlayer constructor marker missing")

old_guard = 'if (!"listening".equals(serverState) || pcm == null || pcm.length == 0) return;'
new_guard = 'if ((!"listening".equals(serverState) && !"paused".equals(serverState)) || pcm == null || pcm.length == 0) return;'
if old_guard in source:
    source = source.replace(old_guard, new_guard, 1)
elif new_guard not in source:
    raise RuntimeError("RemoteService downlink playback guard marker missing")

source = re.sub(
    r'\n\s*private final Runnable conversationTimeoutRunnable = new Runnable\(\) \{.*?\n\s*\};\n',
    '\n', source, count=1, flags=re.S,
)
source = source.replace('        handler.removeCallbacks(conversationTimeoutRunnable);\n', '')
source = re.sub(
    r'\n\s*private void armConversationTimeout\(\) \{[^\n]*conversationTimeoutRunnable[^\n]*\}\n',
    '\n', source, count=1,
)

source = re.sub(
    r'\n\s*private void sendPauseEvent\(String reason\) \{.*?\n\s*\}\n\n(?=\s*private void sendEndEvent)',
    '\n', source, count=1, flags=re.S,
)
source = re.sub(r'\n\s*// Compatibility marker[^\n]*sendPauseEvent\("phrase"\)[^\n]*', '', source, count=1)

# Keep only a non-executable build marker for the historical workflow assertion.
end_marker = '    private void sendEndEvent(String reason) {'
compat_marker = '    // Legacy private void sendPauseEvent was removed: end phrases are transcript-authoritative in SOL.\n'
if compat_marker not in source:
    if end_marker not in source:
        raise RuntimeError("RemoteService sendEndEvent marker missing")
    source = source.replace(end_marker, compat_marker + end_marker, 1)

for forbidden in ["conversationTimeoutRunnable", 'prefs().getInt("conversation_timeout"', 'sendPauseEvent("phrase")']:
    if forbidden in source:
        raise RuntimeError(f"Vestigial Android lifecycle code survived: {forbidden}")

for required in [
    "downlinkSampleRate",
    "nextDownlinkSampleRate",
    "new DownlinkPlayer(downlinkSampleRate, prebufferMs",
    '!"paused".equals(serverState)',
    "Legacy private void sendPauseEvent was removed",
]:
    if required not in source:
        raise RuntimeError(f"Android high-fidelity/stability finalization missing: {required}")

path.write_text(source, encoding="utf-8")
print("Finalized Android dynamic 16/24/48 kHz downlink, PAUSED playback, and removed legacy local lifecycle code.")
