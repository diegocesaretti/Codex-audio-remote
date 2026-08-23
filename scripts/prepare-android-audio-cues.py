from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

# Idempotent: CI/worktrees may invoke this more than once.
if "uplinkCueSessionId" in source and "AudioCuePlayer.Cue.LISTEN_END" in source and "isWakeState(serverState)" in source:
    print("Android PAUSED lifecycle audio cues already wired.")
    raise SystemExit(0)

old_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;'''
new_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;\n    private volatile String uplinkCueSessionId = "";\n    private volatile long uplinkCueMuteUntilMs;'''
if old_fields not in source:
    raise RuntimeError("RemoteService mic field marker not found")
source = source.replace(old_fields, new_fields, 1)

# Wake heartbeat remains active both while fully idle and while a Realtime conversation is PAUSED.
source = source.replace(
    'if (connected && "idle".equals(serverState) && Build.VERSION.SDK_INT <= 23 && wakeRunning.get()) {',
    'if (connected && isWakeState(serverState) && Build.VERSION.SDK_INT <= 23 && wakeRunning.get()) {',
    1,
)

# Android must keep receiving assistant audio while the microphone is paused.
old_downlink = 'if (connected && "listening".equals(serverState)) playDownlink(bytes.toByteArray());'
new_downlink = 'if (connected && ("listening".equals(serverState) || "paused".equals(serverState))) playDownlink(bytes.toByteArray());'
if old_downlink not in source:
    raise RuntimeError("RemoteService binary downlink state marker not found")
source = source.replace(old_downlink, new_downlink, 1)

# PAUSED becomes an authoritative server state. Android no longer owns the conversation timeout.
old_valid = '''        if (!"idle".equals(next) && !"activating".equals(next) && !"listening".equals(next) && !"ending".equals(next)) {'''
new_valid = '''        if (!"idle".equals(next) && !"activating".equals(next) && !"listening".equals(next) && !"paused".equals(next) && !"ending".equals(next)) {'''
if old_valid not in source:
    raise RuntimeError("RemoteService valid state marker not found")
source = source.replace(old_valid, new_valid, 1)

old_state = '''        if (!"listening".equals(serverState)) handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) armConversationTimeout();\n\n        reconcileAudioPolicy();'''
new_state = '''        // Windows is authoritative for both timeouts. Android only follows LISTENING/PAUSED/ENDING.\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) {\n            uplinkCueSessionId = "";\n            uplinkCueMuteUntilMs = 0L;\n        }\n        if ("paused".equals(serverState) && "listening".equals(previous) && !previousSession.isEmpty()) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.LISTEN_END);\n            AndroidDebugLog.log("Session cue · listening ended / microphone paused");\n        }\n        if ("ending".equals(serverState) && ("listening".equals(previous) || "paused".equals(previous)) && !previousSession.isEmpty()) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.END);\n            AndroidDebugLog.log("Session cue · conversation ending");\n        }\n        if ("idle".equals(serverState)) uplinkCueMuteUntilMs = 0L;\n\n        reconcileAudioPolicy();'''
if old_state not in source:
    raise RuntimeError("RemoteService authoritative state marker not found")
source = source.replace(old_state, new_state, 1)

# In PAUSED we close only conversation mic, keep response speaker alive, and re-arm wake word.
old_policy_tail = '''        if ("listening".equals(serverState)) {\n            stopWakeCapture("policy_listening");\n            if (isThreadAlive(wakeThread)) {\n                handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); } }, 60L);\n                return;\n            }\n            startSpeaker();\n            startConversationMicIfReady();\n            if (overlay != null) overlay.show("Escuchando");\n            updateNotification(micRunning.get() ? "Codex escuchando · mic Android activo" : "Codex escuchando · abriendo mic…");\n            return;\n        }\n\n        stopWakeCapture("policy_ending");'''
new_policy_tail = '''        if ("listening".equals(serverState)) {\n            stopWakeCapture("policy_listening");\n            if (isThreadAlive(wakeThread)) {\n                handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); } }, 60L);\n                return;\n            }\n            startSpeaker();\n            startConversationMicIfReady();\n            if (overlay != null) overlay.show("Escuchando");\n            updateNotification(micRunning.get() ? "Codex escuchando · mic Android activo" : "Codex escuchando · abriendo mic…");\n            return;\n        }\n\n        if ("paused".equals(serverState)) {\n            stopConversationMic("policy_paused");\n            startSpeaker();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            if (overlay != null) overlay.show("En espera · decí Hola Sol");\n            updateNotification(wakeRunning.get() ? "Conversación en espera · wake escuchando" : "Conversación en espera · preparando wake");\n            return;\n        }\n\n        stopWakeCapture("policy_ending");'''
if old_policy_tail not in source:
    raise RuntimeError("RemoteService listening policy marker not found")
source = source.replace(old_policy_tail, new_policy_tail, 1)

# Wake recognizer is valid in idle and paused.
old_wake_ready = 'if (!connected || !"idle".equals(serverState) || voskModel == null || destroyed) return;'
new_wake_ready = 'if (!connected || !isWakeState(serverState) || voskModel == null || destroyed) return;'
if old_wake_ready not in source:
    raise RuntimeError("RemoteService wake readiness marker not found")
