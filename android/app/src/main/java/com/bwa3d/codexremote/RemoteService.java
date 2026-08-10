package com.bwa3d.codexremote;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Build;
import android.os.IBinder;

import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;
import org.vosk.android.RecognitionListener;
import org.vosk.android.SpeechService;
import org.vosk.android.StorageService;

import java.util.Locale;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;

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

    private final AtomicBoolean streaming = new AtomicBoolean(false);
    private OkHttpClient client;
    private WebSocket socket;
    private SpeechService speechService;
    private Model voskModel;
    private Thread audioThread;

    @Override
    public void onCreate() {
        super.onCreate();
        createChannel();
        startForeground(NOTIFICATION_ID, notification("Iniciando…"));
        client = new OkHttpClient.Builder()
                .readTimeout(0, TimeUnit.MILLISECONDS)
                .pingInterval(20, TimeUnit.SECONDS)
                .build();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) return START_STICKY;
        String action = intent.getAction();

        if (ACTION_START.equals(action)) {
            String ip = intent.getStringExtra("ip");
            int port = intent.getIntExtra("port", 8765);
            if (ip == null || ip.trim().isEmpty()) {
                ip = getSharedPreferences("settings", MODE_PRIVATE).getString("ip", "192.168.1.100");
                port = getSharedPreferences("settings", MODE_PRIVATE).getInt("port", 8765);
            }
            connect(ip, port);
            initVoskIfAvailable();
        } else if (ACTION_WAKE.equals(action)) {
            sendText("{\"type\":\"wake\"}");
        }
        return START_STICKY;
    }

    private void connect(String ip, int port) {
        if (socket != null) socket.close(1000, "reconnect");
        updateNotification("Conectando a " + ip + "…");
        Request request = new Request.Builder().url("ws://" + ip + ":" + port + "/ws/").build();
        socket = client.newWebSocket(request, new WebSocketListener() {
            @Override public void onOpen(WebSocket webSocket, Response response) {
                sendText("{\"type\":\"hello\",\"name\":\"Android satellite\"}");
                updateNotification("Conectado · wake activo");
            }

            @Override public void onMessage(WebSocket webSocket, String text) {
                handleServerMessage(text);
            }

            @Override public void onFailure(WebSocket webSocket, Throwable t, Response response) {
                updateNotification("Sin conexión: " + t.getClass().getSimpleName());
            }
        });
    }

    private void handleServerMessage(String text) {
        try {
            String type = new JSONObject(text).optString("type", "");
            switch (type) {
                case "activating":
                    updateNotification("Activando Codex…");
                    break;
                case "codex_listening":
                    updateNotification("Codex escuchando");
                    stopWakeRecognition();
                    startMicStreaming();
                    break;
                case "codex_idle":
                    stopMicStreaming();
                    startWakeRecognition();
                    updateNotification("Conectado · wake activo");
                    break;
                case "activation_failed":
                    stopMicStreaming();
                    startWakeRecognition();
                    updateNotification("Codex no respondió");
                    break;
            }
        } catch (Exception ignored) { }
    }

    private void initVoskIfAvailable() {
        if (voskModel != null || speechService != null) return;
        try {
            StorageService.unpack(this, "model", "model",
                    model -> {
                        voskModel = model;
                        startWakeRecognition();
                    },
                    exception -> updateNotification("Conectado · wake manual (sin modelo Vosk)"));
        } catch (Exception e) {
            updateNotification("Conectado · wake manual (sin modelo Vosk)");
        }
    }

    private synchronized void startWakeRecognition() {
        if (voskModel == null || speechService != null || streaming.get()) return;
        try {
            Recognizer recognizer = new Recognizer(voskModel, 16000.0f,
                    "[\"sol\", \"codex\", \"hey codex\", \"[unk]\"]");
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
        String normalized = json.toLowerCase(Locale.ROOT);
        if (normalized.contains("\"sol\"") || normalized.contains("\"codex\"") || normalized.contains("hey codex")) {
            stopWakeRecognition();
            sendText("{\"type\":\"wake\"}");
            updateNotification("Wake detectado · activando…");
        }
    }

    private void startMicStreaming() {
        if (!streaming.compareAndSet(false, true)) return;
        sendText("{\"type\":\"audio_start\",\"sampleRate\":16000,\"channels\":1}");
        audioThread = new Thread(() -> {
            int min = AudioRecord.getMinBufferSize(16000, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            int size = Math.max(min, 3200);
            AudioRecord record = null;
            try {
                record = new AudioRecord(MediaRecorder.AudioSource.VOICE_RECOGNITION, 16000,
                        AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size * 2);
                byte[] buffer = new byte[size];
                record.startRecording();
                while (streaming.get()) {
                    int read = record.read(buffer, 0, buffer.length);
                    if (read > 0 && socket != null) socket.send(ByteString.of(buffer, 0, read));
                }
            } catch (Exception e) {
                updateNotification("Audio error: " + e.getClass().getSimpleName());
            } finally {
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

    private void sendText(String text) {
        if (socket != null) socket.send(text);
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationChannel c = new NotificationChannel(CHANNEL_ID, "Codex Audio Remote", NotificationManager.IMPORTANCE_LOW);
            getSystemService(NotificationManager.class).createNotificationChannel(c);
        }
    }

    private Notification notification(String text) {
        Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL_ID)
                : new Notification.Builder(this);
        return b.setContentTitle("Codex Audio Remote")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setOngoing(true)
                .build();
    }

    private void updateNotification(String text) {
        ((NotificationManager)getSystemService(NOTIFICATION_SERVICE)).notify(NOTIFICATION_ID, notification(text));
    }

    @Override public void onPartialResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onFinalResult(String hypothesis) { checkWake(hypothesis); }
    @Override public void onError(Exception exception) { updateNotification("Vosk error"); }
    @Override public void onTimeout() { startWakeRecognition(); }

    @Override public void onDestroy() {
        stopMicStreaming();
        stopWakeRecognition();
        if (socket != null) socket.close(1000, "service stopped");
        if (client != null) client.dispatcher().executorService().shutdown();
        if (voskModel != null) voskModel.close();
        super.onDestroy();
    }

    @Override public IBinder onBind(Intent intent) { return null; }
}
