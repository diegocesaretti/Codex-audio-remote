package com.bwa3d.codexremote;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.AudioTrack;
import android.media.MediaRecorder;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;

import java.io.ByteArrayOutputStream;

public class MainActivity extends Activity {
    private static final int REQ_AUDIO = 10;
    private static final String[] AUDIO_SOURCE_LABELS = {
            "Reconocimiento de voz / asistente",
            "Comunicación / llamada",
            "Micrófono normal",
            "Default Android",
            "Camcorder"
    };
    private static final String[] AUDIO_SOURCE_KEYS = {
            "voice_recognition", "voice_communication", "mic", "default", "camcorder"
    };
    private static final String[] ENHANCER_LABELS = { "OFF", "Suave", "Normal", "Fuerte" };
    private static final String[] ENHANCER_KEYS = { VoiceEnhancer.OFF, VoiceEnhancer.SOFT, VoiceEnhancer.NORMAL, VoiceEnhancer.STRONG };

    private EditText serverIp, serverPort, timeoutSeconds, endPhrases, manualChunkMs;
    private TextView statusText, sensitivityText, qualityText, latencyText, micGainText;
    private SeekBar sensitivity, quality, latency, micGain;
    private Spinner audioSourceSpinner, voiceEnhancerSpinner;
    private CheckBox autostart, manualLatency, noiseSuppressor, androidAgc, aec;
    private volatile boolean audioTestRunning;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        serverIp = findViewById(R.id.serverIp);
        serverPort = findViewById(R.id.serverPort);
        timeoutSeconds = findViewById(R.id.timeoutSeconds);
        endPhrases = findViewById(R.id.endPhrases);
        manualChunkMs = findViewById(R.id.manualChunkMs);
        statusText = findViewById(R.id.statusText);
        sensitivityText = findViewById(R.id.sensitivityText);
        qualityText = findViewById(R.id.qualityText);
        latencyText = findViewById(R.id.latencyText);
        micGainText = findViewById(R.id.micGainText);
        sensitivity = findViewById(R.id.sensitivitySeek);
        quality = findViewById(R.id.qualitySeek);
        latency = findViewById(R.id.latencySeek);
        micGain = findViewById(R.id.micGainSeek);
        audioSourceSpinner = findViewById(R.id.audioSourceSpinner);
        voiceEnhancerSpinner = findViewById(R.id.voiceEnhancerSpinner);
        noiseSuppressor = findViewById(R.id.noiseSuppressorCheck);
        androidAgc = findViewById(R.id.androidAgcCheck);
        aec = findViewById(R.id.aecCheck);
        manualLatency = findViewById(R.id.manualLatencyCheck);
        autostart = findViewById(R.id.autostartCheck);
        Button connect = findViewById(R.id.connectButton);
        Button wake = findViewById(R.id.wakeButton);
        Button overlay = findViewById(R.id.overlayButton);
        Button audioTest = findViewById(R.id.audioTestButton);

