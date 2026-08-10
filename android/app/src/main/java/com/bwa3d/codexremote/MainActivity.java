package com.bwa3d.codexremote;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;

public class MainActivity extends Activity {
    private static final int REQ_AUDIO = 10;
    private EditText serverIp;
    private EditText serverPort;
    private TextView statusText;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);

        serverIp = findViewById(R.id.serverIp);
        serverPort = findViewById(R.id.serverPort);
        statusText = findViewById(R.id.statusText);
        Button connect = findViewById(R.id.connectButton);
        Button wake = findViewById(R.id.wakeButton);

        SharedPreferences prefs = getSharedPreferences("settings", MODE_PRIVATE);
        serverIp.setText(prefs.getString("ip", "192.168.1.100"));
        serverPort.setText(String.valueOf(prefs.getInt("port", 8765)));

        connect.setOnClickListener(v -> startRemoteService());
        wake.setOnClickListener(v -> {
            Intent i = new Intent(this, RemoteService.class);
            i.setAction(RemoteService.ACTION_WAKE);
            startServiceCompat(i);
            statusText.setText("Wake enviado…");
        });

        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, REQ_AUDIO);
        }
    }

    private void startRemoteService() {
        String ip = serverIp.getText().toString().trim();
        int port;
        try { port = Integer.parseInt(serverPort.getText().toString().trim()); }
        catch (Exception e) { port = 8765; }

        getSharedPreferences("settings", MODE_PRIVATE).edit()
                .putString("ip", ip)
                .putInt("port", port)
                .apply();

        Intent i = new Intent(this, RemoteService.class);
        i.setAction(RemoteService.ACTION_START);
        i.putExtra("ip", ip);
        i.putExtra("port", port);
        startServiceCompat(i);
        statusText.setText("Servicio iniciado: ws://" + ip + ":" + port + "/ws/");
    }

    private void startServiceCompat(Intent i) {
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i);
        else startService(i);
    }
}
