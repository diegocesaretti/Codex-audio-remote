from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "ACTIVATION_PREROLL_MS" in source and "Activation pre-roll flush" in source:
    print("Android wake activation pre-roll already wired.")
    raise SystemExit(0)

# ArrayDeque keeps the rolling PCM pre-buffer cheap without introducing a dependency.
if "import java.util.ArrayDeque;" not in source:
    marker = "import java.util.ArrayList;"
    if marker not in source:
        raise RuntimeError("ArrayList import marker not found")
    source = source.replace(marker, "import java.util.ArrayDeque;\n" + marker, 1)

const_marker = "    private static final long MIC_HEARTBEAT_STALE_MS = 5000L;"
const_block = """    private static final long MIC_HEARTBEAT_STALE_MS = 5000L;\n    private static final long ACTIVATION_PREROLL_MS = 3000L;\n    private static final long ACTIVATION_PREROLL_MAX_WAIT_MS = 10000L;"""
if const_marker not in source:
    raise RuntimeError("Android mic heartbeat constant marker not found")
source = source.replace(const_marker, const_block, 1)

field_marker = """    private Thread micThread;\n    private AudioRecord micRecord;\n    private long lastMicAudioMs;\n    private Thread phraseThread;"""
field_block = """    private Thread micThread;\n    private AudioRecord micRecord;\n    private long lastMicAudioMs;\n    private Thread phraseThread;\n    private volatile boolean activationPreRollRequested;\n    private volatile long activationPreRollRequestedAtMs;"""
if field_marker not in source:
    raise RuntimeError("Android mic fields marker not found")
source = source.replace(field_marker, field_block, 1)

# Clear the local activation latch only on authoritative terminal transitions. LISTENING no longer
# needs the latch because the mic loop is then kept alive by the server state itself.
state_log = '''        AndroidDebugLog.log("STATE v2 " + previous + " -> " + serverState + " · rev=" + serverRevision + " · session=" + sessionId + " · reason=" + o.optString("reason", ""));\n\n        // Windows is authoritative for both timeouts.'''
state_log_new = '''        AndroidDebugLog.log("STATE v2 " + previous + " -> " + serverState + " · rev=" + serverRevision + " · session=" + sessionId + " · reason=" + o.optString("reason", ""));\n\n        if ("listening".equals(serverState)) activationPreRollRequested = false;\n        if ("ending".equals(serverState) || "disconnected".equals(serverState)) activationPreRollRequested = false;\n        if ("idle".equals(serverState) && ("activating".equals(previous) || "ending".equals(previous))) activationPreRollRequested = false;\n\n        // Windows is authoritative for both timeouts.'''
if state_log not in source:
    raise RuntimeError("Android authoritative state log marker not found")
source = source.replace(state_log, state_log_new, 1)

# DISCONNECTED must always discard any locally staged activation audio.
disconnected_marker = '''        if (!connected || "disconnected".equals(serverState)) {\n            stopWakeCapture("policy_disconnected");'''
disconnected_new = '''        if (!connected || "disconnected".equals(serverState)) {\n            activationPreRollRequested = false;\n            stopWakeCapture("policy_disconnected");'''
if disconnected_marker not in source:
    raise RuntimeError("Android disconnected policy marker not found")
source = source.replace(disconnected_marker, disconnected_new, 1)

# While the wake event is in flight, IDLE/PAUSED are allowed to stage conversation PCM locally.
idle_old = '''        if ("idle".equals(serverState)) {\n            stopConversationMic("policy_idle");\n            stopSpeaker();\n            stopResponseTranscriber();\n            if (overlay != null) overlay.hide();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            updateNotification(wakeRunning.get() ? "Conectado · wake escuchando" : "Conectado · preparando wake");\n            return;\n        }'''
idle_new = '''        if ("idle".equals(serverState)) {\n            if (activationPreRollRequested) {\n                long pendingAge = System.currentTimeMillis() - activationPreRollRequestedAtMs;\n                if (pendingAge <= ACTIVATION_PREROLL_MAX_WAIT_MS) {\n                    stopWakeCapture("policy_wake_preroll");\n                    stopSpeaker();\n                    stopResponseTranscriber();\n                    if (isThreadAlive(wakeThread)) {\n                        handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 35L);\n                        return;\n                    }\n                    startConversationMicIfReady();\n                    if (overlay != null) overlay.show("Activando…");\n                    updateNotification("Wake detectado · guardando audio…");\n                    return;\n                }\n                activationPreRollRequested = false;\n                AndroidDebugLog.log("Activation pre-roll expired before server activation · age=" + pendingAge + "ms");\n            }\n            stopConversationMic("policy_idle");\n            stopSpeaker();\n            stopResponseTranscriber();\n            if (overlay != null) overlay.hide();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            updateNotification(wakeRunning.get() ? "Conectado · wake escuchando" : "Conectado · preparando wake");\n            return;\n        }'''
if idle_old not in source:
    raise RuntimeError("Android idle policy marker not found")
