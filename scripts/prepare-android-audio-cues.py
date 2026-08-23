from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "uplinkCueSessionId" in source and "AudioCuePlayer.Cue.LISTEN_END" in source and "wakeArmNotBeforeMs" in source:
    print("Android PAUSED lifecycle audio cues already wired.")
    raise SystemExit(0)

old_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;'''
new_fields = '''    private long lastMicAudioMs;\n    private Thread phraseThread;\n    private volatile String uplinkCueSessionId = "";\n    private volatile long uplinkCueMuteUntilMs;\n    private volatile long wakeArmNotBeforeMs;'''
if old_fields not in source:
    raise RuntimeError("RemoteService mic field marker not found")
source = source.replace(old_fields, new_fields, 1)

source = source.replace(
    'if (connected && "idle".equals(serverState) && Build.VERSION.SDK_INT <= 23 && wakeRunning.get()) {',
    'if (connected && isWakeState(serverState) && Build.VERSION.SDK_INT <= 23 && wakeRunning.get()) {',
    1,
)

old_downlink = 'if (connected && "listening".equals(serverState)) playDownlink(bytes.toByteArray());'
new_downlink = 'if (connected && ("listening".equals(serverState) || "paused".equals(serverState))) playDownlink(bytes.toByteArray());'
if old_downlink not in source:
    raise RuntimeError("RemoteService binary downlink state marker not found")
source = source.replace(old_downlink, new_downlink, 1)

old_valid = '''        if (!"idle".equals(next) && !"activating".equals(next) && !"listening".equals(next) && !"ending".equals(next)) {'''
new_valid = '''        if (!"idle".equals(next) && !"activating".equals(next) && !"listening".equals(next) && !"paused".equals(next) && !"ending".equals(next)) {'''
if old_valid not in source:
    raise RuntimeError("RemoteService valid state marker not found")
source = source.replace(old_valid, new_valid, 1)

old_state = '''        if (!"listening".equals(serverState)) handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) armConversationTimeout();\n\n        reconcileAudioPolicy();'''
new_state = '''        // Windows is authoritative for both timeouts. Android only follows LISTENING/PAUSED/ENDING.\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) {\n            uplinkCueSessionId = "";\n            uplinkCueMuteUntilMs = 0L;\n            wakeArmNotBeforeMs = 0L;\n        }\n        if ("paused".equals(serverState) && "listening".equals(previous) && !previousSession.isEmpty()) {\n            int quietMs = AudioCuePlayer.suggestedSuppressionMs(this, AudioCuePlayer.Cue.LISTEN_END);\n            wakeArmNotBeforeMs = System.currentTimeMillis() + quietMs;\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.LISTEN_END);\n            AndroidDebugLog.log("Session cue · listening ended / microphone paused · wake-arm-delay=" + quietMs + "ms");\n        }\n        if ("ending".equals(serverState) && ("listening".equals(previous) || "paused".equals(previous)) && !previousSession.isEmpty()) {\n            int quietMs = AudioCuePlayer.suggestedSuppressionMs(this, AudioCuePlayer.Cue.END);\n            wakeArmNotBeforeMs = System.currentTimeMillis() + quietMs;\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.END);\n            AndroidDebugLog.log("Session cue · conversation ending · wake-arm-delay=" + quietMs + "ms");\n        }\n        if ("idle".equals(serverState)) uplinkCueMuteUntilMs = 0L;\n\n        reconcileAudioPolicy();'''
if old_state not in source:
    raise RuntimeError("RemoteService authoritative state marker not found")
source = source.replace(old_state, new_state, 1)

old_policy_tail = '''        if ("listening".equals(serverState)) {\n            stopWakeCapture("policy_listening");\n            if (isThreadAlive(wakeThread)) {\n                handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); } }, 60L);\n                return;\n            }\n            startSpeaker();\n            startConversationMicIfReady();\n            if (overlay != null) overlay.show("Escuchando");\n            updateNotification(micRunning.get() ? "Codex escuchando · mic Android activo" : "Codex escuchando · abriendo mic…");\n            return;\n        }\n\n        stopWakeCapture("policy_ending");'''
new_policy_tail = '''        if ("listening".equals(serverState)) {\n            stopWakeCapture("policy_listening");\n            if (isThreadAlive(wakeThread)) {\n                handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); } }, 60L);\n                return;\n            }\n            startSpeaker();\n            startConversationMicIfReady();\n            if (overlay != null) overlay.show("Escuchando");\n            updateNotification(micRunning.get() ? "Codex escuchando · mic Android activo" : "Codex escuchando · abriendo mic…");\n            return;\n        }\n\n        if ("paused".equals(serverState)) {\n            stopConversationMic("policy_paused");\n            startSpeaker();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            if (overlay != null) overlay.show("En espera · decí Hola Sol");\n            updateNotification(wakeRunning.get() ? "Conversación en espera · wake escuchando" : "Conversación en espera · preparando wake");\n            return;\n        }\n\n        stopWakeCapture("policy_ending");'''
if old_policy_tail not in source:
    raise RuntimeError("RemoteService listening policy marker not found")
source = source.replace(old_policy_tail, new_policy_tail, 1)

old_wake_ready = '''    private void startWakeCaptureIfReady() {\n        if (!connected || !"idle".equals(serverState) || voskModel == null || destroyed) return;'''
new_wake_ready = '''    private void startWakeCaptureIfReady() {\n        if (!connected || !isWakeState(serverState) || voskModel == null || destroyed) return;\n        long wakeDelay = wakeArmNotBeforeMs - System.currentTimeMillis();\n        if (wakeDelay > 0L) {\n            handler.postDelayed(RemoteService.this::reconcileAudioPolicy, Math.min(3000L, wakeDelay + 20L));\n            return;\n        }'''
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

old_end_guard = 'if (!"listening".equals(serverState) && !"activating".equals(serverState)) return;'
new_end_guard = 'if (!"listening".equals(serverState) && !"paused".equals(serverState) && !"activating".equals(serverState)) return;'
if old_end_guard not in source:
    raise RuntimeError("RemoteService end guard marker not found")
source = source.replace(old_end_guard, new_end_guard, 1)

old_send = '''                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        if (current == null || !current.send(ByteString.of(buffer, 0, read))) throw new IllegalStateException("binary send failed");\n                        offerPhraseAudio(buffer, read);'''
new_send = '''                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;\n                        WebSocket current;\n                        synchronized (socketLock) { current = socket; }\n                        boolean audioSent = current != null && current.send(ByteString.of(buffer, 0, read));\n                        if (!audioSent) throw new IllegalStateException("binary send failed");\n                        if (!micSession.equals(uplinkCueSessionId)) {\n                            uplinkCueSessionId = micSession;\n                            int cueMuteMs = AudioCuePlayer.suggestedSuppressionMs(RemoteService.this, AudioCuePlayer.Cue.UPLINK);\n                            uplinkCueMuteUntilMs = System.currentTimeMillis() + cueMuteMs;\n                            AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);\n                            AndroidDebugLog.log("Session cue · uplink confirmed · suppress=" + cueMuteMs + "ms · session=" + micSession);\n                        }\n                        offerPhraseAudio(buffer, read);'''
if old_send not in source:
    raise RuntimeError("RemoteService binary uplink marker not found")
source = source.replace(old_send, new_send, 1)

old_status = 'boolean wakeHealthy = connected && "idle".equals(serverState) && wakeRunning.get()'
new_status = 'boolean wakeHealthy = connected && isWakeState(serverState) && wakeRunning.get()'
if old_status not in source:
    raise RuntimeError("RemoteService wake status marker not found")
source = source.replace(old_status, new_status, 1)

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
    'wakeArmNotBeforeMs',
    'En espera · decí Hola Sol',
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Android PAUSED/cue transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android PAUSED lifecycle + four customizable cues + self-audio suppression.")
