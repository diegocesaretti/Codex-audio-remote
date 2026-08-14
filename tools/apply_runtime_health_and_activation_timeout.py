from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)

# -----------------------------------------------------------------------------
# Runtime status panel. It deliberately reads SharedPreferences instead of
# binding to RemoteService so the UI can be recreated at any time without
# touching/restarting the persistent socket.
# -----------------------------------------------------------------------------
panel = Path('android/app/src/main/java/com/bwa3d/codexremote/RuntimeStatusPanel.java')
panel.write_text(r'''package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.TextView;

import java.util.Locale;

public class RuntimeStatusPanel extends LinearLayout {
    private final TextView connectionText;
    private final TextView wakeText;
    private final TextView detailText;
    private final Handler handler = new Handler(Looper.getMainLooper());

    private final Runnable refreshRunnable = new Runnable() {
        @Override public void run() {
            refresh();
            handler.postDelayed(this, 500);
        }
    };

    public RuntimeStatusPanel(Context context) { this(context, null); }
    public RuntimeStatusPanel(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        int density = Math.max(1, Math.round(getResources().getDisplayMetrics().density));
        setPadding(14 * density, 11 * density, 14 * density, 11 * density);

        GradientDrawable bg = new GradientDrawable();
        bg.setColor(0x18000000);
        bg.setCornerRadius(12 * density);
        setBackground(bg);

        connectionText = makeText(16f, true);
        wakeText = makeText(16f, true);
        detailText = makeText(12f, false);
        detailText.setTextColor(0xFF777777);

        addView(connectionText, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        LayoutParams wp = new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        wp.topMargin = 5 * density;
        addView(wakeText, wp);
        LayoutParams dp = new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        dp.topMargin = 5 * density;
        addView(detailText, dp);
        refresh();
    }

    private TextView makeText(float size, boolean bold) {
        TextView t = new TextView(getContext());
        t.setTextSize(size);
        if (bold) t.setTypeface(t.getTypeface(), android.graphics.Typeface.BOLD);
        return t;
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        handler.removeCallbacks(refreshRunnable);
        handler.post(refreshRunnable);
    }

    @Override protected void onDetachedFromWindow() {
        handler.removeCallbacks(refreshRunnable);
        super.onDetachedFromWindow();
    }

    private void refresh() {
        SharedPreferences p = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE);
        String connection = p.getString("runtime_connection", "stopped");
        String wake = p.getString("runtime_wake", "stopped");
        String detail = p.getString("runtime_detail", "Servicio todavía no iniciado");
        long lastAudio = p.getLong("runtime_wake_audio_ms", 0L);
        int rms = p.getInt("runtime_wake_rms", 0);

        if ("connected".equals(connection)) {
            connectionText.setText("● PC: CONECTADO");
            connectionText.setTextColor(Color.rgb(25, 145, 70));
        } else if ("connecting".equals(connection)) {
            connectionText.setText("● PC: RECONECTANDO…");
            connectionText.setTextColor(Color.rgb(210, 140, 0));
        } else {
            connectionText.setText("● PC: DESCONECTADO");
            connectionText.setTextColor(Color.rgb(190, 45, 45));
        }

        long age = lastAudio <= 0 ? Long.MAX_VALUE : Math.max(0, System.currentTimeMillis() - lastAudio);
        if ("listening".equals(wake)) {
            if (age <= 5500) {
                wakeText.setText("● WAKE: ESCUCHANDO · audio OK · RMS " + rms);
                wakeText.setTextColor(Color.rgb(25, 145, 70));
            } else {
                wakeText.setText("● WAKE: ARMADO · SIN AUDIO " + ageText(age));
                wakeText.setTextColor(Color.rgb(210, 140, 0));
            }
        } else if ("activating".equals(wake)) {
            wakeText.setText("● WAKE: ACTIVANDO CODEX…");
            wakeText.setTextColor(Color.rgb(210, 140, 0));
        } else if ("conversation".equals(wake)) {
            wakeText.setText("● WAKE: PAUSADO · conversación activa");
            wakeText.setTextColor(Color.rgb(45, 105, 190));
        } else if ("rearming".equals(wake)) {
            wakeText.setText("● WAKE: REARMANDO…");
            wakeText.setTextColor(Color.rgb(210, 140, 0));
        } else if ("disconnected".equals(wake)) {
            wakeText.setText("● WAKE: PAUSADO · PC desconectada");
            wakeText.setTextColor(Color.rgb(190, 45, 45));
        } else if ("error".equals(wake) || "stalled".equals(wake)) {
            wakeText.setText("● WAKE: ERROR · autorrecuperando…");
            wakeText.setTextColor(Color.rgb(190, 45, 45));
        } else {
            wakeText.setText("● WAKE: DETENIDO");
            wakeText.setTextColor(Color.rgb(190, 45, 45));
        }

        detailText.setText(detail == null ? "" : detail);
    }

    private static String ageText(long ms) {
        if (ms == Long.MAX_VALUE) return "(sin heartbeat)";
        if (ms < 1000) return "hace <1 s";
        return String.format(Locale.US, "hace %.1f s", ms / 1000.0);
    }
}
''', encoding='utf-8')

