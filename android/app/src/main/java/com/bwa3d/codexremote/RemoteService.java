package com.bwa3d.codexremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.content.SharedPreferences;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;

import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;
import org.vosk.android.RecognitionListener;
import org.vosk.android.SpeechService;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import okio.ByteString;

public class RemoteService extends Service implements RecognitionListener {
    public static final String ACTION_START = "com.bwa3d.codexremote.START";
    public static final String ACTION_WAKE = "com.bwa3d.codexremote.WAKE";
    private static final int NOTIFICATION_ID = 42;
    private static final String CHANNEL_ID = "codex_remote";
    private static final String MODEL_URL = "https://alphacephei.com/vosk/models/vosk-model-small-es-0.42.zip";
    private static final int WAKE_SAMPLE_RATE = 16000;

    private final AtomicBoolean streaming = new AtomicBoolean(false);
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ArrayBlockingQueue<byte[]> phraseQueue = new ArrayBlockingQueue<>(24);
    private OkHttpClient client;
    private WebSocket socket;
    private SpeechService speechService;
    private Model voskModel;
    private Thread audioThread;
    private Thread phraseThread;
    private AudioTrack speaker;
    private OverlayController overlay;
    private String serverIp;
    private int serverPort = 8765;
    private boolean connected;
    private boolean destroyed;
    private boolean modelLoading;
    private boolean endingSession;
    private long lastWakeMs;
    private String lastPartial = "";
    private long lastPartialMs;

