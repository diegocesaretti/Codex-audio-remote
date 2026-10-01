package com.diegocesaretti.solwake;

import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.net.Uri;

public final class AssistantLauncher {
    private AssistantLauncher() {}

    public static boolean launch(Context context, SharedPreferences prefs) {
        int mode = prefs.getInt(Prefs.MODE, 0);

        try {
            if (mode == 2) {
                String flat = prefs.getString(
                        Prefs.COMPONENT,
                        "com.openai.chatgpt/com.openai.voice.assistant.AssistantActivity");
                ComponentName component = ComponentName.unflattenFromString(flat == null ? "" : flat.trim());
                if (component == null) throw new IllegalArgumentException("Componente inválido");
                Intent intent = new Intent();
                intent.setComponent(component);
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                context.startActivity(intent);
                return true;
            }

            if (mode == 3) {
                String value = prefs.getString(Prefs.URI, "");
                if (value == null || value.trim().isEmpty()) throw new IllegalArgumentException("URI vacía");
                Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(value.trim()));
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                context.startActivity(intent);
                return true;
            }

            Intent intent = new Intent(mode == 1 ? Intent.ACTION_ASSIST : Intent.ACTION_VOICE_ASSIST);
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
            context.startActivity(intent);
            return true;
        } catch (Throwable first) {
            try {
                Intent fallback = new Intent(Intent.ACTION_VOICE_ASSIST);
                fallback.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                context.startActivity(fallback);
                return true;
            } catch (Throwable second) {
                try {
                    Intent fallback2 = new Intent(Intent.ACTION_ASSIST);
                    fallback2.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
                    context.startActivity(fallback2);
                    return true;
                } catch (Throwable ignored) {
                    return false;
                }
            }
        }
    }
}