# Put the live panel at the top of settings, where it remains visible without
# scrolling through all the audio controls.
layout = Path('android/app/src/main/res/layout/activity_main.xml')
s = layout.read_text(encoding='utf-8')
anchor = '        <TextView android:layout_width="match_parent" android:layout_height="wrap_content" android:layout_marginTop="6dp" android:text="Wake local configurable con Vosk" />'
insert = anchor + '''\n        <com.bwa3d.codexremote.RuntimeStatusPanel\n            android:layout_width="match_parent"\n            android:layout_height="wrap_content"\n            android:layout_marginTop="10dp" />'''
if 'RuntimeStatusPanel' not in s:
    s = replace_once(s, anchor, insert, 'runtime status panel layout')
layout.write_text(s, encoding='utf-8')

# -----------------------------------------------------------------------------
# RemoteService runtime health + activation timeout.
# This patch runs after all existing Android lifecycle patches.
# -----------------------------------------------------------------------------
p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

s = replace_once(
    s,
'''    private PowerManager.WakeLock serviceWakeLock;\n    private Runnable wakeRearmRunnable;\n    private boolean activationPending;\n    private String lastPartial = "";''',
'''    private PowerManager.WakeLock serviceWakeLock;\n    private Runnable wakeRearmRunnable;\n    private boolean activationPending;\n    private boolean activationTimedOut;\n    private long lastWakeAudioMs;\n    private String lastPartial = "";''',
    'runtime health fields')

# Small state publisher used by the polling RuntimeStatusPanel.
s = replace_once(
    s,
'''    private SharedPreferences prefs() { return getSharedPreferences("settings", MODE_PRIVATE); }''',
'''    private SharedPreferences prefs() { return getSharedPreferences("settings", MODE_PRIVATE); }\n\n    private void runtimeConnection(String state, String detail) {\n        prefs().edit().putString("runtime_connection", state).putString("runtime_detail", detail == null ? "" : detail)\n                .putLong("runtime_updated_ms", System.currentTimeMillis()).apply();\n    }\n\n    private void runtimeWake(String state, String detail) {\n        prefs().edit().putString("runtime_wake", state).putString("runtime_detail", detail == null ? "" : detail)\n                .putLong("runtime_updated_ms", System.currentTimeMillis()).apply();\n    }\n\n    private void runtimeWakeAudio(int rms, int peak) {\n        long now = System.currentTimeMillis();\n        lastWakeAudioMs = now;\n        prefs().edit().putLong("runtime_wake_audio_ms", now).putInt("runtime_wake_rms", rms).putInt("runtime_wake_peak", peak).apply();\n    }''',
    'runtime state publishers')

