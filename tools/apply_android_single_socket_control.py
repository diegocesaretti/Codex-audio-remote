from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

# RemoteService: all UI/control events must travel over the one persistent WebSocket.
p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

s = replace_once(
    s,
'''    public static final String ACTION_START = "com.bwa3d.codexremote.START";\n    public static final String ACTION_WAKE = "com.bwa3d.codexremote.WAKE";''',
'''    public static final String ACTION_START = "com.bwa3d.codexremote.START";\n    public static final String ACTION_WAKE = "com.bwa3d.codexremote.WAKE";\n    public static final String ACTION_OVERLAY_END = "com.bwa3d.codexremote.OVERLAY_END";''',
    'overlay end action')

s = replace_once(
    s,
'''        if (ACTION_START.equals(action)) {\n            SharedPreferences p = prefs();\n            serverIp = intent.getStringExtra("ip");\n            serverPort = intent.getIntExtra("port", 8765);\n            if (serverIp == null || serverIp.trim().isEmpty()) serverIp = p.getString("ip", "192.168.1.100");\n            connect(); initVosk();\n        } else if (ACTION_WAKE.equals(action)) triggerWake();''',
'''        if (ACTION_START.equals(action)) {\n            SharedPreferences p = prefs();\n            String requestedIp = intent.getStringExtra("ip");\n            int requestedPort = intent.getIntExtra("port", 8765);\n            if (requestedIp == null || requestedIp.trim().isEmpty()) requestedIp = p.getString("ip", "192.168.1.100");\n            requestedIp = requestedIp.trim();\n\n            boolean sameTarget = requestedIp.equals(serverIp) && requestedPort == serverPort;\n            if (sameTarget && connected && socket != null) {\n                AndroidDebugLog.log("ACTION_START ignored · persistent socket already connected · ws://" + serverIp + ":" + serverPort + "/ws/");\n                initVosk();\n                if (!conversationActive && !activationPending && !streaming.get()) scheduleWakeRearm("duplicate_start");\n                return START_STICKY;\n            }\n            if (sameTarget && !connected && socket != null) {\n                AndroidDebugLog.log("ACTION_START ignored · persistent socket connection already in progress");\n                return START_STICKY;\n            }\n\n            serverIp = requestedIp;\n            serverPort = requestedPort;\n            connect(); initVosk();\n        } else if (ACTION_OVERLAY_END.equals(action)) {\n            AndroidDebugLog.log("Fallback overlay end routed through persistent RemoteService socket");\n            requestEndSession("overlay_tap");\n        } else if (ACTION_WAKE.equals(action)) triggerWake();''',
    'idempotent start and overlay service action')

p.write_text(s, encoding='utf-8')

# Android 6 fallback overlay: never create a second WebSocket. A transient control socket becomes
# a new owner on the Windows single-client server and silently strands the real RemoteService socket.
p = Path('android/app/src/main/java/com/bwa3d/codexremote/OverlayFallbackActivity.java')
s = p.read_text(encoding='utf-8')
start = s.find('    private void endSessionDirectly() {')
if start < 0:
    raise RuntimeError('Patch anchor not found: fallback end method start')
end_marker = '\n    }\n}'
end = s.find(end_marker, start)
if end < 0:
    raise RuntimeError('Patch anchor not found: fallback end method end')
replacement = '''    private void endSessionDirectly() {\n        if (ending) return;\n        ending = true;\n        AndroidDebugLog.log("Fallback overlay tapped · routing end through persistent RemoteService socket");\n\n        Intent i = new Intent(this, RemoteService.class);\n        i.setAction(RemoteService.ACTION_OVERLAY_END);\n        try {\n            if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);\n            finish();\n        } catch (Exception e) {\n            AndroidDebugLog.log("Fallback service end failed: " + e);\n            ending = false;\n        }\n    }'''
s = s[:start] + replacement + s[end + len('\n    }'):]
p.write_text(s, encoding='utf-8')

print('Android single persistent socket control patch applied')
