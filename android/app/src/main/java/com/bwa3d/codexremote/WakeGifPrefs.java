package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;
import android.net.Uri;

/** Persisted settings for the optional fullscreen wake GIF overlay. */
public final class WakeGifPrefs {
    private static final String PREFS = "settings";
    private static final String KEY_ENABLED = "wake_gif_enabled";
    private static final String KEY_URI = "wake_gif_uri";
    private static final String KEY_BG = "wake_gif_bg_color";
    private static final String KEY_SCALE = "wake_gif_scale_mode";
    private static final String KEY_KEEP_LISTENING = "wake_gif_keep_listening";

    private WakeGifPrefs() { }

    private static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    public static boolean enabled(Context context) {
        return prefs(context).getBoolean(KEY_ENABLED, false);
    }

    public static void setEnabled(Context context, boolean enabled) {
        prefs(context).edit().putBoolean(KEY_ENABLED, enabled).apply();
    }

    public static Uri uri(Context context) {
        String raw = prefs(context).getString(KEY_URI, "");
        if (raw == null || raw.trim().isEmpty()) return null;
        try { return Uri.parse(raw); } catch (Exception ignored) { return null; }
    }

    public static void setUri(Context context, Uri uri) {
        prefs(context).edit().putString(KEY_URI, uri == null ? "" : uri.toString()).apply();
    }

    public static void clearUri(Context context) {
        prefs(context).edit().remove(KEY_URI).apply();
    }

    public static int backgroundColor(Context context) {
        return prefs(context).getInt(KEY_BG, Color.BLACK);
    }

    public static void setBackgroundColor(Context context, int color) {
        prefs(context).edit().putInt(KEY_BG, color).apply();
    }

    public static String scaleMode(Context context) {
        String mode = prefs(context).getString(KEY_SCALE, "contain");
        return "cover".equals(mode) ? "cover" : "contain";
    }

    public static void setScaleMode(Context context, String mode) {
        prefs(context).edit().putString(KEY_SCALE, "cover".equals(mode) ? "cover" : "contain").apply();
    }

    public static boolean keepDuringListening(Context context) {
        return prefs(context).getBoolean(KEY_KEEP_LISTENING, false);
    }

    public static void setKeepDuringListening(Context context, boolean keep) {
        prefs(context).edit().putBoolean(KEY_KEEP_LISTENING, keep).apply();
    }

    public static String describeGif(Context context) {
        Uri uri = uri(context);
        if (uri == null) return "Ningún GIF seleccionado";
        String last = uri.getLastPathSegment();
        if (last == null || last.trim().isEmpty()) return "GIF local seleccionado";
        int slash = last.lastIndexOf('/');
        return slash >= 0 ? last.substring(slash + 1) : last;
    }

    public static String colorHex(Context context) {
        return String.format("#%06X", 0xFFFFFF & backgroundColor(context));
    }
}
