package com.bwa3d.codexremote;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;

public class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context context, Intent intent) {
        if (!Intent.ACTION_BOOT_COMPLETED.equals(intent.getAction())) return;
        SharedPreferences p = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        if (!p.getBoolean("autostart", false)) return;
        Intent s = new Intent(context, RemoteService.class);
        s.setAction(RemoteService.ACTION_START);
        s.putExtra("ip", p.getString("ip", "192.168.1.100"));
        s.putExtra("port", p.getInt("port", 8765));
        if (Build.VERSION.SDK_INT >= 26) context.startForegroundService(s);
        else context.startService(s);
    }
}
