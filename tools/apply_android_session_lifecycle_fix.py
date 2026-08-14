from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

s = replace_once(
    s,
'''    private boolean endingSession;\n    private boolean gracefulEndPending;\n    private Runnable pendingPcEndRunnable;\n    private boolean wakeReceiverRegistered;''',
'''    private boolean endingSession;\n    private boolean gracefulEndPending;\n    private Runnable pendingPcEndRunnable;\n    private boolean conversationActive;\n    private boolean wakeReceiverRegistered;''',
    'conversation active field')

# Replacing a WebSocket while a previous local session is alive must first drop all local audio/UI
# state. Otherwise Android may reconnect with streaming=true and a stale overlay, whose next tap is
# immediately sent to the brand-new server session as end_session.
s = replace_once(
    s,
'''    private synchronized void connect() {\n        if (destroyed || serverIp == null) return;\n        if (socket != null) try { socket.cancel(); } catch (Exception ignored) { }\n        connected = false;''',
'''    private synchronized void connect() {\n        if (destroyed || serverIp == null) return;\n        if (socket != null) {\n            resetLocalAfterTransportLoss("socket_replace");\n            try { socket.cancel(); } catch (Exception ignored) { }\n        }\n        connected = false;''',
    'reset before socket replace')

s = replace_once(
    s,
'''            @Override public void onOpen(WebSocket webSocket, Response response) {\n                connected = true; socket = webSocket;\n                AndroidDebugLog.log("WS open");\n                sendText("{\\\"type\\\":\\\"hello\\\",\\\"name\\\":\\\"Android satellite\\\"}");\n                updateNotification(voskModel != null ? "Conectado · " + wakeWord() : "Conectado · preparando wake");\n                handler.post(RemoteService.this::startWakeRecognition);\n            }\n            @Override public void onMessage(WebSocket webSocket, String text) {\n                AndroidDebugLog.log("WS <- " + text);\n                handler.post(() -> handleServerMessage(text));\n            }\n            @Override public void onMessage(WebSocket webSocket, ByteString bytes) { playDownlink(bytes.toByteArray()); }\n            @Override public void onClosed(WebSocket webSocket, int code, String reason) {\n                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false; scheduleReconnect();\n            }\n            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {\n                AndroidDebugLog.log("WS failure: " + t);\n                connected = false; updateNotification("Sin conexión · reintentando…"); scheduleReconnect();\n            }''',
'''            @Override public void onOpen(WebSocket webSocket, Response response) {\n                if (webSocket != socket) {\n                    AndroidDebugLog.log("Ignoring stale WS onOpen");\n                    try { webSocket.close(1000, "superseded"); } catch (Exception ignored) { }\n                    return;\n                }\n                connected = true;\n                conversationActive = false;\n                AndroidDebugLog.log("WS open · current socket");\n                sendText("{\\\"type\\\":\\\"hello\\\",\\\"name\\\":\\\"Android satellite\\\"}");\n                updateNotification(voskModel != null ? "Conectado · " + wakeWord() : "Conectado · preparando wake");\n                long wakeDelay = Build.VERSION.SDK_INT <= 23 ? 350L : 0L;\n                handler.postDelayed(RemoteService.this::startWakeRecognition, wakeDelay);\n            }\n            @Override public void onMessage(WebSocket webSocket, String text) {\n                if (webSocket != socket) { AndroidDebugLog.log("Ignoring stale WS text"); return; }\n                AndroidDebugLog.log("WS <- " + text);\n                handler.post(() -> handleServerMessage(text));\n            }\n            @Override public void onMessage(WebSocket webSocket, ByteString bytes) {\n                if (webSocket != socket) return;\n                playDownlink(bytes.toByteArray());\n            }\n            @Override public void onClosed(WebSocket webSocket, int code, String reason) {\n                if (webSocket != socket) { AndroidDebugLog.log("Ignoring stale WS close"); return; }\n                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false;\n                resetLocalAfterTransportLoss("closed:" + code);\n                scheduleReconnect();\n            }\n            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {\n                if (webSocket != socket) { AndroidDebugLog.log("Ignoring stale WS failure: " + t); return; }\n                AndroidDebugLog.log("WS failure: " + t);\n                connected = false;\n                resetLocalAfterTransportLoss("failure");\n                updateNotification("Sin conexión · reintentando…");\n                scheduleReconnect();\n            }''',
    'websocket lifecycle ownership')

# Insert transport cleanup before reconnect scheduling.
s = replace_once(
    s,
'''    private void scheduleReconnect() {\n        if (destroyed) return;''',
'''    private void resetLocalAfterTransportLoss(String reason) {\n        AndroidDebugLog.log("Reset local session after transport loss · " + reason + " · streaming=" + streaming.get() + " · active=" + conversationActive);\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        cancelPendingPcEnd();\n        gracefulEndPending = false;\n        endingSession = false;\n        conversationActive = false;\n        if (streaming.getAndSet(false)) phraseQueue.clear();\n        stopWakeRecognition();\n        stopSpeaker();\n        stopResponseTranscriber();\n        if (overlay != null) overlay.hide();\n    }\n\n    private void scheduleReconnect() {\n        if (destroyed) return;''',
    'transport reset method')

# Mark the local conversation authoritative only when Codex actually enters listening.
s = replace_once(
    s,
'''                    endingSession = false; stopWakeRecognition(); stopResponseTranscriber(); startSpeaker(); startMicStreaming(); armConversationTimeout();\n                    overlay.show("Escuchando"); updateNotification("Codex escuchando"); break;''',
'''                    conversationActive = true;\n                    endingSession = false; stopWakeRecognition(); stopResponseTranscriber(); startSpeaker(); startMicStreaming(); armConversationTimeout();\n                    overlay.show("Escuchando"); updateNotification("Codex escuchando"); break;''',
    'conversation active on listening')

s = replace_once(
    s,
'''        gracefulEndPending = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide(); startWakeRecognition();''',
'''        gracefulEndPending = false;\n        conversationActive = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide(); startWakeRecognition();''',
    'conversation inactive on finish')

# Never let a stale overlay from a dead/previous session issue end_session against the fresh socket.
s = replace_once(
    s,
'''    private void requestGracefulEnd(String reason) {\n        if (!streaming.get() || endingSession || gracefulEndPending) return;''',
'''    private void requestGracefulEnd(String reason) {\n        if (!conversationActive) {\n            AndroidDebugLog.log("Ignoring stale overlay/end request outside active conversation: " + reason);\n            if (overlay != null) overlay.hide();\n            return;\n        }\n        if (!streaming.get() || endingSession || gracefulEndPending) return;''',
    'stale overlay guard')

p.write_text(s, encoding='utf-8')
print('Android WebSocket/session lifecycle fix applied')
