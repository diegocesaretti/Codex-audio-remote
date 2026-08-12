from pathlib import Path


def replace_once(text, old, new, label):
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

# Android control
control = Path('android/app/src/main/java/com/bwa3d/codexremote/VoiceCloseDelayControl.java')
control.parent.mkdir(parents=True, exist_ok=True)
control.write_text('''package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

public class VoiceCloseDelayControl extends LinearLayout {
    private final TextView label;
    private final SeekBar seek;

    public VoiceCloseDelayControl(Context context) { this(context, null); }

    public VoiceCloseDelayControl(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        label = new TextView(context);
        seek = new SeekBar(context);
        seek.setMax(120);
        addView(label, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        addView(seek, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        SharedPreferences prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        int value = Math.max(0, Math.min(120, prefs.getInt("pc_voice_close_delay_s", 15)));
        seek.setProgress(value);
        updateLabel(value);
        seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                updateLabel(progress);
                if (fromUser) prefs.edit().putInt("pc_voice_close_delay_s", progress).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        });
    }

    private void updateLabel(int seconds) {
        String mode = seconds == 0 ? "inmediato" : seconds <= 10 ? "corto" : seconds <= 30 ? "recomendado" : "largo";
        label.setText("Demora para cerrar Voice en PC: " + seconds + " s · " + mode);
    }
}
''', encoding='utf-8')

# Android layout
p = Path('android/app/src/main/res/layout/activity_main.xml')
s = p.read_text(encoding='utf-8')
anchor = '        <TextView android:layout_width="match_parent" android:layout_height="wrap_content" android:text="0% = casi coincidencia perfecta (99% de confianza). 100% = mucho más tolerante. Si tenés falsos cierres, bajalo." />'
insert = anchor + '''\n        <com.bwa3d.codexremote.VoiceCloseDelayControl\n            android:layout_width="match_parent"\n            android:layout_height="wrap_content"\n            android:layout_marginTop="10dp" />\n        <TextView android:layout_width="match_parent" android:layout_height="wrap_content" android:text="Al terminar localmente, el micrófono se corta enseguida pero Voice en la PC queda vivo durante esta demora para permitir que Codex siga pensando y respondiendo." />'''
if 'VoiceCloseDelayControl' not in s:
    s = replace_once(s, anchor, insert, 'layout delay control')
p.write_text(s, encoding='utf-8')

# Android service
p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')
s = replace_once(s,
'''    private boolean endingSession;\n    private boolean wakeReceiverRegistered;''',
'''    private boolean endingSession;\n    private boolean gracefulEndPending;\n    private Runnable pendingPcEndRunnable;\n    private boolean wakeReceiverRegistered;''', 'android fields')
s = replace_once(s,
'''        overlay = new OverlayController(this, () -> requestEndSession("overlay_tap"));''',
'''        overlay = new OverlayController(this, () -> requestGracefulEnd("overlay_tap"));''', 'overlay graceful end')
s = replace_once(s,
'''                case "codex_listening":\n                    endingSession = false; stopWakeRecognition(); stopResponseTranscriber(); startSpeaker(); startMicStreaming(); armConversationTimeout();\n                    overlay.show("Escuchando"); updateNotification("Codex escuchando"); break;''',
'''                case "codex_listening":\n                    if (gracefulEndPending) {\n                        AndroidDebugLog.log("codex_listening ignored while graceful end is pending");\n                        updateNotification("Mic apagado · esperando cierre de Voice");\n                        break;\n                    }\n                    endingSession = false; stopWakeRecognition(); stopResponseTranscriber(); startSpeaker(); startMicStreaming(); armConversationTimeout();\n                    overlay.show("Escuchando"); updateNotification("Codex escuchando"); break;''', 'codex listening pending')
s = replace_once(s,
'''    private void finishLocalSession() {\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide(); startWakeRecognition();\n        updateNotification("Conectado · " + wakeWord());\n    }''',
'''    private void finishLocalSession() {\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        cancelPendingPcEnd();\n        gracefulEndPending = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide(); startWakeRecognition();\n        updateNotification("Conectado · " + wakeWord());\n    }''', 'finish local session')
s = replace_once(s,
'''    private void requestEndSession(String reason) {\n        if (!streaming.get() || endingSession) return;\n        endingSession = true; handler.removeCallbacks(conversationTimeoutRunnable);\n        AndroidDebugLog.log("Request end session: " + reason);\n        sendText("{\\\"type\\\":\\\"end_session\\\",\\\"reason\\\":\\\"" + reason + "\\\"}");\n        overlay.show("Finalizando…"); updateNotification("Finalizando conversación…");\n    }''',
'''    private void requestEndSession(String reason) {\n        if ((!streaming.get() && !gracefulEndPending) || endingSession) return;\n        cancelPendingPcEnd();\n        gracefulEndPending = false;\n        endingSession = true; handler.removeCallbacks(conversationTimeoutRunnable);\n        AndroidDebugLog.log("Request end session NOW: " + reason);\n        sendText("{\\\"type\\\":\\\"end_session\\\",\\\"reason\\\":\\\"" + reason + "\\\"}");\n        overlay.show("Finalizando…"); updateNotification("Finalizando conversación…");\n    }\n\n    private void requestGracefulEnd(String reason) {\n        if (!streaming.get() || endingSession || gracefulEndPending) return;\n        gracefulEndPending = true;\n        handler.removeCallbacks(conversationTimeoutRunnable);\n        int delaySeconds = Math.max(0, Math.min(120, prefs().getInt("pc_voice_close_delay_s", 15)));\n        AndroidDebugLog.log("Graceful local end: " + reason + " · mic OFF now · PC Voice close in " + delaySeconds + "s");\n        stopLocalMicKeepDownlink();\n        updateNotification(delaySeconds == 0 ? "Mic apagado · cerrando Voice…" : "Mic apagado · Voice sigue activo " + delaySeconds + " s");\n        if (delaySeconds == 0) { requestEndSession(reason + "_delayed"); return; }\n        pendingPcEndRunnable = () -> requestEndSession(reason + "_delayed");\n        handler.postDelayed(pendingPcEndRunnable, delaySeconds * 1000L);\n    }\n\n    private void cancelPendingPcEnd() {\n        if (pendingPcEndRunnable != null) {\n            handler.removeCallbacks(pendingPcEndRunnable);\n            pendingPcEndRunnable = null;\n        }\n    }\n\n    private void stopLocalMicKeepDownlink() {\n        if (!streaming.compareAndSet(true, false)) return;\n        phraseQueue.clear();\n        sendText("{\\\"type\\\":\\\"mic_stop\\\"}");\n        AndroidDebugLog.log("Local mic stopped; downlink kept alive");\n    }''', 'graceful end methods')
s = replace_once(s, '                        requestEndSession("phrase");', '                        requestGracefulEnd("phrase");', 'phrase graceful end')
s = replace_once(s, '        destroyed = true; handler.removeCallbacksAndMessages(null);', '        destroyed = true; cancelPendingPcEnd(); handler.removeCallbacksAndMessages(null);', 'destroy cancel pending')
p.write_text(s, encoding='utf-8')