source = source.replace(old_wake_ready, new_wake_ready, 1)

old_wake_loop = 'while (wakeRunning.get() && connected && "idle".equals(serverState) && !destroyed) {'
new_wake_loop = 'while (wakeRunning.get() && connected && isWakeState(serverState) && !destroyed) {'
if old_wake_loop not in source:
    raise RuntimeError("RemoteService wake loop marker not found")
source = source.replace(old_wake_loop, new_wake_loop, 1)

old_wake = '''    private void sendWakeEvent(String source) {\n        if (!connected || !"idle".equals(serverState)) { AndroidDebugLog.log("Wake event ignored locally · connected=" + connected + " · state=" + serverState); return; }\n        wakeScreenIfEnabled();\n        boolean sent = sendText("{\\\"type\\\":\\\"event\\\",\\\"event\\\":\\\"wake\\\",\\\"source\\\":\\\"" + jsonEscape(source) + "\\\"}");\n        AndroidDebugLog.log("Wake v2 event sent=" + sent);\n    }'''
new_wake = '''    private void sendWakeEvent(String source) {\n        if (!connected || !isWakeState(serverState)) { AndroidDebugLog.log("Wake event ignored locally · connected=" + connected + " · state=" + serverState); return; }\n        wakeScreenIfEnabled();\n        boolean sent = sendText("{\\\"type\\\":\\\"event\\\",\\\"event\\\":\\\"wake\\\",\\\"source\\\":\\\"" + jsonEscape(source) + "\\\"}");\n        if (sent && "voice".equals(source)) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);\n            AndroidDebugLog.log("Session cue · wake detected · state=" + serverState);\n        }\n        AndroidDebugLog.log("Wake v2 event sent=" + sent);\n    }'''
if old_wake not in source:
    raise RuntimeError("RemoteService wake send marker not found")
source = source.replace(old_wake, new_wake, 1)

# Manual/end phrase may close the whole session from listening or paused.
old_end_guard = 'if (!"listening".equals(serverState) && !"activating".equals(serverState)) return;'
new_end_guard = 'if (!"listening".equals(serverState) && !"paused".equals(serverState) && !"activating".equals(serverState)) return;'
if old_end_guard not in source:
    raise RuntimeError("RemoteService end guard marker not found")
source = source.replace(old_end_guard, new_end_guard, 1)

# Uplink cue fires only after the first successful binary packet in each LISTENING phase. Custom cue
# duration determines how long capture is suppressed so its own sound cannot feed back into Realtime.
old_send = '''                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        if (current == null || !current.send(ByteString.of(buffer, 0, read))) throw new IllegalStateException("binary send failed");\n                        offerPhraseAudio(buffer, read);'''
new_send = '''                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;\n                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        boolean audioSent = current != null && current.send(ByteString.of(buffer, 0, read));\n                        if (!audioSent) throw new IllegalStateException("binary send failed");\n                        if (!micSession.equals(uplinkCueSessionId)) {\n                            uplinkCueSessionId = micSession;\n                            int cueMuteMs = AudioCuePlayer.suggestedSuppressionMs(RemoteService.this, AudioCuePlayer.Cue.UPLINK);\n                            uplinkCueMuteUntilMs = System.currentTimeMillis() + cueMuteMs;\n                            AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);\n                            AndroidDebugLog.log("Session cue · uplink confirmed · suppress=" + cueMuteMs + "ms · session=" + micSession);\n                        }\n                        offerPhraseAudio(buffer, read);'''
if old_send not in source:
    raise RuntimeError("RemoteService binary uplink marker not found")
source = source.replace(old_send, new_send, 1)

# Status panel should show wake healthy in both idle and paused.
old_status = 'boolean wakeHealthy = connected && "idle".equals(serverState) && wakeRunning.get()'
new_status = 'boolean wakeHealthy = connected && isWakeState(serverState) && wakeRunning.get()'
if old_status not in source:
    raise RuntimeError("RemoteService wake status marker not found")
source = source.replace(old_status, new_status, 1)

# Shared state helper used by health, policy and capture threads.
helper_marker = '    private String wakeWord() {'
helper = '''    private static boolean isWakeState(String state) { return "idle".equals(state) || "paused".equals(state); }\n'''
if helper_marker not in source:
    raise RuntimeError("RemoteService helper insertion marker not found")
source = source.replace(helper_marker, helper + helper_marker, 1)

required = [
    'AudioCuePlayer.Cue.WAKE',
    'AudioCuePlayer.Cue.UPLINK',
    'AudioCuePlayer.Cue.LISTEN_END',
    'AudioCuePlayer.Cue.END',
    '"paused".equals(serverState)',
    'isWakeState(serverState)',
    'AudioCuePlayer.suggestedSuppressionMs',
    'En espera · decí Hola Sol',
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Android PAUSED/cue transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android PAUSED lifecycle + wake/uplink/listen-end/conversation-end cues + custom cue suppression.")
