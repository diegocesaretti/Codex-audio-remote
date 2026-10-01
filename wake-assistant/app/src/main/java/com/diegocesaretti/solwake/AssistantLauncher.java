package com.diegocesaretti.solwake;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.net.Uri;

public final class AssistantLauncher {
    private static final String ACTION_VOICE_ASSIST = "android.intent.action.VOICE_ASSIST";
    private static final String CHATGPT_PACKAGE = "com.openai.chatgpt";
    private static final String CHATGPT_VOICE_URL = "https://chat.com/?mode=voice";
    private static volatile String lastDetail = "";

    private AssistantLauncher() {}

    public static String getLastDetail() {
        return lastDetail == null ? "" : lastDetail;
    }

    public static boolean launch(Context context, SharedPreferences prefs) {
        int mode = prefs.getInt(Prefs.MODE, 0);
        lastDetail = "";

        if (mode == 2) {
            String flat = prefs.getString(
                    Prefs.COMPONENT,
                    "com.openai.chatgpt/com.openai.voice.assistant.AssistantActivity");
            return launchComponent(context, flat);
        }

        if (mode == 3) {
            String value = prefs.getString(Prefs.URI, "");
            return launchUri(context, value);
        }

        return launchSystemAssistant(context, mode == 1);
    }

    private static boolean launchComponent(Context context, String flat) {
        ComponentName component = ComponentName.unflattenFromString(
                flat == null ? "" : flat.trim());
        if (component == null) {
            lastDetail = "Componente inválido";
            return false;
        }

        try {
            PackageManager pm = context.getPackageManager();
            ActivityInfo info = pm.getActivityInfo(component, 0);
            if (!info.exported) {
                throw new SecurityException("la Activity no está exportada");
            }
            if (!info.enabled) {
                throw new IllegalStateException("la Activity está deshabilitada");
            }
            if (info.permission != null
                    && context.checkSelfPermission(info.permission)
                    != PackageManager.PERMISSION_GRANTED) {
                throw new SecurityException("requiere permiso " + info.permission);
            }

            Intent intent = new Intent()
                    .setComponent(component)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            context.startActivity(intent);
            lastDetail = "Activity directa: " + component.flattenToShortString();
            return true;
        } catch (Throwable directError) {
            String direct = shortMessage(directError);

            // Important: a custom ChatGPT launch must never fall back to Google Assistant.
            if (CHATGPT_PACKAGE.equals(component.getPackageName())) {
                try {
                    Intent deepLink = new Intent(
                            Intent.ACTION_VIEW,
                            Uri.parse(CHATGPT_VOICE_URL))
                            .setPackage(CHATGPT_PACKAGE)
                            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                    context.startActivity(deepLink);
                    lastDetail = "ChatGPT por deep link; Activity directa falló: " + direct;
                    return true;
                } catch (Throwable deepLinkError) {
                    lastDetail = "Activity ChatGPT: " + direct
                            + " | Deep link: " + shortMessage(deepLinkError);
                    return false;
                }
            }

            lastDetail = "No se pudo abrir " + component.flattenToShortString()
                    + ": " + direct;
            return false;
        }
    }

    private static boolean launchUri(Context context, String value) {
        if (value == null || value.trim().isEmpty()) {
            lastDetail = "URI vacía";
            return false;
        }

        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(value.trim()));
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            context.startActivity(intent);
            lastDetail = "URI abierta";
            return true;
        } catch (Throwable t) {
            lastDetail = "URI: " + shortMessage(t);
            return false;
        }
    }

    private static boolean launchSystemAssistant(Context context, boolean actionAssistFirst) {
        String first = actionAssistFirst ? Intent.ACTION_ASSIST : ACTION_VOICE_ASSIST;
        String second = actionAssistFirst ? ACTION_VOICE_ASSIST : Intent.ACTION_ASSIST;

        try {
            Intent intent = new Intent(first)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            context.startActivity(intent);
            lastDetail = "Asistente del sistema: " + first;
            return true;
        } catch (Throwable firstError) {
            try {
                Intent fallback = new Intent(second)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                context.startActivity(fallback);
                lastDetail = "Asistente del sistema (fallback): " + second;
                return true;
            } catch (Throwable secondError) {
                lastDetail = shortMessage(firstError) + " | " + shortMessage(secondError);
                return false;
            }
        }
    }

    private static String shortMessage(Throwable t) {
        if (t == null) return "error desconocido";
        String message = t.getMessage();
        if (message == null || message.trim().isEmpty()) message = t.getClass().getSimpleName();
        return t.getClass().getSimpleName() + ": " + message;
    }
}