        ArrayAdapter<String> sourceAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, AUDIO_SOURCE_LABELS);
        sourceAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        audioSourceSpinner.setAdapter(sourceAdapter);
        ArrayAdapter<String> enhancerAdapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, ENHANCER_LABELS);
        enhancerAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        voiceEnhancerSpinner.setAdapter(enhancerAdapter);

        SharedPreferences prefs = getSharedPreferences("settings", MODE_PRIVATE);
        serverIp.setText(prefs.getString("ip", "192.168.1.100"));
        serverPort.setText(String.valueOf(prefs.getInt("port", 8765)));
        timeoutSeconds.setText(String.valueOf(prefs.getInt("conversation_timeout", 300)));
        endPhrases.setText(prefs.getString("end_phrases", "gracias sol, chau sol, adiós sol, listo sol"));
        manualChunkMs.setText(String.valueOf(prefs.getInt("manual_chunk_ms", 45)));

        int sens = prefs.getInt("sensitivity", 60);
        int q = prefs.getInt("audio_quality", 80);
        int l = prefs.getInt("audio_latency", 55);
        int gainPct = Math.max(50, Math.min(400, prefs.getInt("mic_gain_pct", 100)));
        sensitivity.setProgress(sens);
        quality.setProgress(q);
        latency.setProgress(l);
        micGain.setProgress(gainPct - 50);
        updateSensitivityLabel(sens);
        updateQualityLabel(q);
        updateLatencyLabel(l);
        updateMicGainLabel(gainPct);
        audioSourceSpinner.setSelection(audioSourcePosition(prefs.getString("audio_source", "voice_recognition")));
        voiceEnhancerSpinner.setSelection(enhancerPosition(prefs.getString("voice_enhancer", VoiceEnhancer.OFF)));
        noiseSuppressor.setChecked(prefs.getBoolean("native_ns", false));
        androidAgc.setChecked(prefs.getBoolean("native_agc", false));
        aec.setChecked(prefs.getBoolean("native_aec", false));
        manualLatency.setChecked(prefs.getBoolean("manual_latency", true));
        manualChunkMs.setEnabled(manualLatency.isChecked());
        autostart.setChecked(prefs.getBoolean("autostart", false));

        sensitivity.setOnSeekBarChangeListener(listener((progress) -> {
            updateSensitivityLabel(progress);
            getSharedPreferences("settings", MODE_PRIVATE).edit().putInt("sensitivity", progress).apply();
        }));
        quality.setOnSeekBarChangeListener(listener((progress) -> {
            updateQualityLabel(progress);
            getSharedPreferences("settings", MODE_PRIVATE).edit().putInt("audio_quality", progress).apply();
        }));
        latency.setOnSeekBarChangeListener(listener((progress) -> {
            updateLatencyLabel(progress);
            getSharedPreferences("settings", MODE_PRIVATE).edit().putInt("audio_latency", progress).apply();
        }));
        micGain.setOnSeekBarChangeListener(listener((progress) -> {
            int pct = progress + 50;
            updateMicGainLabel(pct);
            getSharedPreferences("settings", MODE_PRIVATE).edit().putInt("mic_gain_pct", pct).apply();
        }));

        manualLatency.setOnCheckedChangeListener((buttonView, isChecked) -> {
            manualChunkMs.setEnabled(isChecked);
            getSharedPreferences("settings", MODE_PRIVATE).edit().putBoolean("manual_latency", isChecked).apply();
        });

        autostart.setOnCheckedChangeListener((buttonView, isChecked) ->
                getSharedPreferences("settings", MODE_PRIVATE).edit().putBoolean("autostart", isChecked).apply());

        connect.setOnClickListener(v -> startRemoteService());
        wake.setOnClickListener(v -> {
            saveSettings();
            Intent i = new Intent(this, RemoteService.class);
            i.setAction(RemoteService.ACTION_WAKE);
            startServiceCompat(i);
            statusText.setText("Wake enviado…");
        });
        overlay.setOnClickListener(v -> requestOverlayPermission());
        audioTest.setOnClickListener(v -> runLocalAudioTest());

        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQ_AUDIO);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED)
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 11);
    }

    private interface ProgressAction { void apply(int progress); }
    private SeekBar.OnSeekBarChangeListener listener(ProgressAction action) {
        return new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) { if (fromUser) action.apply(progress); }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        };
    }

    private void updateSensitivityLabel(int value) { sensitivityText.setText("Sensibilidad wake: " + value + "%"); }
    private void updateQualityLabel(int value) {
        int rate = RemoteService.sampleRateForQuality(value);
        String label = rate == 48000 ? "Alta" : rate == 24000 ? "Media" : "Compatible";
        qualityText.setText("Calidad: " + value + "% · " + label + " · " + (rate / 1000) + " kHz");
    }
    private void updateLatencyLabel(int value) {
        int chunk = RemoteService.chunkMsForLatency(value);
        String label = value >= 75 ? "Muy baja" : value >= 40 ? "Equilibrada" : "Robusta";
        latencyText.setText("Latencia automática: " + value + "% · " + label + " · paquetes ~" + chunk + " ms");
    }
    private void updateMicGainLabel(int pct) {
        micGainText.setText("Ganancia mic: " + pct + "% · " + String.format(java.util.Locale.US, "%.2f×", pct / 100.0));
    }

    private static int audioSourcePosition(String key) {
        if (key != null) for (int i = 0; i < AUDIO_SOURCE_KEYS.length; i++) if (AUDIO_SOURCE_KEYS[i].equals(key)) return i;
        return 0;
    }
    private static int enhancerPosition(String key) {
        if (key != null) for (int i = 0; i < ENHANCER_KEYS.length; i++) if (ENHANCER_KEYS[i].equals(key)) return i;
        return 0;
    }
    private String selectedAudioSourceKey() {
        int p = audioSourceSpinner.getSelectedItemPosition();
        if (p < 0 || p >= AUDIO_SOURCE_KEYS.length) p = 0;
        return AUDIO_SOURCE_KEYS[p];
    }
    private String selectedEnhancerKey() {
        int p = voiceEnhancerSpinner.getSelectedItemPosition();
        if (p < 0 || p >= ENHANCER_KEYS.length) p = 0;
        return ENHANCER_KEYS[p];
    }

    private void requestOverlayPermission() {
        if (Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(this)) { statusText.setText("Overlay habilitado"); return; }
        startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName())));
    }

    private void runLocalAudioTest() {
        if (audioTestRunning) return;
        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQ_AUDIO);
            return;
        }
        saveSettings();
        audioTestRunning = true;
        stopService(new Intent(this, RemoteService.class));
        statusText.setText("Test local: grabando 5 segundos… hablá ahora");

        new Thread(() -> {
            AudioRecord record = null;
            AudioTrack player = null;
            NativeAudioEffects effects = null;
            int requestedRate = RemoteService.sampleRateForQuality(quality.getProgress());
            int usedRate = requestedRate;
            int gainPct = micGain.getProgress() + 50;
            String enhancerMode = selectedEnhancerKey();
            String sourceKey = selectedAudioSourceKey();
            int requestedSource = RemoteService.audioSourceForKey(sourceKey);
            int usedSource = requestedSource;
            try {
                int min = AudioRecord.getMinBufferSize(usedRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                if (min <= 0) {
                    usedRate = 16000;
                    min = AudioRecord.getMinBufferSize(usedRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                }
                int bufferSize = Math.max(min, usedRate * 2 / 5);
                record = new AudioRecord(usedSource, usedRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bufferSize);
                if (record.getState() != AudioRecord.STATE_INITIALIZED) {
                    record.release();
                    usedSource = MediaRecorder.AudioSource.VOICE_RECOGNITION;
                    if (usedRate != 16000) usedRate = 16000;
                    min = AudioRecord.getMinBufferSize(usedRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
                    bufferSize = Math.max(min, usedRate * 2 / 5);
                    record = new AudioRecord(usedSource, usedRate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, bufferSize);
                }
                if (record.getState() != AudioRecord.STATE_INITIALIZED) throw new IllegalStateException("AudioRecord no inicializa");

                effects = new NativeAudioEffects(record.getAudioSessionId(), noiseSuppressor.isChecked(), androidAgc.isChecked(), aec.isChecked());
                VoiceEnhancer enhancer = new VoiceEnhancer(enhancerMode, usedRate);
                final int finalRate = usedRate;
                final int finalSource = usedSource;
                final String fxSummary = effects.summary();
                runOnUiThread(() -> statusText.setText("Test: " + (finalRate / 1000) + " kHz · " + RemoteService.audioSourceName(finalSource) + " · gain " + gainPct + "% · enhancer " + enhancerMode + " · " + fxSummary));
                ByteArrayOutputStream pcm = new ByteArrayOutputStream(usedRate * 2 * 5);
                byte[] chunk = new byte[Math.max(1024, usedRate * 2 * 45 / 1000)];
                record.startRecording();
                long end = System.currentTimeMillis() + 5000;
                while (System.currentTimeMillis() < end) {
                    int n = record.read(chunk, 0, chunk.length);
                    if (n > 0) {
                        RemoteService.applyGainPcm16InPlace(chunk, n, gainPct);
                        enhancer.processInPlace(chunk, n);
                        pcm.write(chunk, 0, n);
                    }
                }
                record.stop();
                byte[] data = pcm.toByteArray();

                runOnUiThread(() -> statusText.setText("Test local: reproduciendo lo capturado…"));
                int outMin = AudioTrack.getMinBufferSize(usedRate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT);
                player = new AudioTrack(AudioManager.STREAM_MUSIC, usedRate, AudioFormat.CHANNEL_OUT_MONO,
                        AudioFormat.ENCODING_PCM_16BIT, Math.max(outMin, 4096), AudioTrack.MODE_STREAM);
                player.play();
                int pos = 0;
                while (pos < data.length) {
                    int n = player.write(data, pos, data.length - pos);
                    if (n <= 0) break;
                    pos += n;
                }
                try { Thread.sleep(250); } catch (InterruptedException ignored) { }
                runOnUiThread(() -> statusText.setText("Test local terminado. Compará enhancer, ganancia y efectos antes de probar Codex."));
            } catch (Exception e) {
                runOnUiThread(() -> statusText.setText("Test local falló: " + e.getClass().getSimpleName() + " · " + e.getMessage()));
            } finally {
                if (effects != null) effects.close();
                if (record != null) { try { record.stop(); } catch (Exception ignored) { } record.release(); }
                if (player != null) { try { player.stop(); } catch (Exception ignored) { } player.release(); }
                audioTestRunning = false;
                runOnUiThread(this::startRemoteService);
            }
        }, "LocalAudioDiagnostic").start();
    }

    private void saveSettings() {
        String ip = serverIp.getText().toString().trim();
        int port; try { port = Integer.parseInt(serverPort.getText().toString().trim()); } catch (Exception e) { port = 8765; }
        int timeout; try { timeout = Math.max(0, Integer.parseInt(timeoutSeconds.getText().toString().trim())); } catch (Exception e) { timeout = 300; }
        int chunkMs; try { chunkMs = Integer.parseInt(manualChunkMs.getText().toString().trim()); } catch (Exception e) { chunkMs = 45; }
        chunkMs = Math.max(20, Math.min(120, chunkMs));
        manualChunkMs.setText(String.valueOf(chunkMs));
        String endings = endPhrases.getText().toString().trim();
        getSharedPreferences("settings", MODE_PRIVATE).edit()
                .putString("ip", ip).putInt("port", port)
                .putInt("sensitivity", sensitivity.getProgress())
                .putInt("audio_quality", quality.getProgress())
                .putInt("audio_latency", latency.getProgress())
                .putInt("mic_gain_pct", micGain.getProgress() + 50)
                .putString("audio_source", selectedAudioSourceKey())
                .putString("voice_enhancer", selectedEnhancerKey())
                .putBoolean("native_ns", noiseSuppressor.isChecked())
                .putBoolean("native_agc", androidAgc.isChecked())
                .putBoolean("native_aec", aec.isChecked())
                .putBoolean("manual_latency", manualLatency.isChecked())
                .putInt("manual_chunk_ms", chunkMs)
                .putInt("conversation_timeout", timeout)
                .putString("end_phrases", endings)
                .putBoolean("autostart", autostart.isChecked()).apply();
    }

    private void startRemoteService() {
        saveSettings();
        String ip = serverIp.getText().toString().trim();
        int port; try { port = Integer.parseInt(serverPort.getText().toString().trim()); } catch (Exception e) { port = 8765; }
        Intent i = new Intent(this, RemoteService.class);
        i.setAction(RemoteService.ACTION_START);
        i.putExtra("ip", ip); i.putExtra("port", port);
        startServiceCompat(i);
        if (!audioTestRunning) statusText.setText("Servicio iniciado: ws://" + ip + ":" + port + "/ws/ · wake: hola sol");
    }

    private void startServiceCompat(Intent i) {
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
    }
}