source = source.replace(idle_old, idle_new, 1)

activating_old = '''        if ("activating".equals(serverState)) {\n            stopWakeCapture("policy_activating");\n            stopConversationMic("policy_activating");\n            stopSpeaker();\n            stopResponseTranscriber();\n            if (overlay != null) { overlay.clearTranscript(); overlay.show("Activando…"); }\n            updateNotification("Activando Codex…");\n            return;\n        }'''
activating_new = '''        if ("activating".equals(serverState)) {\n            stopWakeCapture("policy_activating");\n            stopSpeaker();\n            stopResponseTranscriber();\n            if (isThreadAlive(wakeThread)) {\n                handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 35L);\n                return;\n            }\n            startConversationMicIfReady();\n            if (overlay != null) { overlay.clearTranscript(); overlay.show("Activando…"); }\n            updateNotification(micRunning.get() ? "Activando Codex · audio en buffer" : "Activando Codex…");\n            return;\n        }'''
if activating_old not in source:
    raise RuntimeError("Android activating policy marker not found")
source = source.replace(activating_old, activating_new, 1)

paused_old = '''        if ("paused".equals(serverState)) {\n            stopConversationMic("policy_paused");\n            startSpeaker();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            if (overlay != null) overlay.show("En espera · decí Hola Sol");\n            updateNotification(wakeRunning.get() ? "Conversación en espera · wake escuchando" : "Conversación en espera · preparando wake");\n            return;\n        }'''
paused_new = '''        if ("paused".equals(serverState)) {\n            if (activationPreRollRequested) {\n                long pendingAge = System.currentTimeMillis() - activationPreRollRequestedAtMs;\n                if (pendingAge <= ACTIVATION_PREROLL_MAX_WAIT_MS) {\n                    stopWakeCapture("policy_resume_preroll");\n                    startSpeaker();\n                    if (isThreadAlive(wakeThread)) {\n                        handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 35L);\n                        return;\n                    }\n                    startConversationMicIfReady();\n                    if (overlay != null) overlay.show("Reanudando…");\n                    updateNotification("Wake detectado · guardando audio…");\n                    return;\n                }\n                activationPreRollRequested = false;\n            }\n            stopConversationMic("policy_paused");\n            startSpeaker();\n            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();\n            if (overlay != null) overlay.show("En espera · decí Hola Sol");\n            updateNotification(wakeRunning.get() ? "Conversación en espera · wake escuchando" : "Conversación en espera · preparando wake");\n            return;\n        }'''
if paused_old not in source:
    raise RuntimeError("Android paused policy marker not found")
source = source.replace(paused_old, paused_new, 1)

# Voice wake immediately relinquishes the wake recognizer and requests a local conversation capture.
wake_old = '''        if (sent && "voice".equals(source)) {\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);\n            AndroidDebugLog.log("Session cue · wake detected · state=" + serverState);\n        }'''
wake_new = '''        if (sent && "voice".equals(source)) {\n            activationPreRollRequested = true;\n            activationPreRollRequestedAtMs = System.currentTimeMillis();\n            stopWakeCapture("wake_detected_preroll");\n            handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 20L);\n            AudioCuePlayer.play(this, AudioCuePlayer.Cue.WAKE);\n            AndroidDebugLog.log("Session cue · wake detected · immediate PCM pre-roll armed · state=" + serverState);\n        }'''
if wake_old not in source:
    raise RuntimeError("Android wake cue marker not found")
source = source.replace(wake_old, wake_new, 1)

# Replace the conversation capture routine with one that can begin in IDLE/PAUSED immediately
# after a voice wake, hold up to three seconds locally, then flush it only after LISTENING arrives.
start = source.find("    private void startConversationMicIfReady() {")
end = source.find("    private synchronized void stopConversationMic(String reason) {", start)
if start < 0 or end < 0:
    raise RuntimeError("Android conversation mic method boundaries not found")

