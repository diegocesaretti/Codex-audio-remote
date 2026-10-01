package com.diegocesaretti.solwake;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.ToneGenerator;
import android.media.AudioManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.os.VibrationEffect;
import android.os.Vibrator;

import org.json.JSONArray;
import org.json.JSONObject;
import org.vosk.Model;
import org.vosk.Recognizer;
import org.vosk.android.StorageService;

import java.text.Normalizer;
import java.util.Locale;

public class WakeService extends Service {
    public static final String ACTION_START = "com.diegocesaretti.solwake.START";
    public static final String ACTION_STOP = "com.diegocesaretti.solwake.STOP";
    public static final String ACTION_RELOAD = "com.diegocesaretti.solwake.RELOAD";
    public static final String ACTION_TEST = "com.diegocesaretti.solwake.TEST";

    private static final String CHANNEL = "sol_wake_listener";
    private static final int NOTIFICATION_ID = 1107;

    private static volatile boolean running = false;
    private static volatile String status = "detenido";

    private final Handler main = new Handler(Looper.getMainLooper());

    private SharedPreferences prefs;
    private Model model;
    private Recognizer recognizer;
    private AudioRecord recorder;
    private Thread audioThread;
    private volatile boolean listening = false;
    private long lastTrigger = 0L;
    private PowerManager.WakeLock cpuLock;

