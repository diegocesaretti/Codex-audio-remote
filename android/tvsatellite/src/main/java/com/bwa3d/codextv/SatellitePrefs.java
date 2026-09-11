package com.bwa3d.codextv;

import android.content.Context;
import android.content.SharedPreferences;

public final class SatellitePrefs {
    private static final String PREFS = "codex_tv_satellite";
    private static final String START_ON_BOOT = "start_on_boot";

    private SatellitePrefs() {}

    public static boolean startOnBoot(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .getBoolean(START_ON_BOOT, false);
    }

    public static void setStartOnBoot(Context context, boolean enabled) {
        SharedPreferences prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        prefs.edit().putBoolean(START_ON_BOOT, enabled).apply();
    }
}