new_method = r'''    private void startConversationMicIfReady() {
        boolean preRollState = activationPreRollRequested &&
                (isWakeState(serverState) || "activating".equals(serverState));
        if (!connected || (!"listening".equals(serverState) && !preRollState) || destroyed) return;
        if (micRunning.get() || isThreadAlive(micThread)) return;
        if (isThreadAlive(wakeThread)) {
            handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 35L);
            return;
        }
        if (!micRunning.compareAndSet(false, true)) return;

        final int quality = prefs().getInt("audio_quality", 80);
        final int latency = prefs().getInt("audio_latency", 55);
        final boolean manualLatency = prefs().getBoolean("manual_latency", true);
        final int chunkMs = manualLatency ? Math.max(20, Math.min(120, prefs().getInt("manual_chunk_ms", 45))) : chunkMsForLatency(latency);
        final int requestedRate = sampleRateForQuality(quality);
        final int gainPct = Math.max(50, Math.min(400, prefs().getInt("mic_gain_pct", 100)));
        final int requestedSource = audioSourceForKey(prefs().getString("audio_source", "default"));
        final String enhancerMode = prefs().getString("voice_enhancer", VoiceEnhancer.OFF);
        final boolean nativeNs = prefs().getBoolean("native_ns", false);
        final boolean nativeAgc = prefs().getBoolean("native_agc", false);
        final boolean nativeAec = prefs().getBoolean("native_aec", false);
        lastMicAudioMs = System.currentTimeMillis();

        micThread = new Thread(new Runnable() {
            @Override public void run() {
                AudioRecord record = null;
                NativeAudioEffects effects = null;
                String activeMicSession = "";
                try {
                    int actualRate = requestedRate;
                    int actualSource = requestedSource;
                    int chunkBytes = actualRate * 2 * chunkMs / 1000;
                    int bufferBytes = Math.max(chunkBytes * 6, 4096);
                    record = createCapture(actualRate, bufferBytes, actualSource);
                    if (record == null && actualRate != 16000) { actualRate = 16000; chunkBytes = actualRate * 2 * chunkMs / 1000; record = createCapture(actualRate, Math.max(chunkBytes * 6, 4096), actualSource); }
                    if (record == null && actualSource != MediaRecorder.AudioSource.DEFAULT) { actualSource = MediaRecorder.AudioSource.DEFAULT; actualRate = requestedRate; chunkBytes = actualRate * 2 * chunkMs / 1000; record = createCapture(actualRate, Math.max(chunkBytes * 6, 4096), actualSource); }
                    if (record == null && actualSource != MediaRecorder.AudioSource.VOICE_RECOGNITION) { actualSource = MediaRecorder.AudioSource.VOICE_RECOGNITION; actualRate = 16000; chunkBytes = actualRate * 2 * chunkMs / 1000; record = createCapture(actualRate, Math.max(chunkBytes * 6, 4096), actualSource); }
                    if (record == null) throw new IllegalStateException("No compatible conversation AudioRecord");

                    micRecord = record;
                    effects = new NativeAudioEffects(record.getAudioSessionId(), nativeNs, nativeAgc, nativeAec);
                    VoiceEnhancer enhancer = new VoiceEnhancer(enhancerMode, actualRate);
                    final int finalRate = actualRate;
                    final int finalSource = actualSource;
                    final int finalChunkBytes = chunkBytes;
                    final int maxPreRollBytes = Math.max(finalChunkBytes, finalRate * 2 * (int)ACTIVATION_PREROLL_MS / 1000);
                    final ArrayDeque<byte[]> preRoll = new ArrayDeque<>();
                    int preRollBytes = 0;
                    boolean uplinkConfigured = false;

                    byte[] buffer = new byte[finalChunkBytes];
                    record.startRecording();
                    AndroidDebugLog.log("Conversation mic v2 EARLY START · " + finalRate + " Hz · source=" + finalSource + " · state=" + serverState + " · preRoll=" + ACTIVATION_PREROLL_MS + "ms");
                    broadcastStatus();

                    while (micRunning.get() && connected && !destroyed) {
                        boolean waitingForRealtime = activationPreRollRequested &&
                                (isWakeState(serverState) || "activating".equals(serverState));
                        boolean live = "listening".equals(serverState);
                        if (!waitingForRealtime && !live) break;

                        if (waitingForRealtime && System.currentTimeMillis() - activationPreRollRequestedAtMs > ACTIVATION_PREROLL_MAX_WAIT_MS) {
                            activationPreRollRequested = false;
                            AndroidDebugLog.log("Activation pre-roll capture timeout · returning to authoritative state=" + serverState);
                            break;
                        }

                        int read = record.read(buffer, 0, buffer.length);
                        if (read <= 0) continue;
                        lastMicAudioMs = System.currentTimeMillis();
                        applyGainPcm16InPlace(buffer, read, gainPct);
                        enhancer.processInPlace(buffer, read);

                        live = "listening".equals(serverState) && !sessionId.isEmpty();
                        if (!live) {
                            byte[] staged = Arrays.copyOf(buffer, read);
                            preRoll.addLast(staged);
                            preRollBytes += staged.length;
                            while (preRollBytes > maxPreRollBytes && preRoll.size() > 1) {
                                byte[] dropped = preRoll.removeFirst();
                                preRollBytes -= dropped.length;
                            }
                            continue;
                        }

                        String currentSession = sessionId;
                        if (!uplinkConfigured || !currentSession.equals(activeMicSession)) {
                            activeMicSession = currentSession;
                            String config = "{\"type\":\"audio_config\",\"sessionId\":\"" + jsonEscape(activeMicSession) + "\",\"sampleRate\":" + finalRate + ",\"channels\":1,\"chunkMs\":" + chunkMs + ",\"quality\":" + quality + ",\"latency\":" + latency + ",\"gainPct\":" + gainPct + ",\"capture\":\"" + jsonEscape(audioSourceName(finalSource)) + "\"}";
                            if (!sendText(config)) throw new IllegalStateException("audio_config send failed");
                            startPhraseDetector(finalRate, activeMicSession);
                            uplinkConfigured = true;

                            int stagedMs = finalRate <= 0 ? 0 : (preRollBytes * 1000 / (finalRate * 2));
                            AndroidDebugLog.log("Activation pre-roll flush · session=" + activeMicSession + " · buffered=" + stagedMs + "ms · bytes=" + preRollBytes);
                            WebSocket current;
                            while (!preRoll.isEmpty()) {
                                byte[] staged = preRoll.removeFirst();
                                synchronized (socketLock) { current = socket; }
                                if (current == null || !current.send(ByteString.of(staged))) throw new IllegalStateException("pre-roll binary send failed");
                                offerPhraseAudio(staged, staged.length);
                            }
                            preRollBytes = 0;
                        }

                        if (System.currentTimeMillis() < uplinkCueMuteUntilMs) continue;
                        WebSocket current;
                        synchronized (socketLock) { current = socket; }
                        boolean audioSent = current != null && current.send(ByteString.of(buffer, 0, read));
                        if (!audioSent) throw new IllegalStateException("binary send failed");
                        offerPhraseAudio(buffer, read);

                        if (!activeMicSession.equals(uplinkCueSessionId)) {
                            uplinkCueSessionId = activeMicSession;
                            int cueMuteMs = AudioCuePlayer.suggestedSuppressionMs(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                            uplinkCueMuteUntilMs = System.currentTimeMillis() + cueMuteMs;
                            AudioCuePlayer.play(RemoteService.this, AudioCuePlayer.Cue.UPLINK);
                            AndroidDebugLog.log("Session cue · uplink confirmed · suppress=" + cueMuteMs + "ms · session=" + activeMicSession);
                        }
                    }
                } catch (Exception e) {
                    if (micRunning.get()) AndroidDebugLog.log("Conversation mic v2 error: " + e);
                } finally {
                    if (effects != null) effects.close();
                    if (record != null) { try { record.stop(); } catch (Exception ignored) { } try { record.release(); } catch (Exception ignored) { } }
                    micRecord = null;
                    micRunning.set(false);
                    micThread = null;
                    phraseQueue.clear();
                    AndroidDebugLog.log("Conversation mic v2 RELEASED · session=" + activeMicSession + " · state=" + serverState);
                    handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); broadcastStatus(); } }, 100L);
                }
            }
        }, "MicV2");
        micThread.setPriority(Thread.MAX_PRIORITY);
        micThread.start();
    }

'''
source = source[:start] + new_method + source[end:]

required = [
    "ACTIVATION_PREROLL_MS = 3000L",
    "activationPreRollRequested",
    "wake_detected_preroll",
    "Conversation mic v2 EARLY START",
    "Activation pre-roll flush",
    "pre-roll binary send failed",
    'sendPauseEvent("phrase")',
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Android wake pre-roll transform missing: {needle}")

# The discarded GIF experiment must not be accidentally reintroduced into this build path.
if "wakeGifOverlay" in source:
    raise RuntimeError("Wake GIF overlay unexpectedly present before pre-roll build")

path.write_text(source, encoding="utf-8")
print("Prepared Android wake audio pre-roll: capture immediately after voice wake, buffer locally up to 3s, flush on LISTENING.")
