from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

# Android 6 / API23 audio routing is device-specific. On the target phone Android DEFAULT is the
# reliable wake source, while VOICE_RECOGNITION can initialize successfully yet deliver unusable
# audio. Prefer DEFAULT first and keep the other sources as fallbacks.
s = replace_once(
    s,
'''                record = createCapture(WAKE_SAMPLE_RATE, bufferBytes, MediaRecorder.AudioSource.VOICE_RECOGNITION);\n                int source = MediaRecorder.AudioSource.VOICE_RECOGNITION;\n                if (record == null) {\n                    source = MediaRecorder.AudioSource.MIC;\n                    record = createCapture(WAKE_SAMPLE_RATE, bufferBytes, source);\n                }\n                if (record == null) throw new IllegalStateException("No AudioRecord for legacy wake");''',
'''                int[] wakeSources = new int[] {\n                        MediaRecorder.AudioSource.DEFAULT,\n                        MediaRecorder.AudioSource.MIC,\n                        MediaRecorder.AudioSource.VOICE_RECOGNITION,\n                        MediaRecorder.AudioSource.CAMCORDER\n                };\n                int source = -1;\n                for (int candidate : wakeSources) {\n                    record = createCapture(WAKE_SAMPLE_RATE, bufferBytes, candidate);\n                    if (record != null) { source = candidate; break; }\n                }\n                if (record == null) throw new IllegalStateException("No AudioRecord for legacy wake");''',
    'legacy wake source order')

# Log actual input level on API23. This makes silent-source failures immediately visible in logs.
s = replace_once(
    s,
'''                while (legacyWakeRunning.get() && connected && !streaming.get() && !destroyed) {\n                    int read = record.read(buffer, 0, buffer.length);\n                    if (read <= 0) continue;\n                    boolean finalResult = recognizer.acceptWaveForm(buffer, read);\n                    String json = finalResult ? recognizer.getResult() : recognizer.getPartialResult();\n                    checkWake(json);\n                }''',
'''                long lastLevelLogMs = 0;\n                while (legacyWakeRunning.get() && connected && !streaming.get() && !destroyed) {\n                    int read = record.read(buffer, 0, buffer.length);\n                    if (read <= 0) continue;\n                    long nowMs = System.currentTimeMillis();\n                    if (nowMs - lastLevelLogMs >= 3000) {\n                        long sum = 0; int peak = 0; int samples = 0;\n                        for (int i = 0; i + 1 < read; i += 2) {\n                            int sample = (short)((buffer[i] & 0xff) | (buffer[i + 1] << 8));\n                            int abs = Math.abs(sample);\n                            if (abs > peak) peak = abs;\n                            sum += (long)sample * sample; samples++;\n                        }\n                        int rms = samples > 0 ? (int)Math.sqrt(sum / (double)samples) : 0;\n                        AndroidDebugLog.log("Legacy wake audio · source=" + source + " · read=" + read + " · rms=" + rms + " · peak=" + peak);\n                        lastLevelLogMs = nowMs;\n                    }\n                    boolean finalResult = recognizer.acceptWaveForm(buffer, read);\n                    String json = finalResult ? recognizer.getResult() : recognizer.getPartialResult();\n                    checkWake(json);\n                }''',
    'legacy wake audio level logging')

# Vosk on the legacy path frequently emits a two-word wake phrase as two successive partials
# ("hola" then "sol"). Accept that sequence at every sensitivity instead of only >=75.
s = replace_once(
    s,
'''            if (!match && sensitivity >= 75 && parts.length == 2) {\n                if (text.equals(parts[0])) { lastPartial = text; lastPartialMs = now; }\n                else if (text.equals(parts[1]) && lastPartial.equals(parts[0]) && now - lastPartialMs < 1600) match = true;\n            }''',
'''            if (!match && parts.length == 2) {\n                if (text.equals(parts[0])) {\n                    lastPartial = text;\n                    lastPartialMs = now;\n                    AndroidDebugLog.log("Wake first word armed: " + text);\n                } else if (text.equals(parts[1]) && lastPartial.equals(parts[0]) && now - lastPartialMs < 2000) {\n                    match = true;\n                    AndroidDebugLog.log("Wake two-part sequence matched: " + parts[0] + " + " + parts[1]);\n                }\n            }''',
    'two-part wake sequence')

# Make API23 arming explicit in the notification/log so we can distinguish model load from capture.
s = replace_once(
    s,
'''                AndroidDebugLog.log("Legacy wake START · word=" + wakeWord() + " · source=" + source + " · buffer=" + bufferBytes);\n                updateNotification("Conectado · " + wakeWord() + " · wake API23");''',
'''                AndroidDebugLog.log("Legacy wake ARMED · word=" + wakeWord() + " · source=" + source + " · buffer=" + bufferBytes);\n                updateNotification("Conectado · wake API23 · " + wakeWord());''',
    'legacy wake armed log')

# The legacy wake AudioRecord and the conversation AudioRecord cannot safely overlap on many API23
# devices. Wait for the wake thread to finish its finally block (where it actually releases the
# recorder) before opening the uplink capture. This removes the intermittent "Codex hears silence"
# race immediately after a successful wake.
s = replace_once(
    s,
'''        audioThread = new Thread(() -> {\n            AudioRecord record = null;\n            NativeAudioEffects effects = null;\n            try {\n                int actualRate = requestedRate;''',
'''        audioThread = new Thread(() -> {\n            AudioRecord record = null;\n            NativeAudioEffects effects = null;\n            try {\n                if (Build.VERSION.SDK_INT <= 23) {\n                    Thread wakeThread = legacyWakeThread;\n                    if (wakeThread != null && wakeThread != Thread.currentThread()) {\n                        AndroidDebugLog.log("API23 uplink waiting for legacy wake AudioRecord release");\n                        try { wakeThread.join(900); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }\n                        if (wakeThread.isAlive()) {\n                            AndroidDebugLog.log("API23 wake release still pending after 900ms; adding guard delay");\n                            try { Thread.sleep(180); } catch (InterruptedException ignored) { Thread.currentThread().interrupt(); }\n                        } else {\n                            AndroidDebugLog.log("API23 legacy wake AudioRecord released; starting uplink");\n                        }\n                    }\n                }\n                int actualRate = requestedRate;''',
    'API23 wake to uplink handoff')

p.write_text(s, encoding='utf-8')
print('Android 6 legacy wake + mic handoff fix applied')
