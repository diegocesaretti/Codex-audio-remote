from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

if "private final VoskWakeGate wakeGate" in source and "checkWake(String json, boolean isFinal)" in source:
    print("Vosk live lab already wired.")
    raise SystemExit(0)

# Keep the authoritative latest lifecycle untouched; this patch changes only wake recognition/gating.
field_marker = '    private String lastWakeLogged = "";'
if field_marker not in source:
    raise RuntimeError("Wake field marker not found")
source = source.replace(field_marker, field_marker + '\n    private final VoskWakeGate wakeGate = new VoskWakeGate();', 1)

start = source.find("    private void startWakeCaptureIfReady() {")
stop = source.find("    private synchronized void stopWakeCapture(String reason) {", start)
if start < 0 or stop < 0:
    raise RuntimeError("Wake capture method boundaries not found")
wake = source[start:stop]

# SpeechService path (> API23): keep constrained grammar but expose word confidence in final results.
needle = 'Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());\n                speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE);'
replacement = 'Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());\n                recognizer.setWords(true);\n                speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE);'
if needle not in wake:
    raise RuntimeError("SpeechService recognizer anchor not found")
wake = wake.replace(needle, replacement, 1)

# Android 6/API23: unconstrained recognition avoids grammar hallucinations; the gate below validates it.
marker = 'if (!wakeRunning.compareAndSet(false, true)) return;'
pos = wake.find(marker)
if pos < 0:
    raise RuntimeError("Native wake path marker not found")
pre, native = wake[:pos], wake[pos:]
old_native_recognizer = 'recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());'
if old_native_recognizer not in native:
    raise RuntimeError("Native Vosk recognizer anchor not found")
native = native.replace(old_native_recognizer,
    '// Android 6: free recognition + explicit post-recognition gate.\n                    recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE);\n                    recognizer.setWords(true);', 1)

old_audio = '''                        lastWakeAudioMs = System.currentTimeMillis();
                        lastWakeRms = pcmRms(buffer, read);
                        boolean complete = recognizer.acceptWaveForm(buffer, read);
                        checkWake(complete ? recognizer.getResult() : recognizer.getPartialResult());'''
new_audio = '''                        lastWakeAudioMs = System.currentTimeMillis();
                        lastWakeRms = pcmRms(buffer, read);
                        wakeGate.observeAudio(lastWakeRms, lastWakeAudioMs);
                        boolean complete = recognizer.acceptWaveForm(buffer, read);
                        checkWake(complete ? recognizer.getResult() : recognizer.getPartialResult(), complete);'''
if old_audio not in native:
    raise RuntimeError("Native wake audio evaluation anchor not found")
native = native.replace(old_audio, new_audio, 1)
source = source[:start] + pre + native + source[stop:]

# Reset candidate/noise evidence whenever capture ownership changes.
stop_marker = '    private synchronized void stopWakeCapture(String reason) {\n'
if stop_marker not in source:
    raise RuntimeError("stopWakeCapture marker not found")
source = source.replace(stop_marker, stop_marker + '        wakeGate.reset();\n', 1)

# Replace the legacy fuzzy/two-token wake method without touching sendWakeEvent/preroll logic.
def replace_method(text: str, signature: str, replacement: str) -> str:
    start = text.find(signature)
    if start < 0:
        raise RuntimeError(f"Method not found: {signature}")
    brace = text.find('{', start)
    if brace < 0:
        raise RuntimeError(f"Opening brace not found: {signature}")
    depth = 0
    i = brace
    in_string = False
    escape = False
    while i < len(text):
        ch = text[i]
        if in_string:
            if escape:
                escape = False
            elif ch == '\\\\':
                escape = True
            elif ch == '"':
                in_string = False
        else:
            if ch == '"':
                in_string = True
            elif ch == '{':
                depth += 1
            elif ch == '}':
                depth -= 1
                if depth == 0:
                    return text[:start] + replacement + text[i + 1:]
        i += 1
    raise RuntimeError(f"Closing brace not found: {signature}")

