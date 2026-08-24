package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;

import java.io.File;
import java.util.Locale;

import ai.picovoice.porcupine.Porcupine;

/** Shared configuration and factory for the Porcupine wake engine. */
final class PorcupineWakeSupport {
    static final String ENGINE_PORCUPINE = "porcupine";
    static final String ENGINE_VOSK = "vosk";
    static final String MODEL_ASSET = "porcupine_params_es.pv";
    private static final String KEYWORD_FILE = "porcupine-wake-es.ppn";

    private PorcupineWakeSupport() { }

    static SharedPreferences prefs(Context context) {
        return context.getSharedPreferences("settings", Context.MODE_PRIVATE);
    }

    static String selectedEngine(Context context) {
        return prefs(context).getString("wake_engine", ENGINE_PORCUPINE);
    }

    static boolean isSelected(Context context) {
        return ENGINE_PORCUPINE.equals(selectedEngine(context));
    }

    static String accessKey(Context context) {
        String value = prefs(context).getString("porcupine_access_key", "");
        return value == null ? "" : value.trim();
    }

    static File keywordFile(Context context) {
        return new File(context.getFilesDir(), KEYWORD_FILE);
    }

    static boolean isConfigured(Context context) {
        return !accessKey(context).isEmpty() && keywordFile(context).isFile() && keywordFile(context).length() > 0;
    }

    static float sensitivity(Context context) {
        int value = Math.max(0, Math.min(100, prefs(context).getInt("sensitivity", 60)));
        // Keep useful room at both ends. 60% maps to 0.55.
        return 0.25f + (0.50f * (value / 100.0f));
    }

    static Porcupine build(Context context) throws Exception {
        return new Porcupine.Builder()
                .setAccessKey(accessKey(context))
                .setKeywordPath(keywordFile(context).getAbsolutePath())
                .setModelPath(MODEL_ASSET)
                .setSensitivity(sensitivity(context))
                .build(context.getApplicationContext());
    }

    static void train(Context context, String accessKey, String phrase) throws Exception {
        String key = accessKey == null ? "" : accessKey.trim();
        String wake = normalize(phrase);
        if (key.isEmpty()) throw new IllegalArgumentException("Falta el AccessKey de Picovoice");
        if (wake.isEmpty()) throw new IllegalArgumentException("El wake word está vacío");

        File out = keywordFile(context);
        File tmp = new File(context.getFilesDir(), KEYWORD_FILE + ".tmp");
        if (tmp.exists() && !tmp.delete()) throw new IllegalStateException("No pude limpiar el modelo temporal");

        Porcupine.trainWakeWordFromPhrase(key, tmp.getAbsolutePath(), "es", wake);
        if (!tmp.isFile() || tmp.length() == 0) throw new IllegalStateException("Picovoice no generó el modelo wake");

        if (out.exists() && !out.delete()) throw new IllegalStateException("No pude reemplazar el modelo wake anterior");
        if (!tmp.renameTo(out)) throw new IllegalStateException("No pude instalar el modelo wake entrenado");

        prefs(context).edit()
                .putString("porcupine_access_key", key)
                .putString("wake_engine", ENGINE_PORCUPINE)
                .putString("porcupine_trained_phrase", wake)
                .apply();
    }

    static String trainedPhrase(Context context) {
        return prefs(context).getString("porcupine_trained_phrase", "");
    }

    static String normalize(String value) {
        if (value == null) return "";
        return value.trim().toLowerCase(Locale.ROOT).replaceAll("\\s+", " ");
    }
}
