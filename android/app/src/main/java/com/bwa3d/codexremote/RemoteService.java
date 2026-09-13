package com.bwa3d.codexremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;

import org.json.JSONArray;
import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;
import org.vosk.android.RecognitionListener;
import org.vosk.android.SpeechService;

import java.io.File;
import java.text.Normalizer;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;

/**
 * Protocol v2 Android satellite.
 *
 * Windows is the only authority for conversation state. Android never infers a session
 * transition from microphone activity, timers, overlays, or socket side effects. Every local
 * audio resource is derived from the latest server state through reconcileAudioPolicy().
 */
public class RemoteService extends Service implements RecognitionListener {
    public static final String ACTION_START = "com.bwa3d.codexremote.START";
    public static final String ACTION_WAKE = "com.bwa3d.codexremote.WAKE";
    public static final String ACTION_OVERLAY_END = "com.bwa3d.codexremote.OVERLAY_END";
    public static final String ACTION_WAKE_WORD_CHANGED = "com.bwa3d.codexremote.WAKE_WORD_CHANGED";
    public static final String ACTION_STATUS = "com.bwa3d.codexremote.STATUS_V2";

    public static final String EXTRA_CONNECTED = "connected";
    public static final String EXTRA_CONNECTING = "connecting";
    public static final String EXTRA_SERVER_STATE = "server_state";
    public static final String EXTRA_WAKE_RUNNING = "wake_running";
    public static final String EXTRA_WAKE_HEALTHY = "wake_healthy";
    public static final String EXTRA_WAKE_RMS = "wake_rms";
    public static final String EXTRA_WAKE_AUDIO_AGE = "wake_audio_age";
    public static final String EXTRA_MIC_RUNNING = "mic_running";
    public static final String EXTRA_MIC_HEALTHY = "mic_healthy";
    public static final String EXTRA_REVISION = "revision";
    public static final String EXTRA_SESSION_ID = "session_id";

    private static final int NOTIFICATION_ID = 42;
    private static final String CHANNEL_ID = "codex_remote_v2";
    private static final int WAKE_SAMPLE_RATE = 16000;
    private static final long RECONNECT_MS = 1500L;
    private static final long AUDIO_HEARTBEAT_STALE_MS = 7500L;
    private static final long MIC_HEARTBEAT_STALE_MS = 5000L;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Object socketLock = new Object();
    private final AtomicBoolean wakeRunning = new AtomicBoolean(false);
    private final AtomicBoolean micRunning = new AtomicBoolean(false);
    private final ArrayBlockingQueue<byte[]> phraseQueue = new ArrayBlockingQueue<>(24);

    private OkHttpClient client;
    private WebSocket socket;
    private long socketGeneration;
    private volatile boolean connected;
    private volatile boolean connecting;
    private boolean destroyed;

    private String serverIp;
    private int serverPort = 8765;
    private volatile String serverState = "disconnected";
    private volatile String sessionId = "";
    private long serverRevision = -1;

    private Model voskModel;
    private boolean modelLoading;
    private SpeechService speechService;
    private Thread wakeThread;
    private AudioRecord wakeRecord;
    private long lastWakeAudioMs;
    private int lastWakeRms;
    private long lastWakeMs;
    private String wakeFirstWord = "";
    private long wakeFirstWordMs;
    private String lastWakeLogged = "";

    private Thread micThread;
    private AudioRecord micRecord;
    private long lastMicAudioMs;
    private Thread phraseThread;

    private DownlinkPlayer speaker;
    private OverlayController overlay;
    private ResponseTranscriber responseTranscriber;
    private PowerManager.WakeLock serviceWakeLock;
    private boolean wakeReceiverRegistered;