new_check = r'''    private void checkWake(String json, boolean isFinal) {
        try {
            JSONObject o = new JSONObject(json);
            String text = normalize(o.optString("text", o.optString("partial", "")));
            if (text.isEmpty()) {
                if (isFinal) wakeGate.noteFinalSilence();
                return;
            }
            if (!text.equals(lastWakeLogged)) {
                lastWakeLogged = text;
                AndroidDebugLog.log("Wake v2 heard: " + text + (isFinal ? " · final" : " · partial"));
            }

            int sensitivity = Math.max(0, Math.min(100, prefs().getInt("sensitivity", 60)));
            VoskWakeGate.Decision decision = wakeGate.evaluate(
                    text, wakeWord(), sensitivity, isFinal,
                    wakeResultConfidence(o), wakeResultDurationMs(o), Build.VERSION.SDK_INT <= 23);
            if (decision.accepted) {
                AndroidDebugLog.log("Wake v2 CONFIRMED · " + decision.reason);
                handler.post(new Runnable() { @Override public void run() { sendWakeEvent("voice"); } });
            } else if (decision.loggable) {
                AndroidDebugLog.log("Wake v2 rejected · " + decision.reason);
            }
        } catch (Exception e) { AndroidDebugLog.log("Wake v2 parse error: " + e); }
    }

    private static double wakeResultConfidence(JSONObject o) {
        JSONArray words = o.optJSONArray("result");
        if (words == null || words.length() == 0) return -1.0;
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < words.length(); i++) {
            JSONObject w = words.optJSONObject(i);
            if (w != null && w.has("conf")) { sum += w.optDouble("conf", 0.0); count++; }
        }
        return count == 0 ? -1.0 : sum / count;
    }

    private static long wakeResultDurationMs(JSONObject o) {
        JSONArray words = o.optJSONArray("result");
        if (words == null || words.length() == 0) return -1L;
        JSONObject first = words.optJSONObject(0);
        JSONObject last = words.optJSONObject(words.length() - 1);
        if (first == null || last == null || !first.has("start") || !last.has("end")) return -1L;
        double seconds = last.optDouble("end", 0.0) - first.optDouble("start", 0.0);
        return seconds <= 0 ? -1L : Math.round(seconds * 1000.0);
    }'''
source = replace_method(source, "    private void checkWake(String json) {", new_check)

# SpeechService callbacks carry their own partial/final semantics.
listener_replacements = {
    '@Override public void onPartialResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }':
        '@Override public void onPartialResult(String hypothesis) { checkWake(hypothesis, false); lastWakeAudioMs = System.currentTimeMillis(); }',
    '@Override public void onResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }':
        '@Override public void onResult(String hypothesis) { checkWake(hypothesis, true); lastWakeAudioMs = System.currentTimeMillis(); }',
    '@Override public void onFinalResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }':
        '@Override public void onFinalResult(String hypothesis) { checkWake(hypothesis, true); lastWakeAudioMs = System.currentTimeMillis(); }',
}
for old, new in listener_replacements.items():
    if old not in source:
        raise RuntimeError("Vosk listener anchor not found: " + old[:45])
    source = source.replace(old, new, 1)

required = [
    'private final VoskWakeGate wakeGate = new VoskWakeGate()',
    'wakeGate.observeAudio(lastWakeRms, lastWakeAudioMs)',
    'new Recognizer(voskModel, WAKE_SAMPLE_RATE);',
    'checkWake(String json, boolean isFinal)',
    'wakeResultConfidence',
    'wakeResultDurationMs',
    'checkWake(hypothesis, false)',
    'checkWake(hypothesis, true)',
]
for needle in required:
    if needle not in source:
        raise RuntimeError("Vosk live lab transform missing: " + needle)

path.write_text(source, encoding="utf-8")
print("Prepared Vosk live laboratory on latest authoritative Android lifecycle.")
