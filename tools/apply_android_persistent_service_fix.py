from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

# Persistent service state / watchdog / wakelock.
s = replace_once(
    s,
'''    private long lastWakeMs;\n    private String lastPartial = "";''',
'''    private long lastWakeMs;\n    private PowerManager.WakeLock serviceWakeLock;\n    private Runnable wakeRearmRunnable;\n    private String lastPartial = "";''',
    'persistent service fields')

s = replace_once(
    s,
'''        startForeground(NOTIFICATION_ID, notification("Iniciando…"));\n        client = new OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).pingInterval(15, TimeUnit.SECONDS).build();''',
'''        startForeground(NOTIFICATION_ID, notification("Iniciando…"));\n        try {\n            PowerManager pm = (PowerManager)getSystemService(POWER_SERVICE);\n            if (pm != null) {\n                serviceWakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "codexremote:service");\n                serviceWakeLock.setReferenceCounted(false);\n                serviceWakeLock.acquire();\n                AndroidDebugLog.log("Persistent service wake-lock ACQUIRED");\n            }\n        } catch (Exception e) { AndroidDebugLog.log("Persistent wake-lock error: " + e); }\n        client = new OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).pingInterval(10, TimeUnit.SECONDS).build();''',
    'service wakelock')

# Start watchdog after receiver setup.
s = replace_once(
    s,
'''        wakeReceiverRegistered = true;\n        AndroidDebugLog.log("RemoteService created · API=" + Build.VERSION.SDK_INT);''',
'''        wakeReceiverRegistered = true;\n        handler.postDelayed(serviceWatchdogRunnable, 3000);\n        AndroidDebugLog.log("RemoteService created · API=" + Build.VERSION.SDK_INT + " · persistent watchdog armed");''',
    'watchdog start')

# Robust reconnect: once the authoritative socket dies, clear it so the reconnect path does not
# keep treating a dead WebSocket object as current.
s = replace_once(
    s,
'''                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false;\n                resetLocalAfterTransportLoss("closed:" + code);\n                scheduleReconnect();''',
'''                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false;\n                socket = null;\n                resetLocalAfterTransportLoss("closed:" + code);\n                scheduleReconnect();''',
    'closed clears current socket')

s = replace_once(
    s,
'''                AndroidDebugLog.log("WS failure: " + t);\n                connected = false;\n                resetLocalAfterTransportLoss("failure");''',
'''                AndroidDebugLog.log("WS failure: " + t);\n                connected = false;\n                socket = null;\n                resetLocalAfterTransportLoss("failure");''',
    'failure clears current socket')

# Faster reconnect, and keep retrying if a connect attempt never opens.
s = replace_once(
    s,
'''    private void scheduleReconnect() {\n        if (destroyed) return;\n        handler.removeCallbacks(reconnectRunnable);\n        handler.postDelayed(reconnectRunnable, 2500);\n    }\n    private final Runnable reconnectRunnable = () -> { if (!destroyed && !connected) connect(); };''',
'''    private void scheduleReconnect() {\n        if (destroyed) return;\n        handler.removeCallbacks(reconnectRunnable);\n        handler.postDelayed(reconnectRunnable, 1200);\n    }\n    private final Runnable reconnectRunnable = () -> {\n        if (destroyed || connected) return;\n        AndroidDebugLog.log("WS reconnect watchdog firing");\n        connect();\n        if (!connected) handler.postDelayed(reconnectRunnable, 4000);\n    };\n\n    private final Runnable serviceWatchdogRunnable = new Runnable() {\n        @Override public void run() {\n            if (destroyed) return;\n            try {\n                if (!connected || socket == null) {\n                    AndroidDebugLog.log("Service watchdog: transport disconnected -> reconnect");\n                    scheduleReconnect();\n                } else if (!conversationActive && !streaming.get() && voskModel != null) {\n                    boolean wakeArmed = Build.VERSION.SDK_INT <= 23 ? legacyWakeRunning.get() : speechService != null;\n                    if (!wakeArmed) {\n                        AndroidDebugLog.log("Service watchdog: connected but wake not armed -> rearm");\n                        scheduleWakeRearm("watchdog");\n                    }\n                }\n            } catch (Exception e) { AndroidDebugLog.log("Service watchdog error: " + e); }\n            handler.postDelayed(this, 4000);\n        }\n    };''',
    'reconnect and service watchdog')

