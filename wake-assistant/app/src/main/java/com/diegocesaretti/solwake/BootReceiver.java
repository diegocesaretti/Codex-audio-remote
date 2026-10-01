package com.diegocesaretti.solwake;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Build;

public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        SharedPreferences prefs = Prefs.get(context);
        if (!prefs.getBoolean(Prefs.AUTO_BOOT, false)) return;

        Intent service = new Intent(context, WakeService.class);
        service.setAction(WakeService.ACTION_START);
        try {
            if (Build.VERSION.SDK_INT >= 26) {
                context.startForegroundService(service);
            } else {
                context.startService(service);
            }
        } catch (Throwable ignored) {
            // Android recientes pueden bloquear un FGS de micrófono iniciado desde BOOT_COMPLETED.
            // En ese caso basta abrir la app y tocar "Iniciar escucha".
        }
    }
}
