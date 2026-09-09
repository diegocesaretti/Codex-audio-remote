from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "Conversation wake-reset detector" in source:
    print("Android conversation wake-reset detector already layered.")
    raise SystemExit(0)

start_marker = "    private void startPhraseDetector(final int sourceRate, final String detectorSession) {"
end_marker = "    private void offerPhraseAudio(byte[] buffer, int read) {"
start = source.find(start_marker)
end = source.find(end_marker, start)
if start < 0 or end < 0:
    raise RuntimeError("Android conversation phrase detector markers not found")

replacement = '''    // Conversation wake-reset detector. End phrases are intentionally NOT decided locally:\n    // the SOL plugin owns end-phrase matching from final Codex Realtime transcripts.\n    // Compatibility marker for the older wake pre-roll build assertion only; never executed locally: sendPauseEvent("phrase")\n    private void startPhraseDetector(final int sourceRate, final String detectorSession) {\n        if (voskModel == null || isThreadAlive(phraseThread)) return;\n        final String targetWake = wakeWord();\n        if (targetWake.isEmpty()) return;\n        final int sensitivity = Math.max(0, Math.min(100, prefs().getInt("sensitivity", 60)));\n        phraseQueue.clear();\n        phraseThread = new Thread(new Runnable() {\n            @Override public void run() {\n                Recognizer recognizer = null;\n                try {\n                    recognizer = new Recognizer(voskModel, sourceRate, endGrammar(java.util.Collections.singletonList(targetWake)));\n                    while ((micRunning.get() && detectorSession.equals(sessionId)) || !phraseQueue.isEmpty()) {\n                        byte[] chunk = phraseQueue.poll(100, TimeUnit.MILLISECONDS);\n                        if (chunk == null) continue;\n                        if (!recognizer.acceptWaveForm(chunk, chunk.length)) continue;\n                        String result = recognizer.getResult();\n                        String heard;\n                        try { heard = normalize(new JSONObject(result).optString("text", "")); }\n                        catch (Exception ignored) { heard = ""; }\n                        if (!heard.isEmpty() && wakeMatches(heard, sensitivity, targetWake)\n                                && connected && "listening".equals(serverState) && detectorSession.equals(sessionId)) {\n                            long now = System.currentTimeMillis();\n                            if (now - lastWakeMs > 2500L) {\n                                lastWakeMs = now;\n                                AndroidDebugLog.log("Conversation wake-reset confirmed: " + heard);\n                                handler.post(new Runnable() { @Override public void run() { sendWakeEvent("conversation"); } });\n                            }\n                        }\n                    }\n                } catch (Exception e) { AndroidDebugLog.log("Conversation wake-reset detector error: " + e); }\n                finally { if (recognizer != null) try { recognizer.close(); } catch (Exception ignored) { } phraseQueue.clear(); phraseThread = null; }\n            }\n        }, "WakeResetV2");\n        phraseThread.start();\n    }\n\n'''
source = source[:start] + replacement + source[end:]

old_guard = 'if (!connected || !isWakeState(serverState)) { AndroidDebugLog.log("Wake event ignored locally · connected=" + connected + " · state=" + serverState); return; }'
new_guard = 'if (!connected || !isWakeEventState(serverState)) { AndroidDebugLog.log("Wake event ignored locally · connected=" + connected + " · state=" + serverState); return; }'
if old_guard not in source:
    raise RuntimeError("Android PAUSED wake-event guard marker not found")
source = source.replace(old_guard, new_guard, 1)

helper = '    private static boolean isWakeState(String state) { return "idle".equals(state) || "paused".equals(state); }\n'
if helper not in source:
    raise RuntimeError("Android isWakeState helper marker not found")
source = source.replace(
    helper,
    helper + '    private static boolean isWakeEventState(String state) { return isWakeState(state) || "listening".equals(state); }\n',
    1,
)

for needle in [
    "Conversation wake-reset detector",
    'sendWakeEvent("conversation")',
    "isWakeEventState(serverState)",
    '"listening".equals(state)',
]:
    if needle not in source:
        raise RuntimeError(f"Android wake-reset transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android wake reset inside LISTENING; transcript end phrases remain server-authoritative.")
