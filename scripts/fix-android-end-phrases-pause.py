from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "android" / "app" / "src" / "main" / "java" / "com" / "bwa3d" / "codexremote" / "RemoteService.java"
source = path.read_text(encoding="utf-8")

old_call = 'handler.post(new Runnable() { @Override public void run() { sendEndEvent("phrase"); } }); break;'
new_call = 'handler.post(new Runnable() { @Override public void run() { sendPauseEvent("phrase"); } }); break;'
if old_call in source:
    source = source.replace(old_call, new_call, 1)
elif 'sendPauseEvent("phrase")' not in source:
    raise RuntimeError("Android end-phrase handler marker not found")

if 'private void sendPauseEvent(String reason)' not in source:
    marker = '    private void sendEndEvent(String reason) {'
    if marker not in source:
        raise RuntimeError("Android sendEndEvent marker not found")
    method = '''    private void sendPauseEvent(String reason) {\n        if (!connected || !"listening".equals(serverState)) return;\n        String payload = "{\\\"type\\\":\\\"event\\\",\\\"event\\\":\\\"pause\\\",\\\"reason\\\":\\\"" + jsonEscape(reason) + "\\\",\\\"sessionId\\\":\\\"" + jsonEscape(sessionId) + "\\\"}";\n        boolean sent = sendText(payload);\n        AndroidDebugLog.log("Pause v2 event · reason=" + reason + " · sent=" + sent);\n    }\n\n'''
    source = source.replace(marker, method + marker, 1)

required = [
    'sendPauseEvent("phrase")',
    '"event\\\":\\\"pause',
    'Pause v2 event',
]
for needle in required:
    if needle not in source:
        raise RuntimeError(f"Android end-phrase pause transform missing: {needle}")

path.write_text(source, encoding="utf-8")
print("Prepared Android end phrases: pause microphone, preserve Realtime conversation until Windows conversation timeout.")