    private final BroadcastReceiver wakeWordReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            AndroidDebugLog.log("Wake word changed -> " + wakeWord());
            stopWakeCapture("wake_word_changed");
            handler.postDelayed(RemoteService.this::reconcileAudioPolicy, 180);
        }
    };

    private final Runnable reconnectRunnable = new Runnable() {
        @Override public void run() {
            if (destroyed || connected || connecting) return;
            connectIfNeeded();
        }
    };

    private final Runnable healthRunnable = new Runnable() {
        @Override public void run() {
            if (destroyed) return;
            long now = System.currentTimeMillis();

            if (connected && "idle".equals(serverState) && Build.VERSION.SDK_INT <= 23 && wakeRunning.get()) {
                long age = lastWakeAudioMs <= 0 ? Long.MAX_VALUE : now - lastWakeAudioMs;
                if (age > AUDIO_HEARTBEAT_STALE_MS) {
                    AndroidDebugLog.log("Wake heartbeat stale " + age + "ms -> recycle capture");
                    stopWakeCapture("heartbeat_stale");
                }
            }

            if (connected && "listening".equals(serverState) && micRunning.get()) {
                long age = lastMicAudioMs <= 0 ? Long.MAX_VALUE : now - lastMicAudioMs;
                if (age > MIC_HEARTBEAT_STALE_MS) {
                    AndroidDebugLog.log("Conversation mic heartbeat stale " + age + "ms -> recycle capture");
                    stopConversationMic("heartbeat_stale");
                }
            }

            reconcileAudioPolicy();
            broadcastStatus();
            handler.postDelayed(this, 2000L);
        }
    };

    private final Runnable conversationTimeoutRunnable = new Runnable() {
        @Override public void run() {
            if (connected && "listening".equals(serverState)) sendEndEvent("timeout");
        }
    };

    @Override public void onCreate() {
        super.onCreate();
        AndroidDebugLog.install(this);
        createChannel();
        startForeground(NOTIFICATION_ID, notification("Iniciando stack v2…"));

        overlay = new OverlayController(this, new Runnable() {
            @Override public void run() { sendEndEvent("overlay_tap"); }
        });

        client = new OkHttpClient.Builder()
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .pingInterval(10, TimeUnit.SECONDS)
                .build();

        try {
            PowerManager pm = (PowerManager)getSystemService(POWER_SERVICE);
            if (pm != null) {
                serviceWakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "codexremote:v2-service");
                serviceWakeLock.setReferenceCounted(false);
                serviceWakeLock.acquire();
            }
        } catch (Exception e) { AndroidDebugLog.log("Service wake-lock error: " + e); }

        IntentFilter filter = new IntentFilter(ACTION_WAKE_WORD_CHANGED);
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(wakeWordReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        else registerReceiver(wakeWordReceiver, filter);
        wakeReceiverRegistered = true;

        loadBundledModel();
        handler.post(healthRunnable);
        AndroidDebugLog.log("RemoteService v2 created · API=" + Build.VERSION.SDK_INT);
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            loadTargetFromPrefs();
            connectIfNeeded();
            return START_STICKY;
        }

        String action = intent.getAction();
        AndroidDebugLog.log("Service v2 command: " + action);
        if (ACTION_START.equals(action)) {
            String requestedIp = intent.getStringExtra("ip");
            int requestedPort = intent.getIntExtra("port", prefs().getInt("port", 8765));
            if (requestedIp == null || requestedIp.trim().isEmpty()) requestedIp = prefs().getString("ip", "192.168.1.100");
            requestedIp = requestedIp.trim();

            boolean targetChanged = serverIp != null && (!requestedIp.equals(serverIp) || requestedPort != serverPort);
            serverIp = requestedIp;
            serverPort = requestedPort;
            if (targetChanged) disconnectTransport("target_changed");
            connectIfNeeded();
        } else if (ACTION_WAKE.equals(action)) {
            sendWakeEvent("manual");
        } else if (ACTION_OVERLAY_END.equals(action)) {
            sendEndEvent("overlay_tap");
        }
        return START_STICKY;
    }

    private void loadTargetFromPrefs() {
        serverIp = prefs().getString("ip", "192.168.1.100");
        serverPort = prefs().getInt("port", 8765);
    }

    private void connectIfNeeded() {
        if (destroyed) return;
        if (serverIp == null || serverIp.trim().isEmpty()) loadTargetFromPrefs();
        synchronized (socketLock) {
            if (connected || connecting || socket != null || destroyed) return;
            connecting = true;
            serverRevision = -1;
            serverState = "disconnected";
            sessionId = "";
            final long generation = ++socketGeneration;
            final String url = "ws://" + serverIp + ":" + serverPort + "/ws/";
            AndroidDebugLog.log("WS v2 connecting · g=" + generation + " · " + url);
            updateNotification("Conectando a " + serverIp + "…");
            broadcastStatus();

            socket = client.newWebSocket(new Request.Builder().url(url).build(), new WebSocketListener() {
                @Override public void onOpen(WebSocket webSocket, Response response) {
                    synchronized (socketLock) {
                        if (generation != socketGeneration || webSocket != socket) {
                            try { webSocket.close(1000, "stale"); } catch (Exception ignored) { }
                            return;
                        }
                        connected = true;
                        connecting = false;
                    }
                    AndroidDebugLog.log("WS v2 OPEN · g=" + generation);

                    // Initial handshake must go directly over the socket that just opened. Using
                    // sendText() here can race with global transport state and schedule a duplicate
                    // reconnect while this socket is actually healthy.
                    boolean helloOk = false;
                    boolean syncOk = false;
                    try {
                        helloOk = webSocket.send("{\"type\":\"hello\",\"protocol\":2,\"name\":\"Android satellite\"}");
                        syncOk = webSocket.send("{\"type\":\"sync\"}");
                    } catch (Exception e) {
                        AndroidDebugLog.log("WS v2 initial handshake exception · g=" + generation + " · " + e);
                    }
                    AndroidDebugLog.log("WS v2 handshake · g=" + generation + " · hello=" + helloOk + " · sync=" + syncOk);
                    if (!helloOk || !syncOk) {
                        failCurrentTransport(webSocket, generation, "initial_handshake_send_failed");
                        return;
                    }
                    broadcastStatus();
                }

                @Override public void onMessage(WebSocket webSocket, String text) {
                    if (!isCurrentSocket(webSocket, generation)) return;
                    AndroidDebugLog.log("WS v2 <- " + text);
                    handler.post(new Runnable() {
                        @Override public void run() { handleServerMessage(text); }
                    });
                }

                @Override public void onMessage(WebSocket webSocket, ByteString bytes) {
                    if (!isCurrentSocket(webSocket, generation)) return;
                    if (connected && "listening".equals(serverState)) playDownlink(bytes.toByteArray());
                }

                @Override public void onClosed(WebSocket webSocket, int code, String reason) {
                    if (!isCurrentSocket(webSocket, generation)) return;
                    AndroidDebugLog.log("WS v2 CLOSED · g=" + generation + " · code=" + code + " · " + reason);
                    onTransportLost(webSocket, generation, "closed:" + code + ":" + reason);
                }

                @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                    if (!isCurrentSocket(webSocket, generation)) return;
                    String http = response == null ? "" : (" · http=" + response.code());
                    AndroidDebugLog.log("WS v2 FAILURE · g=" + generation + " · " + t.getClass().getSimpleName() + ": " + t.getMessage() + http);
                    onTransportLost(webSocket, generation, "failure:" + t.getClass().getSimpleName());
                }
            });
        }
    }

    private boolean isCurrentSocket(WebSocket candidate, long generation) {
        synchronized (socketLock) { return generation == socketGeneration && candidate == socket; }
    }

    private void failCurrentTransport(WebSocket webSocket, long generation, String reason) {
        synchronized (socketLock) {
            if (generation != socketGeneration || webSocket != socket) return;
            socketGeneration++;
            socket = null;
            connected = false;
            connecting = false;
            serverState = "disconnected";
            sessionId = "";
            serverRevision = -1;
        }
        try { webSocket.cancel(); } catch (Exception ignored) { }
        AndroidDebugLog.log("Transport failed locally -> reconnect · " + reason);
        reconcileAudioPolicy();
        broadcastStatus();
        handler.removeCallbacks(reconnectRunnable);
        handler.postDelayed(reconnectRunnable, RECONNECT_MS);
    }

    private void onTransportLost(WebSocket webSocket, long generation, String reason) {
        synchronized (socketLock) {
            if (generation != socketGeneration || webSocket != socket) return;
            socketGeneration++;
            connected = false;
            connecting = false;
            socket = null;
            serverState = "disconnected";
            sessionId = "";
            serverRevision = -1;
        }
        AndroidDebugLog.log("Transport lost -> deterministic disconnected policy · " + reason);
        reconcileAudioPolicy();
        broadcastStatus();
        handler.removeCallbacks(reconnectRunnable);
        handler.postDelayed(reconnectRunnable, RECONNECT_MS);
    }

    private void disconnectTransport(String reason) {
        WebSocket old;
        synchronized (socketLock) {
            socketGeneration++;
            old = socket;
            socket = null;
            connected = false;
            connecting = false;
            serverState = "disconnected";
            sessionId = "";
            serverRevision = -1;
        }
        if (old != null) try { old.close(1000, reason); } catch (Exception ignored) { }
        reconcileAudioPolicy();
        broadcastStatus();
    }

    private void handleServerMessage(String text) {
        try {
            JSONObject o = new JSONObject(text);
            String type = o.optString("type", "");
            if ("state".equals(type)) {
                applyAuthoritativeState(o);
            } else if ("hello".equals(type)) {
                if (o.optInt("protocol", 2) != 2) AndroidDebugLog.log("Server protocol mismatch: " + o);
            } else if ("audio_error".equals(type)) {
                AndroidDebugLog.log("Server audio error: " + o.optString("reason", "unknown"));
            }
        } catch (Exception e) { AndroidDebugLog.log("Server message parse error: " + e); }
    }

    private void applyAuthoritativeState(JSONObject o) {
        long revision = o.optLong("revision", -1);
        if (revision >= 0 && serverRevision >= 0 && revision < serverRevision) {
            AndroidDebugLog.log("Ignoring stale state revision " + revision + " < " + serverRevision);
            return;
        }

        String next = o.optString("state", "idle").toLowerCase(Locale.ROOT);
        if (!"idle".equals(next) && !"activating".equals(next) && !"listening".equals(next) && !"ending".equals(next)) {
            AndroidDebugLog.log("Ignoring unknown server state: " + next);
            return;
        }

        String previous = serverState;
        String previousSession = sessionId;
        serverRevision = revision;
        serverState = next;
        sessionId = o.optString("sessionId", "");
        AndroidDebugLog.log("STATE v2 " + previous + " -> " + serverState + " · rev=" + serverRevision + " · session=" + sessionId + " · reason=" + o.optString("reason", ""));

        if (!"listening".equals(serverState)) handler.removeCallbacks(conversationTimeoutRunnable);
        if ("listening".equals(serverState) && (!"listening".equals(previous) || !sessionId.equals(previousSession))) armConversationTimeout();

        reconcileAudioPolicy();
        broadcastStatus();
    }

    /** The only place that decides which local audio resources may exist. */
    private synchronized void reconcileAudioPolicy() {
        if (destroyed) return;

        if (!connected || "disconnected".equals(serverState)) {
            stopWakeCapture("policy_disconnected");
            stopConversationMic("policy_disconnected");
            stopSpeaker();
            stopResponseTranscriber();
            if (overlay != null) overlay.hide();
            updateNotification(connecting ? "Reconectando…" : "Sin conexión · reintentando…");
            return;
        }

        if ("idle".equals(serverState)) {
            stopConversationMic("policy_idle");
            stopSpeaker();
            stopResponseTranscriber();
            if (overlay != null) overlay.hide();
            if (!isThreadAlive(micThread)) startWakeCaptureIfReady();
            updateNotification(wakeRunning.get() ? "Conectado · wake escuchando" : "Conectado · preparando wake");
            return;
        }

        if ("activating".equals(serverState)) {
            stopWakeCapture("policy_activating");
            stopConversationMic("policy_activating");
            stopSpeaker();
            stopResponseTranscriber();
            if (overlay != null) { overlay.clearTranscript(); overlay.show("Activando…"); }
            updateNotification("Activando Codex…");
            return;
        }

        if ("listening".equals(serverState)) {
            stopWakeCapture("policy_listening");
            if (isThreadAlive(wakeThread)) {
                handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); } }, 60L);
                return;
            }
            startSpeaker();
            startConversationMicIfReady();
            if (overlay != null) overlay.show("Escuchando");
            updateNotification(micRunning.get() ? "Codex escuchando · mic Android activo" : "Codex escuchando · abriendo mic…");
            return;
        }

        stopWakeCapture("policy_ending");
        stopConversationMic("policy_ending");
        stopSpeaker();
        stopResponseTranscriber();
        if (overlay != null) overlay.show("Finalizando…");
        updateNotification("Finalizando conversación…");
    }

    // Remaining audio/wake implementation is unchanged from v2.
    // This file intentionally keeps the existing behavior below this point.

    private void loadBundledModel() {
        if (voskModel != null || modelLoading) return;
        modelLoading = true;
        new Thread(new Runnable() {
            @Override public void run() {
                try {
                    File dir = new File(getFilesDir(), "vosk-es-small");
                    if (!new File(dir, "am").exists()) throw new IllegalStateException("Bundled Vosk model missing");
                    Model loaded = new Model(dir.getAbsolutePath());
                    voskModel = loaded;
                    AndroidDebugLog.log("Vosk v2 model loaded · " + dir.getAbsolutePath());
                } catch (Exception e) {
                    AndroidDebugLog.log("Vosk v2 model load failed: " + e);
                } finally {
                    modelLoading = false;
                    handler.post(new Runnable() { @Override public void run() { reconcileAudioPolicy(); broadcastStatus(); } });
                }
            }
        }, "VoskModelV2").start();
    }

    private void startWakeCaptureIfReady() {
        if (!connected || !"idle".equals(serverState) || voskModel == null || destroyed) return;
        if (wakeRunning.get() || speechService != null || isThreadAlive(wakeThread) || isThreadAlive(micThread)) return;

        if (Build.VERSION.SDK_INT > 23) {
            try {
                Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());
                speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE);
                wakeRunning.set(true);
                lastWakeAudioMs = System.currentTimeMillis();
                speechService.startListening(this);
                AndroidDebugLog.log("Wake v2 SpeechService ARMED");
                broadcastStatus();
            } catch (Exception e) {
                wakeRunning.set(false);
                speechService = null;
                AndroidDebugLog.log("Wake SpeechService start error: " + e);
            }
            return;
        }

        if (!wakeRunning.compareAndSet(false, true)) return;
        lastWakeAudioMs = System.currentTimeMillis();
        wakeThread = new Thread(new Runnable() {
            @Override public void run() {
                AudioRecord record = null;
                Recognizer recognizer = null;
                try {
                    int min = AudioRecord.getMinBufferSize(WAKE_SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                    int bufferBytes = Math.max(min > 0 ? min * 2 : 4096, 4096);
                    int[] sources = new int[] { MediaRecorder.AudioSource.DEFAULT, MediaRecorder.AudioSource.MIC, MediaRecorder.AudioSource.VOICE_RECOGNITION, MediaRecorder.AudioSource.CAMCORDER };
                    int source = -1;
                    for (int candidate : sources) {
                        record = createCapture(WAKE_SAMPLE_RATE, bufferBytes, candidate);
                        if (record != null) { source = candidate; break; }
                    }
                    if (record == null) throw new IllegalStateException("No AudioRecord for wake");
                    wakeRecord = record;
                    recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());
                    byte[] buffer = new byte[4096];
                    record.startRecording();
                    AndroidDebugLog.log("Wake v2 ARMED · source=" + source + " · word=" + wakeWord());
                    while (wakeRunning.get() && connected && "idle".equals(serverState) && !destroyed) {
                        int read = record.read(buffer, 0, buffer.length);
                        if (read <= 0) continue;
                        lastWakeAudioMs = System.currentTimeMillis();
                        lastWakeRms = pcmRms(buffer, read);
                        boolean complete = recognizer.acceptWaveForm(buffer, read);
                        checkWake(complete ? recognizer.getResult() : recognizer.getPartialResult());
                    }
                } catch (Exception e) {
                    if (wakeRunning.get()) AndroidDebugLog.log("Wake v2 capture error: " + e);
                } finally {
                    if (record != null) { try { record.stop(); } catch (Exception ignored) { } try { record.release(); } catch (Exception ignored) { } }
                    if (recognizer != null) try { recognizer.close(); } catch (Exception ignored) { }
                    wakeRecord = null;
                    wakeRunning.set(false);
                    wakeThread = null;
                    AndroidDebugLog.log("Wake v2 RELEASED");
                    handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); broadcastStatus(); } }, 120L);
                }
            }
        }, "WakeV2");
        wakeThread.setPriority(Thread.NORM_PRIORITY + 1);
        wakeThread.start();
        broadcastStatus();
    }

    private synchronized void stopWakeCapture(String reason) {
        if (Build.VERSION.SDK_INT > 23) {
            SpeechService service = speechService;
            speechService = null;
            wakeRunning.set(false);
            if (service != null) { try { service.stop(); } catch (Exception ignored) { } try { service.shutdown(); } catch (Exception ignored) { } AndroidDebugLog.log("Wake v2 SpeechService STOP · " + reason); }
        }
        if (wakeRunning.getAndSet(false)) AndroidDebugLog.log("Wake v2 stop requested · " + reason);
        AudioRecord record = wakeRecord;
        if (record != null) try { record.stop(); } catch (Exception ignored) { }
        Thread thread = wakeThread;
        if (thread != null) thread.interrupt();
    }

    private void checkWake(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String text = normalize(o.optString("text", o.optString("partial", "")));
            if (text.isEmpty()) return;
            if (!text.equals(lastWakeLogged)) { lastWakeLogged = text; AndroidDebugLog.log("Wake v2 heard: " + text); }
            int sensitivity = prefs().getInt("sensitivity", 60);
            String target = wakeWord();
            boolean match = wakeMatches(text, sensitivity, target);
            long now = System.currentTimeMillis();
            String[] parts = target.split(" ");
            if (!match && parts.length == 2) {
                if (text.equals(parts[0])) { wakeFirstWord = parts[0]; wakeFirstWordMs = now; }
                else if (text.equals(parts[1]) && wakeFirstWord.equals(parts[0]) && now - wakeFirstWordMs < 2200L) match = true;
            }
            if (match && now - lastWakeMs > 2500L) { lastWakeMs = now; handler.post(new Runnable() { @Override public void run() { sendWakeEvent("voice"); } }); }
        } catch (Exception e) { AndroidDebugLog.log("Wake v2 parse error: " + e); }
    }

    private void sendWakeEvent(String source) {
        if (!connected || !"idle".equals(serverState)) { AndroidDebugLog.log("Wake event ignored locally · connected=" + connected + " · state=" + serverState); return; }
        wakeScreenIfEnabled();
        boolean sent = sendText("{\"type\":\"event\",\"event\":\"wake\",\"source\":\"" + jsonEscape(source) + "\"}");
        AndroidDebugLog.log("Wake v2 event sent=" + sent);
    }

    private void sendEndEvent(String reason) {
        if (!connected) return;
        if (!"listening".equals(serverState) && !"activating".equals(serverState)) return;
        String payload = "{\"type\":\"event\",\"event\":\"end\",\"reason\":\"" + jsonEscape(reason) + "\",\"sessionId\":\"" + jsonEscape(sessionId) + "\"}";
        boolean sent = sendText(payload);
        AndroidDebugLog.log("End v2 event · reason=" + reason + " · sent=" + sent);
    }

    private void startConversationMicIfReady() {
        if (!connected || !"listening".equals(serverState) || destroyed) return;
        if (micRunning.get() || isThreadAlive(micThread) || isThreadAlive(wakeThread)) return;
        if (!micRunning.compareAndSet(false, true)) return;
        final String micSession = sessionId;
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
                    String config = "{\"type\":\"audio_config\",\"sessionId\":\"" + jsonEscape(micSession) + "\",\"sampleRate\":" + finalRate + ",\"channels\":1,\"chunkMs\":" + chunkMs + ",\"quality\":" + quality + ",\"latency\":" + latency + ",\"gainPct\":" + gainPct + ",\"capture\":\"" + jsonEscape(audioSourceName(finalSource)) + "\"}";
                    if (!sendText(config)) throw new IllegalStateException("audio_config send failed");
                    startPhraseDetector(finalRate, micSession);
                    byte[] buffer = new byte[finalChunkBytes];
                    record.startRecording();
                    AndroidDebugLog.log("Conversation mic v2 START · session=" + micSession + " · " + finalRate + " Hz · source=" + finalSource);
                    broadcastStatus();
                    while (micRunning.get() && connected && "listening".equals(serverState) && micSession.equals(sessionId) && !destroyed) {
                        int read = record.read(buffer, 0, buffer.length);
                        if (read <= 0) continue;
                        lastMicAudioMs = System.currentTimeMillis();
                        applyGainPcm16InPlace(buffer, read, gainPct);
                        enhancer.processInPlace(buffer, read);
                        WebSocket current;
                        synchronized (socketLock) { current = socket; }
                        if (current == null || !current.send(ByteString.of(buffer, 0, read))) throw new IllegalStateException("binary send failed");
                        offerPhraseAudio(buffer, read);
                    }
                } catch (Exception e) { if (micRunning.get()) AndroidDebugLog.log("Conversation mic v2 error: " + e); }
                finally {
                    if (effects != null) effects.close();
                    if (record != null) { try { record.stop(); } catch (Exception ignored) { } try { record.release(); } catch (Exception ignored) { } }
                    micRecord = null; micRunning.set(false); micThread = null; phraseQueue.clear();
                    AndroidDebugLog.log("Conversation mic v2 RELEASED · session=" + micSession);
                    handler.postDelayed(new Runnable() { @Override public void run() { reconcileAudioPolicy(); broadcastStatus(); } }, 100L);
                }
            }
        }, "MicV2");
        micThread.setPriority(Thread.MAX_PRIORITY);
        micThread.start();
    }

    private synchronized void stopConversationMic(String reason) {
        if (micRunning.getAndSet(false)) AndroidDebugLog.log("Conversation mic v2 stop requested · " + reason);
        phraseQueue.clear();
        AudioRecord record = micRecord;
        if (record != null) try { record.stop(); } catch (Exception ignored) { }
        Thread thread = micThread;
        if (thread != null) thread.interrupt();
    }

    private void startPhraseDetector(final int sourceRate, final String detectorSession) {
        if (voskModel == null || isThreadAlive(phraseThread)) return;
        final List<String> phrases = endPhrases();
        if (phrases.isEmpty()) return;
        final int sensitivity = Math.max(0, Math.min(100, prefs().getInt("end_sensitivity", 30)));
        phraseQueue.clear();
        phraseThread = new Thread(new Runnable() {
            @Override public void run() {
                Recognizer recognizer = null;
                try {
                    recognizer = new Recognizer(voskModel, sourceRate, endGrammar(phrases)); recognizer.setWords(true);
                    while ((micRunning.get() && detectorSession.equals(sessionId)) || !phraseQueue.isEmpty()) {
                        byte[] chunk = phraseQueue.poll(100, TimeUnit.MILLISECONDS);
                        if (chunk == null) continue;
                        if (!recognizer.acceptWaveForm(chunk, chunk.length)) continue;
                        String result = recognizer.getResult();
                        String matched = matchedEndPhrase(result, phrases, sensitivity);
                        if (matched != null && connected && "listening".equals(serverState) && detectorSession.equals(sessionId)) {
                            AndroidDebugLog.log("End phrase v2 confirmed: " + matched);
                            handler.post(new Runnable() { @Override public void run() { sendEndEvent("phrase"); } }); break;
                        }
                    }
                } catch (Exception e) { AndroidDebugLog.log("End phrase v2 error: " + e); }
                finally { if (recognizer != null) try { recognizer.close(); } catch (Exception ignored) { } phraseQueue.clear(); phraseThread = null; }
            }
        }, "EndPhraseV2");
        phraseThread.start();
    }

    private void offerPhraseAudio(byte[] buffer, int read) {
        if (!isThreadAlive(phraseThread)) return;
        byte[] copy = Arrays.copyOf(buffer, read);
        if (!phraseQueue.offer(copy)) { phraseQueue.poll(); phraseQueue.offer(copy); }
    }

    private synchronized void startSpeaker() {
        if (speaker != null) return;
        int latency = prefs().getInt("audio_latency", 55);
        int prebufferMs = Build.VERSION.SDK_INT <= 23 ? (latency >= 80 ? 260 : latency >= 45 ? 320 : 400) : (latency >= 80 ? 100 : latency >= 45 ? 170 : 260);
        speaker = new DownlinkPlayer(prebufferMs, new DownlinkPlayer.Listener() {
            @Override public void onStarted() { AndroidDebugLog.log("Downlink v2 START"); }
            @Override public void onStopped() { AndroidDebugLog.log("Downlink v2 STOP"); }
            @Override public void onAudio(byte[] pcm) {
                ResponseTranscriber transcriber = responseTranscriber;
                if (transcriber != null) transcriber.accept(pcm);
            }
        });
    }

    private synchronized void playDownlink(byte[] pcm) {
        if (!"listening".equals(serverState) || pcm == null || pcm.length == 0) return;
        if (speaker == null) startSpeaker();
        if (speaker != null) speaker.enqueue(pcm);
    }

    private synchronized void stopSpeaker() { DownlinkPlayer old = speaker; speaker = null; if (old != null) try { old.close(); } catch (Exception ignored) { } }

    private synchronized void ensureResponseTranscriber() {
        if (!prefs().getBoolean("show_transcript", false) || voskModel == null || responseTranscriber != null) return;
        try { responseTranscriber = new ResponseTranscriber(voskModel, text -> handler.post(new Runnable() { @Override public void run() { if (overlay != null) overlay.setTranscript(text); } })); }
        catch (Exception e) { responseTranscriber = null; }
    }

    private synchronized void stopResponseTranscriber() {
        if (responseTranscriber != null) { try { responseTranscriber.close(); } catch (Exception ignored) { } responseTranscriber = null; }
        if (overlay != null) overlay.clearTranscript();
    }

    private void armConversationTimeout() { handler.removeCallbacks(conversationTimeoutRunnable); int seconds = prefs().getInt("conversation_timeout", 300); if (seconds > 0) handler.postDelayed(conversationTimeoutRunnable, seconds * 1000L); }

    private boolean sendText(String text) {
        WebSocket current;
        long generation;
        boolean currentConnected;
        synchronized (socketLock) { current = socket; generation = socketGeneration; currentConnected = connected; }
        boolean ok = false;
        try { if (currentConnected && current != null) ok = current.send(text); }
        catch (Exception e) { AndroidDebugLog.log("WS v2 send error: " + e); }
        AndroidDebugLog.log("WS v2 -> " + text + " · sent=" + ok);
        if (!ok && currentConnected && current != null) failCurrentTransport(current, generation, "send_failed");
        return ok;
    }

    private void broadcastStatus() {
        long now = System.currentTimeMillis();
        long wakeAge = lastWakeAudioMs <= 0 ? -1 : Math.max(0, now - lastWakeAudioMs);
        long micAge = lastMicAudioMs <= 0 ? -1 : Math.max(0, now - lastMicAudioMs);
        boolean wakeHealthy = connected && "idle".equals(serverState) && wakeRunning.get() && (Build.VERSION.SDK_INT > 23 || (wakeAge >= 0 && wakeAge < AUDIO_HEARTBEAT_STALE_MS));
        boolean micHealthy = connected && "listening".equals(serverState) && micRunning.get() && micAge >= 0 && micAge < MIC_HEARTBEAT_STALE_MS;
        Intent status = new Intent(ACTION_STATUS); status.setPackage(getPackageName());
        status.putExtra(EXTRA_CONNECTED, connected); status.putExtra(EXTRA_CONNECTING, connecting); status.putExtra(EXTRA_SERVER_STATE, serverState);
        status.putExtra(EXTRA_WAKE_RUNNING, wakeRunning.get()); status.putExtra(EXTRA_WAKE_HEALTHY, wakeHealthy); status.putExtra(EXTRA_WAKE_RMS, lastWakeRms); status.putExtra(EXTRA_WAKE_AUDIO_AGE, wakeAge);
        status.putExtra(EXTRA_MIC_RUNNING, micRunning.get()); status.putExtra(EXTRA_MIC_HEALTHY, micHealthy); status.putExtra(EXTRA_REVISION, serverRevision); status.putExtra(EXTRA_SESSION_ID, sessionId); sendBroadcast(status);
    }

    private String wakeWord() { String value = normalize(prefs().getString("wake_word", "hola sol")); return value.isEmpty() ? "hola sol" : value; }
    private String wakeGrammar() { String word = wakeWord(); List<String> variants = new ArrayList<>(); variants.add(word); String[] parts = word.split(" "); if (parts.length == 2) { variants.add(parts[0]); variants.add(parts[1]); } variants.add("[unk]"); StringBuilder b = new StringBuilder("["); for (int i = 0; i < variants.size(); i++) { if (i > 0) b.append(','); b.append('"').append(variants.get(i).replace("\\", "\\\\").replace("\"", "\\\"")).append('"'); } return b.append(']').toString(); }
    private static boolean wakeMatches(String text, int sensitivity, String target) { if (text.equals(target) || text.contains(target)) return true; int distance = levenshtein(text, target); if (sensitivity >= 80) return distance <= Math.max(2, target.length() / 5); if (sensitivity >= 45) return distance <= Math.max(1, target.length() / 8); return false; }
    private List<String> endPhrases() { String raw = prefs().getString("end_phrases", "gracias sol,chau sol,adiós sol,listo sol"); List<String> out = new ArrayList<>(); if (raw != null) for (String value : raw.split(",")) { String normalized = normalize(value); if (!normalized.isEmpty() && !out.contains(normalized)) out.add(normalized); } return out; }
    private String endGrammar(List<String> phrases) { StringBuilder b = new StringBuilder("["); for (int i = 0; i < phrases.size(); i++) { if (i > 0) b.append(','); b.append('"').append(phrases.get(i).replace("\\", "\\\\").replace("\"", "\\\"")).append('"'); } if (!phrases.isEmpty()) b.append(','); return b.append("\"[unk]\"]").toString(); }
    public static double endPhraseMinConfidence(int sensitivity) { sensitivity = Math.max(0, Math.min(100, sensitivity)); return 0.99 - (0.44 * sensitivity / 100.0); }
    private String matchedEndPhrase(String json, List<String> phrases, int sensitivity) { try { JSONObject o = new JSONObject(json); String text = normalize(o.optString("text", "")); if (text.isEmpty()) return null; String matched = null; for (String phrase : phrases) { if (text.equals(phrase) || text.endsWith(" " + phrase)) { matched = phrase; break; } } if (matched == null) return null; JSONArray words = o.optJSONArray("result"); double sum = 0.0; int count = 0; if (words != null) for (int i = 0; i < words.length(); i++) { JSONObject w = words.optJSONObject(i); if (w != null && w.has("conf")) { sum += w.optDouble("conf", 0.0); count++; } } double confidence = count > 0 ? sum / count : 0.0; return confidence >= endPhraseMinConfidence(sensitivity) ? matched : null; } catch (Exception e) { return null; } }
    private AudioRecord createCapture(int sampleRate, int bufferBytes, int source) { try { int min = AudioRecord.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT); if (min <= 0) return null; AudioRecord record = new AudioRecord(source, sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min, bufferBytes)); if (record.getState() != AudioRecord.STATE_INITIALIZED) { record.release(); return null; } return record; } catch (Exception e) { AndroidDebugLog.log("AudioRecord v2 create failed · rate=" + sampleRate + " · source=" + source + " · " + e); return null; } }
    private static int pcmRms(byte[] data, int count) { long sum = 0; int samples = 0; for (int i = 0; i + 1 < count; i += 2) { int sample = (short)((data[i] & 0xff) | (data[i + 1] << 8)); sum += (long)sample * sample; samples++; } return samples == 0 ? 0 : (int)Math.sqrt(sum / (double)samples); }
    private static boolean isThreadAlive(Thread thread) { return thread != null && thread.isAlive(); }
    private static String normalize(String value) { if (value == null) return ""; return Normalizer.normalize(value.toLowerCase(Locale.ROOT), Normalizer.Form.NFD).replaceAll("\\p{InCombiningDiacriticalMarks}+", "").replaceAll("[^a-z0-9ñ ]", " ").trim().replaceAll("\\s+", " "); }
    private static int levenshtein(String a, String b) { int[] prev = new int[b.length() + 1]; for (int j = 0; j <= b.length(); j++) prev[j] = j; for (int i = 1; i <= a.length(); i++) { int[] cur = new int[b.length() + 1]; cur[0] = i; for (int j = 1; j <= b.length(); j++) cur[j] = Math.min(Math.min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a.charAt(i - 1) == b.charAt(j - 1) ? 0 : 1)); prev = cur; } return prev[b.length()]; }
    private static String jsonEscape(String value) { if (value == null) return ""; return value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", " ").replace("\r", " "); }
    public static int sampleRateForQuality(int quality) { if (quality >= 67) return 48000; if (quality >= 34) return 24000; return 16000; }
    public static int chunkMsForLatency(int latency) { latency = Math.max(0, Math.min(100, latency)); int raw = 20 + ((100 - latency) * 60 / 100); return Math.max(20, Math.min(80, ((raw + 5) / 10) * 10)); }
    public static int audioSourceForKey(String key) { if ("voice_communication".equals(key)) return MediaRecorder.AudioSource.VOICE_COMMUNICATION; if ("mic".equals(key)) return MediaRecorder.AudioSource.MIC; if ("default".equals(key)) return MediaRecorder.AudioSource.DEFAULT; if ("camcorder".equals(key)) return MediaRecorder.AudioSource.CAMCORDER; return MediaRecorder.AudioSource.VOICE_RECOGNITION; }
    public static String audioSourceName(int source) { if (source == MediaRecorder.AudioSource.VOICE_COMMUNICATION) return "voice_communication"; if (source == MediaRecorder.AudioSource.MIC) return "mic"; if (source == MediaRecorder.AudioSource.DEFAULT) return "default"; if (source == MediaRecorder.AudioSource.CAMCORDER) return "camcorder"; return "voice_recognition"; }
    public static void applyGainPcm16InPlace(byte[] data, int count, int gainPct) { gainPct = Math.max(50, Math.min(400, gainPct)); if (gainPct == 100 || data == null) return; for (int i = 0; i + 1 < count; i += 2) { int sample = (short)((data[i] & 0xff) | (data[i + 1] << 8)); int amplified = (sample * gainPct) / 100; if (amplified > 32767) amplified = 32767; else if (amplified < -32768) amplified = -32768; data[i] = (byte)(amplified & 0xff); data[i + 1] = (byte)((amplified >> 8) & 0xff); } }

    @SuppressWarnings("deprecation") private void wakeScreenIfEnabled() { if (!prefs().getBoolean("wake_screen_on", true)) return; try { PowerManager pm = (PowerManager)getSystemService(POWER_SERVICE); if (pm == null) return; boolean interactive = Build.VERSION.SDK_INT >= 20 ? pm.isInteractive() : pm.isScreenOn(); if (interactive) return; PowerManager.WakeLock lock = pm.newWakeLock(PowerManager.SCREEN_BRIGHT_WAKE_LOCK | PowerManager.ACQUIRE_CAUSES_WAKEUP | PowerManager.ON_AFTER_RELEASE, "codexremote:wake-screen"); lock.setReferenceCounted(false); lock.acquire(3000L); } catch (Exception e) { AndroidDebugLog.log("Wake screen error: " + e); } }
    private SharedPreferences prefs() { return getSharedPreferences("settings", MODE_PRIVATE); }
    private void createChannel() { if (Build.VERSION.SDK_INT >= 26) { NotificationChannel channel = new NotificationChannel(CHANNEL_ID, "Codex Audio Remote", NotificationManager.IMPORTANCE_LOW); getSystemService(NotificationManager.class).createNotificationChannel(channel); } }
    private Notification notification(String text) { Notification.Builder b = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL_ID) : new Notification.Builder(this); b.setContentTitle("Codex Audio Remote").setContentText(text).setSmallIcon(android.R.drawable.stat_sys_headset).setOngoing(true); return b.build(); }
    private void updateNotification(String text) { try { ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).notify(NOTIFICATION_ID, notification(text)); } catch (Exception ignored) { } }

    @Override public void onDestroy() {
        destroyed = true;
        handler.removeCallbacksAndMessages(null);
        disconnectTransport("service_destroyed");
        stopWakeCapture("service_destroyed");
        stopConversationMic("service_destroyed");
        stopSpeaker();
        stopResponseTranscriber();
        if (overlay != null) overlay.destroy();
        if (wakeReceiverRegistered) { try { unregisterReceiver(wakeWordReceiver); } catch (Exception ignored) { } wakeReceiverRegistered = false; }
        if (voskModel != null) { try { voskModel.close(); } catch (Exception ignored) { } voskModel = null; }
        if (serviceWakeLock != null && serviceWakeLock.isHeld()) { try { serviceWakeLock.release(); } catch (Exception ignored) { } }
        if (client != null) { try { client.dispatcher().executorService().shutdown(); } catch (Exception ignored) { } }
        AndroidDebugLog.log("RemoteService v2 destroyed");
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
    @Override public void onPartialResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }
    @Override public void onResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }
    @Override public void onFinalResult(String hypothesis) { checkWake(hypothesis); lastWakeAudioMs = System.currentTimeMillis(); }
    @Override public void onError(Exception exception) { AndroidDebugLog.log("Vosk v2 listener error: " + exception); stopWakeCapture("vosk_error"); handler.postDelayed(this::reconcileAudioPolicy, 250L); }
    @Override public void onTimeout() { AndroidDebugLog.log("Vosk v2 listener timeout"); }
}