    @Override public void onCreate() {
        super.onCreate();
        createChannel();
        overlay = new OverlayController(this);
        startForeground(NOTIFICATION_ID, notification("Iniciando…"));
        client = new OkHttpClient.Builder().readTimeout(0, TimeUnit.MILLISECONDS).pingInterval(15, TimeUnit.SECONDS).build();
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) { startFromSavedSettings(); return START_STICKY; }
        String action = intent.getAction();
        if (ACTION_START.equals(action)) {
            SharedPreferences p = prefs();
            serverIp = intent.getStringExtra("ip");
            serverPort = intent.getIntExtra("port", 8765);
            if (serverIp == null || serverIp.trim().isEmpty()) serverIp = p.getString("ip", "192.168.1.100");
            connect(); initVosk();
        } else if (ACTION_WAKE.equals(action)) triggerWake();
        return START_STICKY;
    }

    private void startFromSavedSettings() {
        serverIp = prefs().getString("ip", "192.168.1.100");
        serverPort = prefs().getInt("port", 8765);
        connect(); initVosk();
    }

    private SharedPreferences prefs() { return getSharedPreferences("settings", MODE_PRIVATE); }

    private synchronized void connect() {
        if (destroyed || serverIp == null) return;
        if (socket != null) try { socket.cancel(); } catch (Exception ignored) { }
        connected = false;
        updateNotification("Conectando a " + serverIp + "…");
        socket = client.newWebSocket(new Request.Builder().url("ws://" + serverIp + ":" + serverPort + "/ws/").build(), new WebSocketListener() {
            @Override public void onOpen(WebSocket webSocket, Response response) {
                connected = true; socket = webSocket;
                sendText("{\"type\":\"hello\",\"name\":\"Android satellite\"}");
                updateNotification(voskModel != null ? "Conectado · hola sol" : "Conectado · preparando wake");
                startWakeRecognition();
            }
            @Override public void onMessage(WebSocket webSocket, String text) { handleServerMessage(text); }
            @Override public void onMessage(WebSocket webSocket, ByteString bytes) { playDownlink(bytes.toByteArray()); }
            @Override public void onClosed(WebSocket webSocket, int code, String reason) { connected = false; scheduleReconnect(); }
            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) { connected = false; updateNotification("Sin conexión · reintentando…"); scheduleReconnect(); }
        });
    }

    private void scheduleReconnect() {
        if (destroyed) return;
        handler.removeCallbacks(reconnectRunnable);
        handler.postDelayed(reconnectRunnable, 2500);
    }
    private final Runnable reconnectRunnable = () -> { if (!destroyed && !connected) connect(); };

    private void handleServerMessage(String text) {
        try {
            String type = new JSONObject(text).optString("type", "");
            switch (type) {
                case "activating": stopWakeRecognition(); overlay.show("Activando…"); updateNotification("Activando Codex…"); break;
                case "codex_listening":
                    endingSession = false; stopWakeRecognition(); startSpeaker(); startMicStreaming(); armConversationTimeout();
                    overlay.show("Escuchando"); updateNotification("Codex escuchando"); break;
                case "session_ending": endingSession = true; overlay.show("Finalizando…"); updateNotification("Finalizando conversación…"); break;
                case "codex_idle": finishLocalSession(); break;
                case "activation_failed":
                case "audio_error": finishLocalSession(); updateNotification("Codex no respondió"); break;
                case "downlink_start": startSpeaker(); break;
            }
        } catch (Exception ignored) { }
    }

    private void finishLocalSession() {
        handler.removeCallbacks(conversationTimeoutRunnable);
        endingSession = false; stopMicStreaming(); stopSpeaker(); overlay.hide(); startWakeRecognition();
        updateNotification("Conectado · hola sol");
    }

    private void armConversationTimeout() {
        handler.removeCallbacks(conversationTimeoutRunnable);
        int seconds = prefs().getInt("conversation_timeout", 300);
        if (seconds > 0) handler.postDelayed(conversationTimeoutRunnable, seconds * 1000L);
    }
    private final Runnable conversationTimeoutRunnable = () -> requestEndSession("timeout");

    private void requestEndSession(String reason) {
        if (!streaming.get() || endingSession) return;
        endingSession = true; handler.removeCallbacks(conversationTimeoutRunnable);
        sendText("{\"type\":\"end_session\",\"reason\":\"" + reason + "\"}");
        overlay.show("Finalizando…"); updateNotification("Finalizando conversación…");
    }

    private void initVosk() {
        if (voskModel != null || modelLoading) return;
        File modelDir = new File(getFilesDir(), "vosk-es-small");
        if (new File(modelDir, "am").exists()) { loadModel(modelDir); return; }
        modelLoading = true;
        new Thread(() -> {
            File zip = new File(getCacheDir(), "vosk-es.zip");
            try {
                updateNotification("Descargando wake model (~39 MB)…");
                downloadFile(MODEL_URL, zip);
                if (modelDir.exists()) deleteRecursive(modelDir);
                modelDir.mkdirs(); unzipStripRoot(zip, modelDir); loadModel(modelDir);
            } catch (Exception e) { updateNotification("Wake manual · error descargando modelo"); }
            finally { modelLoading = false; if (zip.exists()) zip.delete(); }
        }, "VoskModelSetup").start();
    }

    private void loadModel(File dir) {
        try { voskModel = new Model(dir.getAbsolutePath()); updateNotification(connected ? "Conectado · hola sol" : "Wake listo · esperando PC"); startWakeRecognition(); }
        catch (Exception e) { updateNotification("Wake model inválido"); }
    }

    private static void downloadFile(String urlString, File out) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(urlString).openConnection();
        c.setConnectTimeout(15000); c.setReadTimeout(30000); c.setInstanceFollowRedirects(true);
        try (BufferedInputStream in = new BufferedInputStream(c.getInputStream()); FileOutputStream fos = new FileOutputStream(out)) {
            byte[] b = new byte[32768]; int n; while ((n = in.read(b)) > 0) fos.write(b, 0, n);
        } finally { c.disconnect(); }
    }

    private static void unzipStripRoot(File zip, File dest) throws Exception {
        String root = dest.getCanonicalPath() + File.separator;
        try (ZipInputStream zin = new ZipInputStream(new BufferedInputStream(new java.io.FileInputStream(zip)))) {
            ZipEntry e; byte[] b = new byte[32768];
            while ((e = zin.getNextEntry()) != null) {
                String name = e.getName(); int slash = name.indexOf('/'); if (slash >= 0) name = name.substring(slash + 1);
                if (name.isEmpty()) continue;
                File f = new File(dest, name);
                if (!f.getCanonicalPath().startsWith(root)) throw new SecurityException("Bad zip path");
                if (e.isDirectory()) { f.mkdirs(); continue; }
                File parent = f.getParentFile(); if (parent != null) parent.mkdirs();
                try (FileOutputStream target = new FileOutputStream(f)) { int n; while ((n = zin.read(b)) > 0) target.write(b, 0, n); }
            }
        }
    }

    private static void deleteRecursive(File f) {
        if (f.isDirectory()) { File[] children = f.listFiles(); if (children != null) for (File c : children) deleteRecursive(c); }
        f.delete();
    }

    private synchronized void startWakeRecognition() {
        if (!connected || voskModel == null || speechService != null || streaming.get()) return;
        try {
            Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, "[\"hola sol\",\"ola sol\",\"hola so\",\"hola\",\"sol\",\"[unk]\"]");
            speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE); speechService.startListening(this);
        } catch (Exception e) { updateNotification("Vosk error: " + e.getClass().getSimpleName()); }
    }

    private synchronized void stopWakeRecognition() {
        if (speechService != null) { speechService.stop(); speechService.shutdown(); speechService = null; }
    }

    private void checkWake(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String text = normalize(o.optString("text", o.optString("partial", "")));
            if (text.isEmpty()) return;
            int sensitivity = prefs().getInt("sensitivity", 60);
            boolean match = wakeMatches(text, sensitivity);
            long now = System.currentTimeMillis();
            if (!match && sensitivity >= 75) {
                if (text.equals("hola")) { lastPartial = text; lastPartialMs = now; }
                else if (text.equals("sol") && lastPartial.equals("hola") && now - lastPartialMs < 1500) match = true;
            }
            if (match && now - lastWakeMs > 2500) { lastWakeMs = now; triggerWake(); }
        } catch (Exception ignored) { }
    }

    private void triggerWake() {
        if (!connected) { updateNotification("Sin conexión · reintentando…"); scheduleReconnect(); return; }
        stopWakeRecognition(); sendText("{\"type\":\"wake\"}"); overlay.show("Activando…"); updateNotification("Hola Sol detectado · activando…");
    }

    private static boolean wakeMatches(String text, int sensitivity) {
        if (text.contains("hola sol")) return true;
        int d = levenshtein(text, "hola sol");
        if (sensitivity >= 80) return d <= 2;
        if (sensitivity >= 45) return d <= 1;
        return false;
    }

    private List<String> endPhrases() {
        String raw = prefs().getString("end_phrases", "gracias sol,chau sol,adiós sol,listo sol");
        List<String> out = new ArrayList<>();
        if (raw != null) for (String s : raw.split(",")) { String n = normalize(s); if (!n.isEmpty() && !out.contains(n)) out.add(n); }
        return out;
    }

    private String endGrammar(List<String> phrases) {
        StringBuilder b = new StringBuilder("[");
        for (int i = 0; i < phrases.size(); i++) { if (i > 0) b.append(','); b.append('"').append(phrases.get(i).replace("\\", "\\\\").replace("\"", "\\\"")).append('"'); }
        if (!phrases.isEmpty()) b.append(','); b.append("\"[unk]\"]"); return b.toString();
    }

    private boolean isEndPhrase(String json, List<String> phrases) {
        try {
            JSONObject o = new JSONObject(json); String text = normalize(o.optString("text", o.optString("partial", "")));
            if (text.isEmpty()) return false;
            for (String phrase : phrases) if (text.equals(phrase) || text.endsWith(" " + phrase)) return true;
        } catch (Exception ignored) { }
        return false;
    }

    private static String normalize(String s) { return s.toLowerCase(Locale.ROOT).trim().replaceAll("\\s+", " "); }

    private static int levenshtein(String a, String b) {
        int[] prev = new int[b.length()+1]; for (int j=0;j<=b.length();j++) prev[j]=j;
        for (int i=1;i<=a.length();i++) {
            int[] cur = new int[b.length()+1]; cur[0]=i;
            for (int j=1;j<=b.length();j++) cur[j]=Math.min(Math.min(cur[j-1]+1, prev[j]+1), prev[j-1]+(a.charAt(i-1)==b.charAt(j-1)?0:1));
            prev=cur;
        }
        return prev[b.length()];
    }

    public static int sampleRateForQuality(int quality) {
        if (quality >= 67) return 48000;
        if (quality >= 34) return 24000;
        return 16000;
    }

    public static int chunkMsForLatency(int latency) {
        latency = Math.max(0, Math.min(100, latency));
        int raw = 20 + ((100 - latency) * 60 / 100);
        return Math.max(20, Math.min(80, ((raw + 5) / 10) * 10));
    }

    public static int audioSourceForKey(String key) {
        if ("voice_communication".equals(key)) return MediaRecorder.AudioSource.VOICE_COMMUNICATION;
        if ("mic".equals(key)) return MediaRecorder.AudioSource.MIC;
        if ("default".equals(key)) return MediaRecorder.AudioSource.DEFAULT;
        if ("camcorder".equals(key)) return MediaRecorder.AudioSource.CAMCORDER;
        return MediaRecorder.AudioSource.VOICE_RECOGNITION;
    }

    public static String audioSourceName(int source) {
        if (source == MediaRecorder.AudioSource.VOICE_COMMUNICATION) return "Llamada";
        if (source == MediaRecorder.AudioSource.MIC) return "Mic normal";
        if (source == MediaRecorder.AudioSource.DEFAULT) return "Default";
        if (source == MediaRecorder.AudioSource.CAMCORDER) return "Camcorder";
        return "Reconocimiento";
    }

    public static void applyGainPcm16InPlace(byte[] data, int count, int gainPct) {
        gainPct = Math.max(50, Math.min(400, gainPct));
        if (gainPct == 100 || data == null) return;
        for (int i = 0; i + 1 < count; i += 2) {
            int sample = (short)((data[i] & 0xff) | (data[i + 1] << 8));
            int amplified = (sample * gainPct) / 100;
            if (amplified > 32767) amplified = 32767;
            else if (amplified < -32768) amplified = -32768;
            data[i] = (byte)(amplified & 0xff);
            data[i + 1] = (byte)((amplified >> 8) & 0xff);
        }
    }

    private void startPhraseDetector(final int sourceRate) {
        if (voskModel == null || (phraseThread != null && phraseThread.isAlive())) return;
        final List<String> phrases = endPhrases();
        if (phrases.isEmpty()) return;
        phraseQueue.clear();
        phraseThread = new Thread(() -> {
            Recognizer recognizer = null;
            try {
                recognizer = new Recognizer(voskModel, sourceRate, endGrammar(phrases));
                while (streaming.get() || !phraseQueue.isEmpty()) {
                    byte[] chunk = phraseQueue.poll(100, TimeUnit.MILLISECONDS);
                    if (chunk == null) continue;
                    boolean finalChunk = recognizer.acceptWaveForm(chunk, chunk.length);
                    String result = finalChunk ? recognizer.getResult() : recognizer.getPartialResult();
                    if (!endingSession && isEndPhrase(result, phrases)) requestEndSession("phrase");
                }
            } catch (Exception ignored) { }
            finally { if (recognizer != null) recognizer.close(); phraseQueue.clear(); phraseThread = null; }
        }, "EndPhraseVosk");
        phraseThread.setPriority(Math.max(Thread.MIN_PRIORITY, Thread.NORM_PRIORITY - 1)); phraseThread.start();
    }

    private void offerPhraseAudio(byte[] buffer, int read) {
        if (phraseThread == null || !phraseThread.isAlive() || endingSession) return;
        byte[] copy = Arrays.copyOf(buffer, read);
        if (!phraseQueue.offer(copy)) { phraseQueue.poll(); phraseQueue.offer(copy); }
    }

    private AudioRecord createCapture(int sampleRate, int bufferBytes, int audioSource) {
        try {
            int min = AudioRecord.getMinBufferSize(sampleRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (min <= 0) return null;
            AudioRecord record = new AudioRecord(audioSource, sampleRate,
                    AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, Math.max(min, bufferBytes));
            if (record.getState() != AudioRecord.STATE_INITIALIZED) { record.release(); return null; }
            return record;
        } catch (Exception e) { return null; }
    }

    private void startMicStreaming() {
        if (!streaming.compareAndSet(false, true)) return;
        final int quality = prefs().getInt("audio_quality", 80);
        final int latency = prefs().getInt("audio_latency", 55);
        final boolean manualLatency = prefs().getBoolean("manual_latency", true);
        final int requestedManualMs = prefs().getInt("manual_chunk_ms", 45);
        final int chunkMs = manualLatency ? Math.max(20, Math.min(120, requestedManualMs)) : chunkMsForLatency(latency);
        final int requestedRate = sampleRateForQuality(quality);
        final int gainPct = Math.max(50, Math.min(400, prefs().getInt("mic_gain_pct", 100)));
        final String sourceKey = prefs().getString("audio_source", "voice_recognition");
        final int requestedSource = audioSourceForKey(sourceKey);
        final String enhancerMode = prefs().getString("voice_enhancer", VoiceEnhancer.OFF);
        final boolean nativeNs = prefs().getBoolean("native_ns", false);
        final boolean nativeAgc = prefs().getBoolean("native_agc", false);
        final boolean nativeAec = prefs().getBoolean("native_aec", false);

        audioThread = new Thread(() -> {
            AudioRecord record = null;
            NativeAudioEffects effects = null;
            try {
                int actualRate = requestedRate;
                int actualSource = requestedSource;
                int chunkBytes = actualRate * 2 * chunkMs / 1000;
                int safetyChunks = quality >= 75 ? 6 : quality >= 40 ? 5 : 4;
                record = createCapture(actualRate, chunkBytes * safetyChunks, actualSource);

                if (record == null && actualRate != 16000) {
                    actualRate = 16000;
                    chunkBytes = actualRate * 2 * chunkMs / 1000;
                    record = createCapture(actualRate, chunkBytes * safetyChunks, actualSource);
                }
                if (record == null && actualSource != MediaRecorder.AudioSource.VOICE_RECOGNITION) {
                    actualSource = MediaRecorder.AudioSource.VOICE_RECOGNITION;
                    actualRate = requestedRate;
                    chunkBytes = actualRate * 2 * chunkMs / 1000;
                    record = createCapture(actualRate, chunkBytes * safetyChunks, actualSource);
                    if (record == null && actualRate != 16000) {
                        actualRate = 16000;
                        chunkBytes = actualRate * 2 * chunkMs / 1000;
                        record = createCapture(actualRate, chunkBytes * safetyChunks, actualSource);
                    }
                }
                if (record == null) throw new IllegalStateException("No compatible AudioRecord format");

                final int finalRate = actualRate;
                final int finalChunkBytes = chunkBytes;
                final int finalSource = actualSource;
                effects = new NativeAudioEffects(record.getAudioSessionId(), nativeNs, nativeAgc, nativeAec);
                VoiceEnhancer enhancer = new VoiceEnhancer(enhancerMode, finalRate);
                final String effectSummary = effects.summary();
                String captureName = audioSourceName(finalSource).replace(" ", "_").toLowerCase(Locale.ROOT);
                sendText("{\"type\":\"audio_start\",\"sampleRate\":" + finalRate + ",\"channels\":1,\"chunkMs\":" + chunkMs + ",\"quality\":" + quality + ",\"latency\":" + latency + ",\"manualLatency\":" + manualLatency + ",\"gainPct\":" + gainPct + ",\"enhancer\":\"" + enhancerMode + "\",\"capture\":\"" + captureName + "\"}");
                startPhraseDetector(finalRate);
                updateNotification("Codex escuchando · " + audioSourceName(finalSource) + " · " + (finalRate / 1000) + " kHz · gain " + gainPct + "% · enh " + enhancerMode + " · " + effectSummary);

                byte[] buffer = new byte[finalChunkBytes];
                record.startRecording();
                while (streaming.get()) {
                    int read = record.read(buffer, 0, buffer.length);
                    if (read > 0 && socket != null) {
                        applyGainPcm16InPlace(buffer, read, gainPct);
                        enhancer.processInPlace(buffer, read);
                        socket.send(ByteString.of(buffer, 0, read));
                        overlay.setLevel(rmsPcm16(buffer, read));
                        offerPhraseAudio(buffer, read);
                    }
                }
            } catch (Exception e) { updateNotification("Audio error: " + e.getClass().getSimpleName()); }
            finally {
                if (effects != null) effects.close();
                if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); }
            }
        }, "MicUplink");
        audioThread.setPriority(Thread.MAX_PRIORITY); audioThread.start();
    }

    private void stopMicStreaming() {
        if (!streaming.compareAndSet(true, false)) return;
        phraseQueue.clear(); sendText("{\"type\":\"audio_stop\"}");
    }

    private synchronized void startSpeaker() {
        if (speaker != null) return;
        int latency = prefs().getInt("audio_latency", 55);
        int min = AudioTrack.getMinBufferSize(WAKE_SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
        int buffer;
        if (latency >= 80) buffer = Math.max(min, 2048);
        else if (latency >= 45) buffer = Math.max(min * 2, 4096);
        else buffer = Math.max(min * 3, 6144);
        speaker = new AudioTrack(AudioManager.STREAM_MUSIC, WAKE_SAMPLE_RATE, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT, buffer, AudioTrack.MODE_STREAM);
        speaker.play();
    }

    private synchronized void playDownlink(byte[] pcm) {
        if (speaker == null) startSpeaker();
        if (speaker == null || pcm.length == 0) return;
        speaker.write(pcm, 0, pcm.length);
        overlay.show("Sol hablando"); overlay.setLevel(rmsPcm16(pcm, pcm.length));
        handler.removeCallbacks(backToListening); handler.postDelayed(backToListening, 180);
    }

    private final Runnable backToListening = () -> { if (streaming.get() && !endingSession) overlay.show("Escuchando"); };

    private synchronized void stopSpeaker() {
        if (speaker == null) return;
        try { speaker.pause(); speaker.flush(); speaker.stop(); } catch (Exception ignored) { }
        speaker.release(); speaker = null;
    }

    private static float rmsPcm16(byte[] data, int count) {
        if (count < 2) return 0.05f;
        double sum = 0; int n = 0;
        for (int i=0;i+1<count;i+=2) { int v = (short)((data[i] & 0xff) | (data[i+1] << 8)); double x = v / 32768.0; sum += x*x; n++; }
        return (float)Math.min(1.0, Math.sqrt(sum / Math.max(1,n)) * 5.0);
    }

    private void sendText(String text) { if (socket != null && connected) socket.send(text); }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel c = new NotificationChannel(CHANNEL_ID, "Codex Audio Remote", NotificationManager.IMPORTANCE_LOW);
            getSystemService(NotificationManager.class).createNotificationChannel(c);
        }
    }

    private Notification notification(String text) {
        Notification.Builder b = Build.VERSION.SDK_INT >= 26 ? new Notification.Builder(this, CHANNEL_ID) : new Notification.Builder(this);
        return b.setContentTitle("Codex Audio Remote").setContentText(text).setSmallIcon(android.R.drawable.ic_btn_speak_now).setOngoing(true).build();
    }

    private void updateNotification(String text) {
        handler.post(() -> ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).notify(NOTIFICATION_ID, notification(text)));
    }

    @Override public void onPartialResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onFinalResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onError(Exception exception) { updateNotification("Vosk error"); }
    @Override public void onTimeout() { startWakeRecognition(); }

    @Override public void onDestroy() {
        destroyed = true; handler.removeCallbacksAndMessages(null);
        stopMicStreaming(); stopWakeRecognition(); stopSpeaker(); overlay.hide();
        if (socket != null) socket.close(1000, "service stopped");
        if (client != null) client.dispatcher().executorService().shutdown();
        if (voskModel != null) voskModel.close();
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
