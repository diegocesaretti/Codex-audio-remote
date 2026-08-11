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
    private EditText serverIp;
    private EditText serverPort;
    private TextView statusText;
    private TextView sensitivityText;
    private SeekBar sensitivity;
    private CheckBox autostart;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        serverIp = findViewById(R.id.serverIp);
        serverPort = findViewById(R.id.serverPort);
        statusText = findViewById(R.id.statusText);
        sensitivityText = findViewById(R.id.sensitivityText);
        sensitivity = findViewById(R.id.sensitivitySeek);
        autostart = findViewById(R.id.autostartCheck);
        Button connect = findViewById(R.id.connectButton);
        Button wake = findViewById(R.id.wakeButton);
        Button overlay = findViewById(R.id.overlayButton);

        SharedPreferences prefs = getSharedPreferences("settings", MODE_PRIVATE);
        serverIp.setText(prefs.getString("ip", "192.168.1.100"));
        serverPort.setText(String.valueOf(prefs.getInt("port", 8765)));
        int sens = prefs.getInt("sensitivity", 60);
        sensitivity.setProgress(sens);
        sensitivityText.setText("Sensibilidad wake: " + sens + "%");
        autostart.setChecked(prefs.getBoolean("autostart", false));

        sensitivity.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                sensitivityText.setText("Sensibilidad wake: " + progress + "%");
                if (fromUser) getSharedPreferences("settings", MODE_PRIVATE).edit().putInt("sensitivity", progress).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        });

        autostart.setOnCheckedChangeListener((buttonView, isChecked) ->
                getSharedPreferences("settings", MODE_PRIVATE).edit().putBoolean("autostart", isChecked).apply());

        connect.setOnClickListener(v -> startRemoteService());
        wake.setOnClickListener(v -> {
            Intent i = new Intent(this, RemoteService.class);
            i.setAction(RemoteService.ACTION_WAKE);
            startServiceCompat(i);
            statusText.setText("Wake enviado…");
        });
        overlay.setOnClickListener(v -> requestOverlayPermission());

        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQ_AUDIO);
        }
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 11);
        }
    }

    private void requestOverlayPermission() {
        if (Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(this)) {
            statusText.setText("Overlay habilitado");
            return;
        }
        Intent i = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:" + getPackageName()));
        startActivity(i);
    }

    private void startRemoteService() {
        String ip = serverIp.getText().toString().trim();
        int port;
        try { port = Integer.parseInt(serverPort.getText().toString().trim()); }
        catch (Exception e) { port = 8765; }

        getSharedPreferences("settings", MODE_PRIVATE).edit()
                .putString("ip", ip)
                .putInt("port", port)
                .putInt("sensitivity", sensitivity.getProgress())
                .putBoolean("autostart", autostart.isChecked())
                .apply();

        Intent i = new Intent(this, RemoteService.class);
        i.setAction(RemoteService.ACTION_START);
        i.putExtra("ip", ip);
        i.putExtra("port", port);
        startServiceCompat(i);
        statusText.setText("Servicio iniciado: ws://" + ip + ":" + port + "/ws/ · wake: hola sol");
    }

    private void startServiceCompat(Intent i) {
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i);
        else startService(i);
    }
}
