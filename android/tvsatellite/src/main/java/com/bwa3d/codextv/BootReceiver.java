package com.bwa3d.codextv;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (intent == null) return;
        String action = intent.getAction();
        if (!Intent.ACTION_BOOT_COMPLETED.equals(action) &&
                !Intent.ACTION_LOCKED_BOOT_COMPLETED.equals(action)) return;
        if (SatellitePrefs.startOnBoot(context)) {
            SatelliteService.ensureRunning(context);
        }
    }
}