# Windows companion
p = Path('windows/CodexAudioRemote.Server/Program.cs')
s = p.read_text(encoding='utf-8')
s = replace_once(s,
'''            using var cts = new CancellationTokenSource();\n            var registryTask = WatchCodexMic(socket, sendGate, switcher, cts.Token);''',
'''            using var cts = new CancellationTokenSource();\n            bool gracefulHold = false;\n            var registryTask = WatchCodexMic(socket, sendGate, switcher, () => gracefulHold, cts.Token);''', 'windows hold state')
s = replace_once(s,
'''            async Task StopAudioSession(bool notify = true)\n            {\n                codexInputRecorder?.Dispose(); codexInputRecorder = null;\n                audioSink?.Dispose(); audioSink = null;\n                downlink?.Dispose(); downlink = null;\n                if (notify) await SendJson(socket, sendGate, new { type = "downlink_stop" });\n            }''',
'''            async Task StopAudioSession(bool notify = true)\n            {\n                codexInputRecorder?.Dispose(); codexInputRecorder = null;\n                audioSink?.Dispose(); audioSink = null;\n                downlink?.Dispose(); downlink = null;\n                if (notify) await SendJson(socket, sendGate, new { type = "downlink_stop" });\n            }\n\n            async Task StopUplinkOnly()\n            {\n                codexInputRecorder?.Dispose(); codexInputRecorder = null;\n                audioSink?.Dispose(); audioSink = null;\n                await SendJson(socket, sendGate, new { type = "mic_stopped" });\n                Console.WriteLine("Remote microphone stopped; Codex Voice/downlink kept alive");\n            }''', 'stop uplink only')
s = replace_once(s, '                    case "audio_start":\n                        audioBytes = 0;', '                    case "audio_start":\n                        gracefulHold = false;\n                        audioBytes = 0;', 'audio start clears hold')
s = replace_once(s,
'''                    case "audio_stop":\n                        await StopAudioSession();\n                        Console.WriteLine("Bidirectional audio session stopped");\n                        break;''',
'''                    case "mic_stop":\n                        gracefulHold = true;\n                        await StopUplinkOnly();\n                        Console.WriteLine("Graceful hold ACTIVE: suppressing mic-idle session end until Android closes Voice");\n                        break;\n\n                    case "audio_stop":\n                        gracefulHold = false;\n                        await StopAudioSession();\n                        Console.WriteLine("Bidirectional audio session stopped");\n                        break;''', 'mic stop protocol')
s = replace_once(s, '                    case "end_session":\n                        var reason =', '                    case "end_session":\n                        gracefulHold = false;\n                        var reason =', 'end clears hold')
s = replace_once(s,
'async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, CancellationToken token)',
'async Task WatchCodexMic(WebSocket socket, SemaphoreSlim gate, AudioDeviceSwitcher audioSwitcher, Func<bool> suppressIdle, CancellationToken token)', 'watch signature')
s = replace_once(s,
'''        else\n        {\n            if (announcedActive == true)''',
'''        else\n        {\n            if (suppressIdle())\n            {\n                if (idleSince != 0) idleSince = 0;\n                if (announcedActive == true) Console.WriteLine("Codex mic inactive but session end is suppressed during graceful hold (likely thinking/processing)");\n                await Task.Delay(100, token);\n                continue;\n            }\n            if (announcedActive == true)''', 'suppress idle during hold')
p.write_text(s, encoding='utf-8')

print('Graceful end patch ready for build')
