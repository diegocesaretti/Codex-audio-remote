from pathlib import Path

path = Path(__file__).resolve().parents[1] / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

# Idempotent: CI/worktrees may invoke this more than once.
if "uplinkCueSessionId" in source and "AudioCuePlayer.Cue.UPLINK" in source:
    print("Android lifecycle audio cues already wired.")
    raise SystemExit(0)

old_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;'''
new_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;\n    private volatile String uplinkCueSessionId = "";\n    private volatile long uplinkCueMuteUntilMs;'''
if old_fields not in source:
    raise RuntimeError("RemoteService mic field marker not found")
source = source.replace(old_fields, new_fields, 1)

old_state = '''        if (!"listening".equals(serverState)) handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) armConversationTimeout();\n\n        reconcileAudioPolicy();'''
new_state = '''        if (!"listening".equals(serverState)) handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) {\n            uplinkCueSessionId = "";\n            uplinkCueMuteUntilMs = 0L;\n            armConversationTimeout();\n        }\n        if ("ending".equals(serverState) && "listening".equals(previous) && !previousSession.isEmpty()) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.END);\n            AndroidDebugLog.log("Session cue · conversation ending");\n        }\n        if ("idle".equals(serverState)) uplinkCueMuteUntilMs = 0L;\n\n        reconcileAudioPolicy();'''
if old_state not in source:
    raise RuntimeError("RemoteService authoritative state marker not found")
source = source.replace(old_state, new_state, 1)

old_wake = '''        boolean sent = sendText("{\\\"type\\\":\\\"event\\\",\\\"event\\\":\\\"wake\\\",\\\"source\\\":\\\"" + jsonEscape(source) + "\\\"}");\n        AndroidDebugLog.log("Wake v2 event sent=" + sent);'''
new_wake = '''        boolean sent = sendText("{\\\"type\\\":\\\"event\\\",\\\"event\\\":\\\"wake\\\",\\\"source\\\":\\\"" + jsonEscape(source) + "\\\"}");\n        if (sent && "voice".equals(source)) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);\n            AndroidDebugLog.log("Session cue · wake detected");\n        }\n        AndroidDebugLog.log("Wake v2 event sent=" + sent);'''
if old_wake not in source:
    raise RuntimeError("RemoteService wake send marker not found")
source = source.replace(old_wake, new_wake, 1)

old_send = '''                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        if (current == null || !current.send(ByteString.of(buffer, 0, read))) throw new IllegalStateException("binary send failed");\n                        offerPhraseAudio(buffer, read);'''
new_send = '''                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;\n                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        boolean audioSent = current != null && current.send(ByteString.of(buffer, 0, read));\n                        if (!audioSent) throw new IllegalStateException("binary send failed");\n                        if (!micSession.equals(uplinkCueSessionId)) {\n                            uplinkCueSessionId = micSession;\n                            uplinkCueMuteUntilMs = System.currentTimeMillis() + 220L;\n                            AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);\n                            AndroidDebugLog.log("Session cue · uplink confirmed · session=" + micSession);\n                        }\n                        offerPhraseAudio(buffer, read);'''
if old_send not in source:
    raise RuntimeError("RemoteService binary uplink marker not found")
source = source.replace(old_send, new_send, 1)

required = [
    "AudioCuePlayer.Cue.WAKE",
    "AudioCuePlayer.Cue.UPLINK",
    "AudioCuePlayer.Cue.END",
    "uplinkCueMuteUntilMs = System.currentTimeMillis() + 220L",
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Android audio cue transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android wake/uplink/end audio cues with uplink self-audio suppression.")
