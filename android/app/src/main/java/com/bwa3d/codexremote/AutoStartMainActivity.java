package com.bwa3d.codexremote;

import android.Manifest;
import android.app.ActivityManager;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;

public class AutoStartMainActivity extends MainActivity {
    private final Handler autoStartHandler = new Handler();

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        autoStartHandler.postDelayed(this::maybeStartRemoteService, 700);
    }

    @Override protected void onResume() {
        super.onResume();
        autoStartHandler.postDelayed(this::maybeStartRemoteService, 350);
    }

    private void maybeStartRemoteService() {
        SharedPreferences p = getSharedPreferences("settings", MODE_PRIVATE);
        if (!p.getBoolean("start_on_open", false)) return;
        if (Build.VERSION.SDK_INT >= 23 && checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) return;
        if (isServiceRunning(RemoteService.class)) {
            AndroidDebugLog.log("Start-on-open: RemoteService already running");
            return;
        }

        String ip = p.getString("ip", "192.168.1.100");
        int port = p.getInt("port", 8765);
        Intent i = new Intent(this, RemoteService.class);
        i.setAction(RemoteService.ACTION_START);
        i.putExtra("ip", ip);
        i.putExtra("port", port);
        try {
            if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
            AndroidDebugLog.log("Start-on-open: RemoteService started · ws://" + ip + ":" + port + "/ws/");
        } catch (Exception e) {
            AndroidDebugLog.log("Start-on-open failed: " + e);
        }
    }

    private boolean isServiceRunning(Class<?> serviceClass) {
        ActivityManager manager = (ActivityManager) getSystemService(Context.ACTIVITY_SERVICE);
        if (manager == null) return false;
        for (ActivityManager.RunningServiceInfo service : manager.getRunningServices(Integer.MAX_VALUE)) {
            if (serviceClass.getName().equals(service.service.getClassName())) return true;
        }
        return false;
    }
}
