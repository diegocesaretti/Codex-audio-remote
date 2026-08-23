from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "UPLINK_CAPTURE_GUARD_MS" in source and "Uplink boundary reset" in source:
    print("Android uplink listening boundary already fixed.")
    raise SystemExit(0)

# UPLINK is the authoritative handoff to live capture. A long custom wake greeting must never
# keep the microphone muted after Windows has already entered LISTENING.
const_marker = "    private static final long ACTIVATION_PREROLL_MAX_WAIT_MS = 10000L;"
if const_marker not in source:
    raise RuntimeError("Activation pre-roll constant marker not found")
source = source.replace(
    const_marker,
    const_marker + "\n    private static final long UPLINK_CAPTURE_GUARD_MS = 180L;",
    1,
)

old_suppression = '''                        // Never feed the phone's own wake greeting into Codex. For the built-in
                        // chime this is only ~200 ms; custom spoken greetings use their measured duration.
                        if (System.currentTimeMillis() < activationPreRollMuteUntilMs) continue;

                        live = "listening".equals(serverState) && !sessionId.isEmpty();'''
new_suppression = '''                        // Suppress the local wake greeting only while Realtime is still activating.
                        // LISTENING/UPLINK is a hard boundary: inherited wake suppression is cancelled
                        // immediately so a long greeting can never create a dead zone after the uplink cue.
                        boolean realtimeReadyNow = "listening".equals(serverState) && !sessionId.isEmpty();
                        if (!realtimeReadyNow && System.currentTimeMillis() < activationPreRollMuteUntilMs) continue;
                        if (realtimeReadyNow && activationPreRollMuteUntilMs != 0L) {
                            activationPreRollMuteUntilMs = 0L;
                            AndroidDebugLog.log("Uplink boundary reset · wake greeting suppression cancelled");
                        }

                        live = realtimeReadyNow;'''
if old_suppression not in source:
    raise RuntimeError("Wake greeting suppression block not found")
source = source.replace(old_suppression, new_suppression, 1)

# The pre-roll has just been drained here. Make the boundary explicit and start the uplink cue now.
old_boundary = '''                            preRollBytes = 0;
                        }

                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;'''
new_boundary = '''                            preRoll.clear();
                            preRollBytes = 0;

                            if (!activeMicSession.equals(uplinkCueSessionId)) {
                                uplinkCueSessionId = activeMicSession;
                                int requestedGuardMs = AudioCuePlayer.suggestedSuppressionMs(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                                int cueGuardMs = (int)Math.min(UPLINK_CAPTURE_GUARD_MS, Math.max(0, requestedGuardMs));
                                uplinkCueMuteUntilMs = System.currentTimeMillis() + cueGuardMs;
                                AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                                AndroidDebugLog.log("Session cue · uplink confirmed · buffer restarted · capture-guard=" + cueGuardMs + "ms · requested=" + requestedGuardMs + "ms · session=" + activeMicSession);
                            }
                        }

                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;'''
if old_boundary not in source:
    raise RuntimeError("Pre-roll/uplink boundary marker not found")
source = source.replace(old_boundary, new_boundary, 1)

# Remove the old post-send UPLINK trigger. It used the full cue duration and is what could create
# a ~2 second dead zone when a long local audio file was selected.
old_tail = '''                        offerPhraseAudio(buffer, read);

                        if (!activeMicSession.equals(uplinkCueSessionId)) {
                            uplinkCueSessionId = activeMicSession;
                            int cueMuteMs = AudioCuePlayer.suggestedSuppressionMs(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                            uplinkCueMuteUntilMs = System.currentTimeMillis() + cueMuteMs;
                            AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                            AndroidDebugLog.log("Session cue · uplink confirmed · suppress=" + cueMuteMs + "ms · session=" + activeMicSession);
                        }'''
new_tail = '''                        offerPhraseAudio(buffer, read);'''
if old_tail not in source:
    raise RuntimeError("Legacy post-send uplink suppression block not found")
source = source.replace(old_tail, new_tail, 1)

required = [
    "UPLINK_CAPTURE_GUARD_MS = 180L",
    "Uplink boundary reset",
    "buffer restarted",
    "capture-guard=",
    "requestedGuardMs",
]
for needle in required:
    if needle not in source:
        raise RuntimeError("Android uplink boundary fix missing: " + needle)

path.write_text(source, encoding="utf-8")
print("Fixed Android UPLINK boundary: cancel inherited wake mute, reset drained pre-roll, cap cue guard to 180 ms.")