# Initial UI state.
s = replace_once(
    s,
'''        handler.postDelayed(serviceWatchdogRunnable, 3000);\n        AndroidDebugLog.log("RemoteService created · API=" + Build.VERSION.SDK_INT + " · persistent watchdog armed");''',
'''        handler.postDelayed(serviceWatchdogRunnable, 3000);\n        runtimeConnection("connecting", "Servicio iniciado · preparando conexión");\n        runtimeWake("rearming", "Preparando detector local");\n        AndroidDebugLog.log("RemoteService created · API=" + Build.VERSION.SDK_INT + " · persistent watchdog armed");''',
    'initial runtime state')

# Connecting/open/failure state.
s = replace_once(
    s,
'''        connected = false;\n        updateNotification("Conectando a " + serverIp + "…");\n        AndroidDebugLog.log("WS connecting ws://" + serverIp + ":" + serverPort + "/ws/");''',
'''        connected = false;\n        runtimeConnection("connecting", "Conectando a " + serverIp + ":" + serverPort);\n        runtimeWake("disconnected", "Esperando conexión con la PC");\n        updateNotification("Conectando a " + serverIp + "…");\n        AndroidDebugLog.log("WS connecting ws://" + serverIp + ":" + serverPort + "/ws/");''',
    'runtime connecting state')

s = replace_once(
    s,
'''                AndroidDebugLog.log("WS open · current socket");\n                sendText("{\\\"type\\\":\\\"hello\\\",\\\"name\\\":\\\"Android satellite\\\"}");''',
'''                AndroidDebugLog.log("WS open · current socket");\n                runtimeConnection("connected", "WebSocket activo · " + serverIp + ":" + serverPort);\n                runtimeWake("rearming", "Conectado · armando wake");\n                sendText("{\\\"type\\\":\\\"hello\\\",\\\"name\\\":\\\"Android satellite\\\"}");''',
    'runtime connected state')

s = replace_once(
    s,
'''                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false;\n                socket = null;''',
'''                AndroidDebugLog.log("WS closed code=" + code + " reason=" + reason);\n                connected = false;\n                runtimeConnection("disconnected", "Socket cerrado · code=" + code + " · " + reason);\n                runtimeWake("disconnected", "Wake pausado hasta recuperar la PC");\n                socket = null;''',
    'runtime closed state')

s = replace_once(
    s,
'''                AndroidDebugLog.log("WS failure: " + t);\n                connected = false;\n                socket = null;''',
'''                AndroidDebugLog.log("WS failure: " + t);\n                connected = false;\n                runtimeConnection("disconnected", "Error de red · " + t.getClass().getSimpleName() + ": " + t.getMessage());\n                runtimeWake("disconnected", "Wake pausado hasta recuperar la PC");\n                socket = null;''',
    'runtime failure state')

# API23 wake state and real AudioRecord heartbeat.
s = replace_once(
    s,
'''                AndroidDebugLog.log("Legacy wake ARMED · word=" + wakeWord() + " · source=" + source + " · buffer=" + bufferBytes);\n                updateNotification("Conectado · wake API23 · " + wakeWord());''',
'''                lastWakeAudioMs = System.currentTimeMillis();\n                AndroidDebugLog.log("Legacy wake ARMED · word=" + wakeWord() + " · source=" + source + " · buffer=" + bufferBytes);\n                runtimeWake("listening", "Wake activo · fuente Android=" + source + " · esperando “" + wakeWord() + "”");\n                updateNotification("Conectado · wake API23 · " + wakeWord());''',
    'wake armed runtime state')

s = replace_once(
    s,
'''                    long nowMs = System.currentTimeMillis();\n                    if (nowMs - lastLevelLogMs >= 3000) {''',
'''                    long nowMs = System.currentTimeMillis();\n                    lastWakeAudioMs = nowMs;\n                    if (nowMs - lastLevelLogMs >= 3000) {''',
    'real wake audio heartbeat')

s = replace_once(
    s,
'''                        int rms = samples > 0 ? (int)Math.sqrt(sum / (double)samples) : 0;\n                        AndroidDebugLog.log("Legacy wake audio · source=" + source + " · read=" + read + " · rms=" + rms + " · peak=" + peak);''',
'''                        int rms = samples > 0 ? (int)Math.sqrt(sum / (double)samples) : 0;\n                        runtimeWakeAudio(rms, peak);\n                        AndroidDebugLog.log("Legacy wake audio · source=" + source + " · read=" + read + " · rms=" + rms + " · peak=" + peak);''',
    'wake audio status sample')

