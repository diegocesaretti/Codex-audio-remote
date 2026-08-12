package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Color;

public final class OverlayPrefs {
    private OverlayPrefs() { }

    public static float scale(Context context) {
        SharedPreferences p = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        int progress = Math.max(0, Math.min(100, p.getInt("overlay_size", 55)));
        return 0.80f + (progress / 100f) * 0.90f; // 80% .. 170%
    }

    public static int color(Context context) {
        SharedPreferences p = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        int idx = Math.max(0, Math.min(5, p.getInt("overlay_color", 0)));
        switch (idx) {
            case 1: return 0xE6283A56; // azul
            case 2: return 0xE61F5A42; // verde
            case 3: return 0xE66A2E55; // violeta
            case 4: return 0xE66A3D20; // marrón/cálido
            case 5: return 0xE6602020; // rojo oscuro
            default: return 0xE6212121; // grafito
        }
    }

    public static int textColor(Context context) {
        return Color.WHITE;
    }
}
