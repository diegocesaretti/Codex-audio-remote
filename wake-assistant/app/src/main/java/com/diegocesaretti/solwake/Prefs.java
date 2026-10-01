package com.diegocesaretti.solwake;

import android.content.Context;
import android.content.SharedPreferences;

public final class Prefs {
    private Prefs() {}

    public static final String NAME = "sol_wake_prefs";

    public static final String PHRASE = "phrase";
    public static final String SENSITIVITY = "sensitivity";
    public static final String SCREEN_ONLY = "screen_only";
    public static final String CHARGING_ONLY = "charging_only";
    public static final String VIBRATE = "vibrate";
    public static final String BEEP = "beep";
    public static final String AUTO_BOOT = "auto_boot";
    public static final String COOLDOWN = "cooldown";
    public static final String MODE = "mode";
    public static final String COMPONENT = "component";
    public static final String URI = "uri";
    public static final String AUDIO_SOURCE = "audio_source";

    public static SharedPreferences get(Context context) {
        return context.getSharedPreferences(NAME, Context.MODE_PRIVATE);
    }

    public static String phrase(SharedPreferences p) {
        String value = p.getString(PHRASE, "Hola Sol");
        value = value == null ? "Hola Sol" : value.trim();
        return value.isEmpty() ? "Hola Sol" : value;
    }
}