s = replace_once(
    s,
'''            } catch (Exception e) {\n                AndroidDebugLog.log("Legacy wake error: " + e);\n                updateNotification("Wake API23 error: " + e.getClass().getSimpleName());''',
'''            } catch (Exception e) {\n                AndroidDebugLog.log("Legacy wake error: " + e);\n                runtimeWake("error", "Wake API23 · " + e.getClass().getSimpleName() + ": " + e.getMessage());\n                updateNotification("Wake API23 error: " + e.getClass().getSimpleName());''',
    'wake error runtime state')

s = replace_once(
    s,
'''                AndroidDebugLog.log("Legacy wake STOP");\n                if (!destroyed && connected && !conversationActive && !activationPending && !streaming.get())''',
'''                AndroidDebugLog.log("Legacy wake STOP");\n                if (destroyed) runtimeWake("stopped", "Servicio detenido");\n                else if (!connected) runtimeWake("disconnected", "Wake pausado · PC desconectada");\n                else if (conversationActive) runtimeWake("conversation", "Conversación activa");\n                else if (activationPending) runtimeWake("activating", "Wake detectado · esperando a Codex");\n                else runtimeWake("rearming", "Detector detenido · rearmando automáticamente");\n                if (!destroyed && connected && !conversationActive && !activationPending && !streaming.get())''',
    'wake stopped runtime state')

# Activation timeout. Successful activations are ~2 seconds in the supplied logs; 10 seconds is
# deliberately generous, while still preventing a failed activation from disabling wake forever.
s = replace_once(
    s,
'''    private void triggerWake() {''',
'''    private final Runnable activationTimeoutRunnable = () -> {\n        if (destroyed || !activationPending || conversationActive) return;\n        activationPending = false;\n        activationTimedOut = true;\n        AndroidDebugLog.log("Activation timeout · no codex_listening within 10s · rearming wake");\n        runtimeWake("rearming", "Activación agotó 10 s · recuperando wake");\n        sendText("{\\\"type\\\":\\\"end_session\\\",\\\"reason\\\":\\\"activation_timeout\\\"}");\n        if (overlay != null) overlay.hide();\n        updateNotification("Activación sin respuesta · rearmando wake");\n        scheduleWakeRearm("activation_timeout");\n    };\n\n    private void triggerWake() {''',
    'activation timeout runnable')

s = replace_once(
    s,
'''        activationPending = sent;\n        AndroidDebugLog.log("WAKE send=" + sent + " · activationPending=" + activationPending + " · thread=" + Thread.currentThread().getName());\n        stopWakeRecognition();''',
'''        activationPending = sent;\n        activationTimedOut = false;\n        handler.removeCallbacks(activationTimeoutRunnable);\n        if (sent) {\n            runtimeWake("activating", "“" + wakeWord() + "” detectado · esperando codex_listening");\n            handler.postDelayed(activationTimeoutRunnable, 10000);\n        }\n        AndroidDebugLog.log("WAKE send=" + sent + " · activationPending=" + activationPending + " · thread=" + Thread.currentThread().getName());\n        stopWakeRecognition();''',
    'activation timeout scheduling')

# Keep the timeout refreshed/visible on the explicit server activating event.
s = replace_once(
    s,
'''                case "activating": activationPending = true; stopWakeRecognition(); overlay.clearTranscript(); overlay.show("Activando…"); updateNotification("Activando Codex…"); break;''',
'''                case "activating":\n                    activationPending = true;\n                    runtimeWake("activating", "PC recibió wake · Codex activando");\n                    stopWakeRecognition(); overlay.clearTranscript(); overlay.show("Activando…"); updateNotification("Activando Codex…"); break;''',
    'activating runtime state')