# Do not immediately contend for AudioRecord after conversation mic shutdown. Wait until MicUplink
# has actually exited, with a bounded retry loop.
s = replace_once(
    s,
'''        gracefulEndPending = false;\n        conversationActive = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide(); startWakeRecognition();\n        updateNotification("Conectado · " + wakeWord());''',
'''        gracefulEndPending = false;\n        conversationActive = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide();\n        scheduleWakeRearm("session_finished");\n        updateNotification("Conectado · rearmando " + wakeWord());''',
    'delayed wake rearm after session')

# Add reusable rearm method before armConversationTimeout.
s = replace_once(
    s,
'''    private void armConversationTimeout() {''',
'''    private void scheduleWakeRearm(String reason) {\n        if (destroyed) return;\n        if (wakeRearmRunnable != null) handler.removeCallbacks(wakeRearmRunnable);\n        final long started = System.currentTimeMillis();\n        wakeRearmRunnable = new Runnable() {\n            @Override public void run() {\n                if (destroyed || !connected || conversationActive || streaming.get()) return;\n                Thread micThread = audioThread;\n                if (micThread != null && micThread.isAlive()) {\n                    long elapsed = System.currentTimeMillis() - started;\n                    if (elapsed < 2500) {\n                        AndroidDebugLog.log("Wake rearm waiting for MicUplink release · " + reason + " · " + elapsed + "ms");\n                        handler.postDelayed(this, 100);\n                        return;\n                    }\n                    AndroidDebugLog.log("Wake rearm timeout waiting for MicUplink; retrying capture anyway");\n                }\n                wakeRearmRunnable = null;\n                AndroidDebugLog.log("Wake rearm NOW · " + reason);\n                startWakeRecognition();\n            }\n        };\n        handler.postDelayed(wakeRearmRunnable, Build.VERSION.SDK_INT <= 23 ? 180 : 50);\n    }\n\n    private void armConversationTimeout() {''',
    'wake rearm helper')

# Legacy wake must self-retry if AudioRecord was temporarily unavailable/busy.
s = replace_once(
    s,
'''                legacyWakeRunning.set(false);\n                legacyWakeThread = null;\n                AndroidDebugLog.log("Legacy wake STOP");''',
'''                legacyWakeRunning.set(false);\n                legacyWakeThread = null;\n                AndroidDebugLog.log("Legacy wake STOP");\n                if (!destroyed && connected && !conversationActive && !streaming.get())\n                    handler.postDelayed(() -> scheduleWakeRearm("legacy_wake_stopped"), 500);''',
    'legacy wake self retry')

# Mark audioThread ended so the wake rearm can verify the actual AudioRecord lifecycle.
s = replace_once(
    s,
'''            finally {\n                if (effects != null) effects.close();\n                if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); }\n            }\n        }, "MicUplink");''',
'''            finally {\n                if (effects != null) effects.close();\n                if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); }\n                audioThread = null;\n                AndroidDebugLog.log("MicUplink AudioRecord RELEASED");\n                if (!destroyed && connected && !conversationActive && !streaming.get())\n                    handler.post(() -> scheduleWakeRearm("mic_released"));\n            }\n        }, "MicUplink");''',
    'mic release signal')

# Transport loss must actually stop the mic thread through the normal method when active; merely
# flipping streaming earlier prevented stopMicStreaming from doing any cleanup/signal work.
s = replace_once(
    s,
'''        conversationActive = false;\n        if (streaming.getAndSet(false)) phraseQueue.clear();\n        stopWakeRecognition();''',
'''        conversationActive = false;\n        if (streaming.get()) stopMicStreaming();\n        else phraseQueue.clear();\n        stopWakeRecognition();''',
    'transport loss mic stop')

# Release persistent lock cleanly.
s = replace_once(
    s,
'''        if (voskModel != null) voskModel.close();\n        AndroidDebugLog.log("RemoteService destroyed");''',
'''        if (voskModel != null) voskModel.close();\n        try {\n            if (serviceWakeLock != null && serviceWakeLock.isHeld()) serviceWakeLock.release();\n            AndroidDebugLog.log("Persistent service wake-lock RELEASED");\n        } catch (Exception ignored) { }\n        AndroidDebugLog.log("RemoteService destroyed");''',
    'release service wakelock')

p.write_text(s, encoding='utf-8')
print('Android persistent service / reconnect / wake rearm fix applied')
