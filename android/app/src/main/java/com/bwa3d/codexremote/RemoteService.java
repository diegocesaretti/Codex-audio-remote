package com.bwa3d.codexremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.media.AcousticEchoCanceler;
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
import java.util.Locale;
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

    private final AtomicBoolean streaming = new AtomicBoolean(false);
    private final Handler handler = new Handler(Looper.getMainLooper());
    private OkHttpClient client;
    private WebSocket socket;
    private SpeechService speechService;
    private Model voskModel;
    private Thread audioThread;
    private AudioTrack speaker;
    private OverlayController overlay;
    private String serverIp;
    private int serverPort = 8765;
    private boolean connected;
    private boolean destroyed;
    private boolean modelLoading;
    private long lastWakeMs;
    private String lastPartial = "";
    private long lastPartialMs;

    @Override public void onCreate() {
        super.onCreate();
        createChannel();
        overlay = new OverlayController(this);
        startForeground(NOTIFICATION_ID, notification("Iniciando…"));
        client = new OkHttpClient.Builder()
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .pingInterval(20, TimeUnit.SECONDS)
                .build();
    }

    @Override public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            startFromSavedSettings();
            return START_STICKY;
        }
        String action = intent.getAction();
        if (ACTION_START.equals(action)) {
            SharedPreferences p = prefs();
            serverIp = intent.getStringExtra("ip");
            serverPort = intent.getIntExtra("port", 8765);
            if (serverIp == null || serverIp.trim().isEmpty()) serverIp = p.getString("ip", "192.168.1.100");
            connect();
            initVosk();
        } else if (ACTION_WAKE.equals(action)) {
            triggerWake();
        }
        return START_STICKY;
    }

    private void startFromSavedSettings() {
        serverIp = prefs().getString("ip", "192.168.1.100");
        serverPort = prefs().getInt("port", 8765);
        connect();
        initVosk();
    }

    private SharedPreferences prefs() { return getSharedPreferences("settings", MODE_PRIVATE); }

    private synchronized void connect() {
        if (destroyed || serverIp == null) return;
        if (socket != null) {
            try { socket.cancel(); } catch (Exception ignored) { }
        }
        connected = false;
        updateNotification("Conectando a " + serverIp + "…");
        Request request = new Request.Builder().url("ws://" + serverIp + ":" + serverPort + "/ws/").build();
        socket = client.newWebSocket(request, new WebSocketListener() {
            @Override public void onOpen(WebSocket webSocket, Response response) {
                connected = true;
                socket = webSocket;
                sendText("{\"type\":\"hello\",\"name\":\"Android satellite\"}");
                updateNotification(voskModel != null ? "Conectado · hola sol" : "Conectado · preparando wake");
                startWakeRecognition();
            }
            @Override public void onMessage(WebSocket webSocket, String text) { handleServerMessage(text); }
            @Override public void onMessage(WebSocket webSocket, ByteString bytes) { playDownlink(bytes.toByteArray()); }
            @Override public void onClosed(WebSocket webSocket, int code, String reason) {
                connected = false;
                scheduleReconnect();
            }
            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                connected = false;
                updateNotification("Sin conexión · reintentando…");
                scheduleReconnect();
            }
        });
    }

    private void scheduleReconnect() {
        if (destroyed) return;
        handler.removeCallbacks(reconnectRunnable);
        handler.postDelayed(reconnectRunnable, 5000);
    }

    private final Runnable reconnectRunnable = () -> {
        if (!destroyed && !connected) connect();
    };

    private void handleServerMessage(String text) {
        try {
            String type = new JSONObject(text).optString("type", "");
            switch (type) {
                case "activating":
                    stopWakeRecognition();
                    overlay.show("Activando…");
                    updateNotification("Activando Codex…");
                    break;
                case "codex_listening":
                    stopWakeRecognition();
                    startSpeaker();
                    startMicStreaming();
                    overlay.show("Escuchando");
                    updateNotification("Codex escuchando");
                    break;
                case "codex_idle":
                    stopMicStreaming();
                    stopSpeaker();
                    overlay.hide();
                    startWakeRecognition();
                    updateNotification("Conectado · hola sol");
                    break;
                case "activation_failed":
                case "audio_error":
                    stopMicStreaming();
                    stopSpeaker();
                    overlay.hide();
                    startWakeRecognition();
                    updateNotification("Codex no respondió");
                    break;
                case "downlink_start":
                    startSpeaker();
                    break;
                case "downlink_stop":
                    break;
            }
        } catch (Exception ignored) { }
    }

    private void initVosk() {
        if (voskModel != null || modelLoading) return;
        File modelDir = new File(getFilesDir(), "vosk-es-small");
        if (new File(modelDir, "am").exists()) {
            loadModel(modelDir);
            return;
        }
        modelLoading = true;
        new Thread(() -> {
            File zip = new File(getCacheDir(), "vosk-es.zip");
            try {
                updateNotification("Descargando wake model (~39 MB)…");
                downloadFile(MODEL_URL, zip);
                if (modelDir.exists()) deleteRecursive(modelDir);
                modelDir.mkdirs();
                unzipStripRoot(zip, modelDir);
                loadModel(modelDir);
            } catch (Exception e) {
                updateNotification("Wake manual · error descargando modelo");
            } finally {
                modelLoading = false;
                if (zip.exists()) zip.delete();
            }
        }, "VoskModelSetup").start();
    }

    private void loadModel(File dir) {
        try {
            voskModel = new Model(dir.getAbsolutePath());
            updateNotification(connected ? "Conectado · hola sol" : "Wake listo · esperando PC");
            startWakeRecognition();
        } catch (Exception e) {
            updateNotification("Wake model inválido");
        }
    }

    private static void downloadFile(String urlString, File out) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(urlString).openConnection();
        c.setConnectTimeout(15000);
        c.setReadTimeout(30000);
        c.setInstanceFollowRedirects(true);
        try (BufferedInputStream in = new BufferedInputStream(c.getInputStream());
             FileOutputStream fos = new FileOutputStream(out)) {
            byte[] b = new byte[32768];
            int n;
            while ((n = in.read(b)) > 0) fos.write(b, 0, n);
        } finally { c.disconnect(); }
    }

    private static void unzipStripRoot(File zip, File dest) throws Exception {
        String root = dest.getCanonicalPath() + File.separator;
        try (ZipInputStream zin = new ZipInputStream(new BufferedInputStream(new java.io.FileInputStream(zip)))) {
            ZipEntry e;
            byte[] b = new byte[32768];
            while ((e = zin.getNextEntry()) != null) {
                String name = e.getName();
                int slash = name.indexOf('/');
                if (slash >= 0) name = name.substring(slash + 1);
                if (name.isEmpty()) continue;
                File f = new File(dest, name);
                if (!f.getCanonicalPath().startsWith(root)) throw new SecurityException("Bad zip path");
                if (e.isDirectory()) { f.mkdirs(); continue; }
                File parent = f.getParentFile();
                if (parent != null) parent.mkdirs();
                try (FileOutputStream out = new FileOutputStream(f)) {
                    int n;
                    while ((n = zin.read(b)) > 0) out.write(b, 0, n);
                }
            }
        }
    }

    private static void deleteRecursive(File f) {
        if (f.isDirectory()) {
            File[] children = f.listFiles();
            if (children != null) for (File c : children) deleteRecursive(c);
        }
        f.delete();
    }

    private synchronized void startWakeRecognition() {
        if (!connected || voskModel == null || speechService != null || streaming.get()) return;
        try {
            Recognizer recognizer = new Recognizer(voskModel, 16000.0f,
                    "[\"hola sol\",\"ola sol\",\"hola so\",\"hola\",\"sol\",\"[unk]\"]");
            speechService = new SpeechService(recognizer, 16000.0f);
            speechService.startListening(this);
        } catch (Exception e) {
            updateNotification("Vosk error: " + e.getClass().getSimpleName());
        }
    }

    private synchronized void stopWakeRecognition() {
        if (speechService != null) {
            speechService.stop();
            speechService.shutdown();
            speechService = null;
        }
    }

    private void checkWake(String json) {
        try {
            JSONObject o = new JSONObject(json);
            String text = o.optString("text", o.optString("partial", ""));
            text = normalize(text);
            if (text.isEmpty()) return;
            int sensitivity = prefs().getInt("sensitivity", 60);
            boolean match = wakeMatches(text, sensitivity);
            long now = System.currentTimeMillis();
            if (!match && sensitivity >= 75) {
                if (text.equals("hola")) { lastPartial = text; lastPartialMs = now; }
                else if (text.equals("sol") && lastPartial.equals("hola") && now - lastPartialMs < 1800) match = true;
            }
            if (match && now - lastWakeMs > 3000) {
                lastWakeMs = now;
                triggerWake();
            }
        } catch (Exception ignored) { }
    }

    private void triggerWake() {
        if (!connected) {
            updateNotification("Sin conexión · reintentando…");
            scheduleReconnect();
            return;
        }
        stopWakeRecognition();
        sendText("{\"type\":\"wake\"}");
        overlay.show("Activando…");
        updateNotification("Hola Sol detectado · activando…");
    }

    private static boolean wakeMatches(String text, int sensitivity) {
        if (text.contains("hola sol")) return true;
        int d = levenshtein(text, "hola sol");
        if (sensitivity >= 80) return d <= 2;
        if (sensitivity >= 45) return d <= 1;
        return false;
    }

    private static String normalize(String s) {
        return s.toLowerCase(Locale.ROOT).trim().replaceAll("\\s+", " ");
    }

    private static int levenshtein(String a, String b) {
        int[] prev = new int[b.length()+1];
        for (int j=0;j<=b.length();j++) prev[j]=j;
        for (int i=1;i<=a.length();i++) {
            int[] cur = new int[b.length()+1]; cur[0]=i;
            for (int j=1;j<=b.length();j++) cur[j]=Math.min(Math.min(cur[j-1]+1, prev[j]+1), prev[j-1]+(a.charAt(i-1)==b.charAt(j-1)?0:1));
            prev=cur;
        }
        return prev[b.length()];
    }

    private void startMicStreaming() {
        if (!streaming.compareAndSet(false, true)) return;
        sendText("{\"type\":\"audio_start\",\"sampleRate\":16000,\"channels\":1}");
        audioThread = new Thread(() -> {
            int min = AudioRecord.getMinBufferSize(16000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            int size = Math.max(min, 3200);
            AudioRecord record = null;
            AcousticEchoCanceler aec = null;
            try {
                record = new AudioRecord(MediaRecorder.AudioSource.VOICE_COMMUNICATION, 16000,
                        AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size * 2);
                if (AcousticEchoCanceler.isAvailable()) {
                    aec = AcousticEchoCanceler.create(record.getAudioSessionId());
                    if (aec != null) aec.setEnabled(true);
                }
                byte[] buffer = new byte[size];
                record.startRecording();
                while (streaming.get()) {
                    int read = record.read(buffer, 0, buffer.length);
                    if (read > 0 && socket != null) {
                        socket.send(ByteString.of(buffer, 0, read));
                        overlay.setLevel(rmsPcm16(buffer, read));
                    }
                }
            } catch (Exception e) {
                updateNotification("Audio error: " + e.getClass().getSimpleName());
            } finally {
                if (aec != null) aec.release();
                if (record != null) {
                    try { record.stop(); } catch (Exception ignored) { }
                    record.release();
                }
            }
        }, "MicUplink");
        audioThread.start();
    }

    private void stopMicStreaming() {
        if (!streaming.compareAndSet(true, false)) return;
        sendText("{\"type\":\"audio_stop\"}");
    }

    private synchronized void startSpeaker() {
        if (speaker != null) return;
        int min = AudioTrack.getMinBufferSize(16000, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
        speaker = new AudioTrack(AudioManager.STREAM_MUSIC, 16000, AudioFormat.CHANNEL_OUT_MONO,
                AudioFormat.ENCODING_PCM_16BIT, Math.max(min * 4, 8192), AudioTrack.MODE_STREAM);
        speaker.play();
    }

    private synchronized void playDownlink(byte[] pcm) {
        if (speaker == null) startSpeaker();
        if (speaker == null || pcm.length == 0) return;
        speaker.write(pcm, 0, pcm.length);
        overlay.show("Sol hablando");
        overlay.setLevel(rmsPcm16(pcm, pcm.length));
        handler.removeCallbacks(backToListening);
        handler.postDelayed(backToListening, 350);
    }

    private final Runnable backToListening = () -> {
        if (streaming.get()) overlay.show("Escuchando");
    };

    private synchronized void stopSpeaker() {
        if (speaker == null) return;
        try { speaker.pause(); speaker.flush(); speaker.stop(); } catch (Exception ignored) { }
        speaker.release();
        speaker = null;
    }

    private static float rmsPcm16(byte[] data, int count) {
        if (count < 2) return 0.05f;
        double sum = 0; int n = 0;
        for (int i=0;i+1<count;i+=2) {
            int v = (short)((data[i] & 0xff) | (data[i+1] << 8));
            double x = v / 32768.0; sum += x*x; n++;
        }
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
        return b.setContentTitle("Codex Audio Remote").setContentText(text)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now).setOngoing(true).build();
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
        destroyed = true;
        handler.removeCallbacksAndMessages(null);
        stopMicStreaming();
        stopWakeRecognition();
        stopSpeaker();
        overlay.hide();
        if (socket != null) socket.close(1000, "service stopped");
        if (client != null) client.dispatcher().executorService().shutdown();
        if (voskModel != null) voskModel.close();
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