# A late codex_listening after local timeout must not steal AudioRecord back from the rearmed wake.
s = replace_once(
    s,
'''                case "codex_listening":\n                    activationPending = false;\n                    if (gracefulEndPending) {''',
'''                case "codex_listening":\n                    activationPending = false;\n                    handler.removeCallbacks(activationTimeoutRunnable);\n                    if (activationTimedOut) {\n                        AndroidDebugLog.log("Ignoring late codex_listening after activation timeout");\n                        sendText("{\\\"type\\\":\\\"end_session\\\",\\\"reason\\\":\\\"late_activation_after_timeout\\\"}");\n                        runtimeWake("rearming", "Codex respondió tarde · manteniendo wake disponible");\n                        if (overlay != null) overlay.hide();\n                        scheduleWakeRearm("late_activation");\n                        break;\n                    }\n                    runtimeWake("conversation", "Codex escuchando · wake pausado durante conversación");\n                    if (gracefulEndPending) {''',
    'late activation guard')

# Session finish explicitly re-enters rearming state.
s = replace_once(
    s,
'''        activationPending = false;\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide();\n        scheduleWakeRearm("session_finished");''',
'''        activationPending = false;\n        activationTimedOut = false;\n        handler.removeCallbacks(activationTimeoutRunnable);\n        runtimeWake("rearming", "Conversación finalizada · rearmando wake");\n        endingSession = false; stopMicStreaming(); stopSpeaker(); stopResponseTranscriber(); overlay.hide();\n        scheduleWakeRearm("session_finished");''',
    'session finish runtime state')

# Existing watchdog only checked whether the wake thread existed. Add a real audio heartbeat: if
# AudioRecord stops delivering buffers while the thread still claims to be armed, recycle capture.
s = replace_once(
    s,
'''                    boolean wakeArmed = Build.VERSION.SDK_INT <= 23 ? legacyWakeRunning.get() : speechService != null;\n                    if (!wakeArmed) {\n                        AndroidDebugLog.log("Service watchdog: connected but wake not armed -> rearm");\n                        scheduleWakeRearm("watchdog");\n                    }''',
'''                    boolean wakeArmed = Build.VERSION.SDK_INT <= 23 ? legacyWakeRunning.get() : speechService != null;\n                    long wakeAudioAge = lastWakeAudioMs <= 0 ? Long.MAX_VALUE : System.currentTimeMillis() - lastWakeAudioMs;\n                    if (Build.VERSION.SDK_INT <= 23 && wakeArmed && wakeAudioAge > 7500) {\n                        AndroidDebugLog.log("Service watchdog: wake thread armed but audio heartbeat stale " + wakeAudioAge + "ms -> recycle");\n                        runtimeWake("stalled", "Wake armado pero AudioRecord no entrega buffers · reiniciando");\n                        stopWakeRecognition();\n                        scheduleWakeRearm("wake_audio_stalled");\n                    } else if (!wakeArmed) {\n                        AndroidDebugLog.log("Service watchdog: connected but wake not armed -> rearm");\n                        runtimeWake("rearming", "Watchdog detectó wake detenido");\n                        scheduleWakeRearm("watchdog");\n                    }''',
    'wake audio heartbeat watchdog')

# Transport resets cancel activation timeout and publish the paused state.
s = replace_once(
    s,
'''        activationPending = false;\n        if (streaming.get()) stopMicStreaming();''',
'''        activationPending = false;\n        activationTimedOut = false;\n        handler.removeCallbacks(activationTimeoutRunnable);\n        runtimeWake("disconnected", "Transporte perdido · esperando reconexión");\n        if (streaming.get()) stopMicStreaming();''',
    'transport timeout cleanup')

# Final stopped status.
s = replace_once(
    s,
'''        AndroidDebugLog.log("RemoteService destroyed");\n        super.onDestroy();''',
'''        runtimeConnection("stopped", "RemoteService detenido");\n        runtimeWake("stopped", "Servicio detenido");\n        AndroidDebugLog.log("RemoteService destroyed");\n        super.onDestroy();''',
    'service destroyed runtime state')

p.write_text(s, encoding='utf-8')
print('Android runtime health indicators + activation timeout applied')
