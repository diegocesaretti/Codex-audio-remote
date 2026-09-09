from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "activationPreRollMuteUntilMs" in source and "playWakeRandom" in source:
    print("Wake greeting pre-roll suppression already wired.")
    raise SystemExit(0)

field = '    private volatile long activationPreRollRequestedAtMs;'
if field not in source:
    raise RuntimeError("Activation pre-roll field marker not found")
source = source.replace(field, field + '\n    private volatile long activationPreRollMuteUntilMs;', 1)

old_wake = '''            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);\n            AndroidDebugLog.log("Session cue · wake detected · immediate PCM pre-roll armed · state=" + serverState);'''
new_wake = '''            int wakeSuppressMs = AudioCuePlayer.playWakeRandom(this);\n            activationPreRollMuteUntilMs = System.currentTimeMillis() + wakeSuppressMs;\n            AndroidDebugLog.log("Session cue · wake detected · immediate PCM pre-roll armed · greeting-suppress=" + wakeSuppressMs + "ms · state=" + serverState);'''
if old_wake not in source:
    raise RuntimeError("Wake playback marker from pre-roll transform not found")
source = source.replace(old_wake, new_wake, 1)

old_process = '''                        applyGainPcm16InPlace(buffer, read, gainPct);\n                        enhancer.processInPlace(buffer, read);\n\n                        live = "listening".equals(serverState) && !sessionId.isEmpty();'''
new_process = '''                        applyGainPcm16InPlace(buffer, read, gainPct);\n                        enhancer.processInPlace(buffer, read);\n\n                        // Never feed the phone's own wake greeting into Codex. For the built-in\n                        // chime this is only ~200 ms; custom spoken greetings use their measured duration.\n                        if (System.currentTimeMillis() < activationPreRollMuteUntilMs) continue;\n\n                        live = "listening".equals(serverState) && !sessionId.isEmpty();'''
if old_process not in source:
    raise RuntimeError("Activation pre-roll PCM processing marker not found")
source = source.replace(old_process, new_process, 1)

required = [
    "activationPreRollMuteUntilMs",
    "AudioCuePlayer.playWakeRandom(this)",
    "greeting-suppress=",
    "Never feed the phone's own wake greeting into Codex",
]
for needle in required:
    if needle not in source:
        raise RuntimeError("Wake greeting suppression missing: " + needle)

path.write_text(source, encoding="utf-8")
print("Prepared randomized wake greetings with measured self-audio suppression during activation pre-roll.")