    private final BroadcastReceiver conditionsReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) {
            evaluateListeningState();
        }
    };

    public static boolean isRunning() {
        return running;
    }

    public static String getStatus() {
        return status;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        running = true;
        prefs = Prefs.get(this);
        createChannel();
        startForegroundCompat(buildNotification("iniciando modelo..."));

        IntentFilter filter = new IntentFilter();
        filter.addAction(Intent.ACTION_SCREEN_ON);
        filter.addAction(Intent.ACTION_SCREEN_OFF);
        filter.addAction(Intent.ACTION_POWER_CONNECTED);
        filter.addAction(Intent.ACTION_POWER_DISCONNECTED);
        if (Build.VERSION.SDK_INT >= 33) {
            registerReceiver(conditionsReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
        } else {
            registerReceiver(conditionsReceiver, filter);
        }

        loadModel();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = intent == null ? ACTION_START : intent.getAction();

        if (ACTION_STOP.equals(action)) {
            stopSelf();
            return START_NOT_STICKY;
        }

        if (ACTION_TEST.equals(action)) {
            AssistantLauncher.launch(this, prefs);
            return START_STICKY;
        }

        if (ACTION_RELOAD.equals(action)) {
            prefs = Prefs.get(this);
            stopListening();
            evaluateListeningState();
            return START_STICKY;
        }

        prefs = Prefs.get(this);
        evaluateListeningState();
        return START_STICKY;
    }

    private void loadModel() {
        setStatus("preparando modelo Vosk...");
        StorageService.unpack(
                this,
                "model",
                "model",
                loaded -> {
                    model = loaded;
                    setStatus("modelo listo");
                    evaluateListeningState();
                },
                error -> {
                    setStatus("error de modelo: " + shortMessage(error));
                }
        );
    }

    private synchronized void evaluateListeningState() {
        if (model == null) return;

        if (prefs.getBoolean(Prefs.SCREEN_ONLY, true) && !isScreenInteractive()) {
            stopListening();
            setStatus("en espera: pantalla apagada");
            return;
        }

        if (prefs.getBoolean(Prefs.CHARGING_ONLY, false) && !isCharging()) {
            stopListening();
            setStatus("en espera: no está cargando");
            return;
        }

        startListening();
    }

    private synchronized void startListening() {
        if (listening || model == null) return;

        listening = true;
        acquireCpuLock();
        String phrase = Prefs.phrase(prefs);
        setStatus("escuchando: \"" + phrase + "\"");

        audioThread = new Thread(() -> audioLoop(phrase), "SolWake-Audio");
        audioThread.start();
    }

    private void audioLoop(String phrase) {
        AudioRecord localRecorder = null;
        Recognizer localRecognizer = null;

        try {
            JSONArray grammarArray = new JSONArray();
            grammarArray.put(phrase);
            grammarArray.put("[unk]");
            localRecognizer = new Recognizer(model, 16000.0f, grammarArray.toString());
            localRecognizer.setWords(true);
            localRecognizer.setPartialWords(true);
            recognizer = localRecognizer;

            int source = chooseAudioSource();
            int min = AudioRecord.getMinBufferSize(
                    16000,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT);
            if (min < 0) min = 4096;
            int bufferSize = Math.max(8192, min * 2);

            localRecorder = new AudioRecord(
                    source,
                    16000,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSize);

            if (localRecorder.getState() != AudioRecord.STATE_INITIALIZED
                    && source != MediaRecorder.AudioSource.MIC) {
                try { localRecorder.release(); } catch (Throwable ignored) {}
                localRecorder = new AudioRecord(
                        MediaRecorder.AudioSource.MIC,
                        16000,
                        AudioFormat.CHANNEL_IN_MONO,
                        AudioFormat.ENCODING_PCM_16BIT,
                        bufferSize);
            }

            if (localRecorder.getState() != AudioRecord.STATE_INITIALIZED) {
                throw new IllegalStateException("No se pudo inicializar AudioRecord");
            }

            recorder = localRecorder;
            localRecorder.startRecording();
            byte[] buffer = new byte[Math.max(4096, min)];

            while (listening && !Thread.currentThread().isInterrupted()) {
                int n = localRecorder.read(buffer, 0, buffer.length);
                if (n <= 0) continue;

                boolean utteranceDone = localRecognizer.acceptWaveForm(buffer, n);
                if (utteranceDone) {
                    inspectResult(localRecognizer.getResult(), false, phrase, localRecognizer);
                } else {
                    inspectResult(localRecognizer.getPartialResult(), true, phrase, localRecognizer);
                }
            }
        } catch (Throwable t) {
            if (listening) setStatus("error de micrófono/Vosk: " + shortMessage(t));
        } finally {
            try {
                if (localRecorder != null && localRecorder.getRecordingState()
                        == AudioRecord.RECORDSTATE_RECORDING) {
                    localRecorder.stop();
                }
            } catch (Throwable ignored) {}
            try { if (localRecorder != null) localRecorder.release(); } catch (Throwable ignored) {}
            try { if (localRecognizer != null) localRecognizer.close(); } catch (Throwable ignored) {}

            recorder = null;
            recognizer = null;
            if (Thread.currentThread() == audioThread) audioThread = null;
            releaseCpuLock();
        }
    }

    private void inspectResult(
            String json,
            boolean partial,
            String wakePhrase,
            Recognizer localRecognizer) {
        try {
            JSONObject obj = new JSONObject(json);
            String text = obj.optString(partial ? "partial" : "text", "");
            if (text == null || text.trim().isEmpty()) return;

            int sensitivity = prefs.getInt(Prefs.SENSITIVITY, 65);
            if (partial && sensitivity < 60) return;

            String expected = normalize(wakePhrase);
            String heard = normalize(text);
            if (heard.length() < Math.max(2, (int) Math.ceil(expected.length() * 0.70))) return;

            double confidence = extractConfidence(obj, partial);
            double similarity = similarity(expected, heard);
            double score = similarity * 0.78 + confidence * 0.22;
            double threshold = 0.93 - (Math.max(0, Math.min(100, sensitivity)) * 0.0038);

            if (score >= threshold) {
                long now = System.currentTimeMillis();
                int cooldownSeconds = prefs.getInt(Prefs.COOLDOWN, 2);
                if (now - lastTrigger < cooldownSeconds * 1000L) return;

                lastTrigger = now;
                try { localRecognizer.reset(); } catch (Throwable ignored) {}
                onWakeDetected(text, score);
            }
        } catch (Throwable ignored) {
        }
    }

    private void onWakeDetected(String heard, double score) {
        main.post(() -> {
            setStatus(String.format(
                    Locale.US,
                    "detectado (%.0f%%): %s",
                    score * 100.0,
                    heard));

            if (prefs.getBoolean(Prefs.VIBRATE, true)) {
                try {
                    Vibrator vibrator = (Vibrator) getSystemService(VIBRATOR_SERVICE);
                    if (vibrator != null && vibrator.hasVibrator()) {
                        if (Build.VERSION.SDK_INT >= 26) {
                            vibrator.vibrate(VibrationEffect.createOneShot(
                                    80, VibrationEffect.DEFAULT_AMPLITUDE));
                        } else {
                            vibrator.vibrate(80);
                        }
                    }
                } catch (Throwable ignored) {}
            }

            if (prefs.getBoolean(Prefs.BEEP, false)) {
                try {
                    ToneGenerator tg = new ToneGenerator(AudioManager.STREAM_NOTIFICATION, 55);
                    tg.startTone(ToneGenerator.TONE_PROP_ACK, 90);
                    main.postDelayed(tg::release, 180);
                } catch (Throwable ignored) {}
            }

            // ChatGPT also needs the microphone. Release Vosk/AudioRecord before launch.
            stopListening();

            main.postDelayed(() -> {
                boolean launched = AssistantLauncher.launch(this, prefs);
                String detail = AssistantLauncher.getLastDetail();

                if (!launched) {
                    setStatus("no se pudo abrir ChatGPT: " + detail);
                    main.postDelayed(this::evaluateListeningState, 1200L);
                    return;
                }

                setStatus("ChatGPT abierto; micrófono liberado");
                scheduleResumeAfterAssistant();
            }, 350L);
        });
    }

    private void scheduleResumeAfterAssistant() {
        // Give ChatGPT time to acquire the mic, then wait while another recording
        // or communication mode is active. A hard cap avoids getting stuck forever.
        main.postDelayed(new Runnable() {
            int checks = 0;

            @Override public void run() {
                checks++;
                boolean assistantBusy = false;

                try {
                    AudioManager audioManager =
                            (AudioManager) getSystemService(AUDIO_SERVICE);
                    if (audioManager != null) {
                        assistantBusy = audioManager.getMode() != AudioManager.MODE_NORMAL;
                        if (Build.VERSION.SDK_INT >= 24) {
                            assistantBusy = assistantBusy
                                    || !audioManager.getActiveRecordingConfigurations().isEmpty();
                        }
                    }
                } catch (Throwable ignored) {}

                if ((checks < 5 || assistantBusy) && checks < 120) {
                    if (assistantBusy) setStatus("ChatGPT en voz; Sol Wake pausado");
                    main.postDelayed(this, 1000L);
                    return;
                }

                evaluateListeningState();
            }
        }, 1000L);
    }

    private int chooseAudioSource() {
        int selected = prefs.getInt(Prefs.AUDIO_SOURCE, 0);
        if (selected == 1) return MediaRecorder.AudioSource.MIC;
        if (selected == 2 && Build.VERSION.SDK_INT >= 24) {
            return MediaRecorder.AudioSource.UNPROCESSED;
        }
        return MediaRecorder.AudioSource.VOICE_RECOGNITION;
    }

    private boolean isScreenInteractive() {
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            return pm != null && pm.isInteractive();
        } catch (Throwable ignored) {
            return true;
        }
    }

    private boolean isCharging() {
        try {
            Intent battery = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
            if (battery == null) return false;
            int status = battery.getIntExtra(BatteryManager.EXTRA_STATUS, -1);
            return status == BatteryManager.BATTERY_STATUS_CHARGING
                    || status == BatteryManager.BATTERY_STATUS_FULL;
        } catch (Throwable ignored) {
            return false;
        }
    }

    private synchronized void stopListening() {
        listening = false;
        try {
            if (recorder != null && recorder.getRecordingState()
                    == AudioRecord.RECORDSTATE_RECORDING) {
                recorder.stop();
            }
        } catch (Throwable ignored) {}
        if (audioThread != null) audioThread.interrupt();
        releaseCpuLock();
    }

    private void acquireCpuLock() {
        try {
            if (cpuLock == null) {
                PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
                if (pm != null) {
                    cpuLock = pm.newWakeLock(
                            PowerManager.PARTIAL_WAKE_LOCK,
                            "SolWake:Listener");
                    cpuLock.setReferenceCounted(false);
                }
            }
            if (cpuLock != null && !cpuLock.isHeld()) cpuLock.acquire();
        } catch (Throwable ignored) {}
    }

    private void releaseCpuLock() {
        try {
            if (cpuLock != null && cpuLock.isHeld()) cpuLock.release();
        } catch (Throwable ignored) {}
    }

    private double extractConfidence(JSONObject obj, boolean partial) {
        try {
            JSONArray words = obj.optJSONArray(partial ? "partial_result" : "result");
            if (words == null || words.length() == 0) return partial ? 0.74 : 0.82;
            double sum = 0.0;
            int count = 0;
            for (int i = 0; i < words.length(); i++) {
                JSONObject w = words.optJSONObject(i);
                if (w != null && w.has("conf")) {
                    sum += w.optDouble("conf", 0.0);
                    count++;
                }
            }
            return count == 0 ? (partial ? 0.74 : 0.82) : sum / count;
        } catch (Throwable ignored) {
            return partial ? 0.74 : 0.82;
        }
    }

    private static String normalize(String value) {
        String s = value == null ? "" : value.toLowerCase(Locale.ROOT).trim();
        s = Normalizer.normalize(s, Normalizer.Form.NFD)
                .replaceAll("\\p{M}+", "");
        s = s.replaceAll("[^a-z0-9ñ ]", " ")
                .replaceAll("\\s+", " ")
                .trim();
        return s;
    }

    private static double similarity(String a, String b) {
        if (a.equals(b)) return 1.0;
        if (a.isEmpty() || b.isEmpty()) return 0.0;
        int distance = levenshtein(a, b);
        int max = Math.max(a.length(), b.length());
        return Math.max(0.0, 1.0 - ((double) distance / (double) max));
    }

    private static int levenshtein(String a, String b) {
        int[] prev = new int[b.length() + 1];
        int[] cur = new int[b.length() + 1];
        for (int j = 0; j <= b.length(); j++) prev[j] = j;

        for (int i = 1; i <= a.length(); i++) {
            cur[0] = i;
            for (int j = 1; j <= b.length(); j++) {
                int cost = a.charAt(i - 1) == b.charAt(j - 1) ? 0 : 1;
                cur[j] = Math.min(
                        Math.min(cur[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
            }
            int[] tmp = prev;
            prev = cur;
            cur = tmp;
        }
        return prev[b.length()];
    }

    private void setStatus(String value) {
        status = value;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm != null) nm.notify(NOTIFICATION_ID, buildNotification(value));
    }

    private Notification buildNotification(String text) {
        Intent open = new Intent(this, MainActivity.class);
        PendingIntent content = PendingIntent.getActivity(
                this, 1, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Intent testIntent = new Intent(this, WakeService.class).setAction(ACTION_TEST);
        PendingIntent test = PendingIntent.getService(
                this, 2, testIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Intent stopIntent = new Intent(this, WakeService.class).setAction(ACTION_STOP);
        PendingIntent stop = PendingIntent.getService(
                this, 3, stopIntent,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        return new Notification.Builder(this, CHANNEL)
                .setSmallIcon(android.R.drawable.ic_btn_speak_now)
                .setContentTitle("Sol Wake")
                .setContentText(text)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setContentIntent(content)
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_media_play, "Probar", test).build())
                .addAction(new Notification.Action.Builder(
                        android.R.drawable.ic_menu_close_clear_cancel, "Detener", stop).build())
                .build();
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT >= 26) {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) {
                NotificationChannel channel = new NotificationChannel(
                        CHANNEL,
                        "Wake word",
                        NotificationManager.IMPORTANCE_LOW);
                channel.setDescription("Escucha local de la frase de activación");
                nm.createNotificationChannel(channel);
            }
        }
    }

    private void startForegroundCompat(Notification notification) {
        if (Build.VERSION.SDK_INT >= 29) {
            startForeground(
                    NOTIFICATION_ID,
                    notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    private String shortMessage(Throwable t) {
        if (t == null) return "desconocido";
        String m = t.getMessage();
        if (m == null || m.trim().isEmpty()) return t.getClass().getSimpleName();
        return m.length() > 100 ? m.substring(0, 100) : m;
    }

    @Override
    public void onDestroy() {
        stopListening();
        try { unregisterReceiver(conditionsReceiver); } catch (Throwable ignored) {}
        try { if (model != null) model.close(); } catch (Throwable ignored) {}
        model = null;
        running = false;
        status = "detenido";
        try { stopForeground(true); } catch (Throwable ignored) {}
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
