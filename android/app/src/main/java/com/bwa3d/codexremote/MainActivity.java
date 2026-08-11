package com.bwa3d.codexremote;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.SeekBar;
import android.widget.TextView;

public class MainActivity extends Activity {
    private static final int REQ_AUDIO = 10;
    private EditText serverIp, serverPort, timeoutSeconds, endPhrases, manualChunkMs;
    private TextView statusText, sensitivityText, qualityText, latencyText;
    private SeekBar sensitivity, quality, latency;
    private CheckBox autostart, manualLatency;

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
        sensitivity = findViewById(R.id.sensitivitySeek);
        quality = findViewById(R.id.qualitySeek);
        latency = findViewById(R.id.latencySeek);
        manualLatency = findViewById(R.id.manualLatencyCheck);
        autostart = findViewById(R.id.autostartCheck);
        Button connect = findViewById(R.id.connectButton);
        Button wake = findViewById(R.id.wakeButton);
        Button overlay = findViewById(R.id.overlayButton);

        SharedPreferences prefs = getSharedPreferences("settings", MODE_PRIVATE);
        serverIp.setText(prefs.getString("ip", "192.168.1.100"));
        serverPort.setText(String.valueOf(prefs.getInt("port", 8765)));
        timeoutSeconds.setText(String.valueOf(prefs.getInt("conversation_timeout", 300)));
        endPhrases.setText(prefs.getString("end_phrases", "gracias sol, chau sol, adiós sol, listo sol"));
        manualChunkMs.setText(String.valueOf(prefs.getInt("manual_chunk_ms", 45)));

        int sens = prefs.getInt("sensitivity", 60);
        int q = prefs.getInt("audio_quality", 80);
        int l = prefs.getInt("audio_latency", 55);
        sensitivity.setProgress(sens);
        quality.setProgress(q);
        latency.setProgress(l);
        updateSensitivityLabel(sens);
        updateQualityLabel(q);
        updateLatencyLabel(l);
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
        String label = value >= 75 ? "Alta" : value >= 40 ? "Equilibrada" : "Ligera";
        qualityText.setText("Calidad / estabilidad: " + value + "% · " + label);
    }
    private void updateLatencyLabel(int value) {
        int chunk = RemoteService.chunkMsForLatency(value);
        String label = value >= 75 ? "Muy baja" : value >= 40 ? "Equilibrada" : "Robusta";
        latencyText.setText("Latencia automática: " + value + "% · " + label + " · paquetes ~" + chunk + " ms");
    }

    private void requestOverlayPermission() {
        if (Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(this)) { statusText.setText("Overlay habilitado"); return; }
        startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName())));
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
        statusText.setText("Servicio iniciado: ws://" + ip + ":" + port + "/ws/ · wake: hola sol");
    }

    private void startServiceCompat(Intent i) {
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
    }
}
