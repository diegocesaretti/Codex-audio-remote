package com.diegocesaretti.solwake;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

public class MainActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());

    private EditText phrase;
    private SeekBar sensitivity;
    private TextView sensitivityValue;
    private CheckBox screenOnly;
    private CheckBox chargingOnly;
    private CheckBox vibrate;
    private CheckBox beep;
    private CheckBox autoBoot;
    private SeekBar cooldown;
    private TextView cooldownValue;
    private Spinner mode;
    private EditText component;
    private EditText uri;
    private Spinner audioSource;
    private TextView status;

    private boolean startAfterPermission = false;
    private boolean startAfterOverlay = false;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(buildUi());
        loadPrefs();
        requestBasePermissions(false);
    }

    private View buildUi() {
        ScrollView scroll = new ScrollView(this);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(18), dp(20), dp(28));
        scroll.addView(root);

        TextView title = label("Sol Wake", 28, true);
        root.addView(title);

        TextView intro = label(
                "Wake word offline con Vosk. Al detectar la frase abre el asistente de voz predeterminado de Android o una Activity/URI configurable.",
                15, false);
        intro.setPadding(0, dp(6), 0, dp(14));
        root.addView(intro);

        status = label("Estado: detenido", 16, true);
        status.setPadding(0, dp(4), 0, dp(16));
        root.addView(status);

        root.addView(label("Frase de activación", 15, true));
        phrase = new EditText(this);
        phrase.setSingleLine(true);
        phrase.setHint("Hola Sol");
        root.addView(phrase, matchWrap());

        root.addView(label("Sensibilidad", 15, true));
        sensitivityValue = label("", 14, false);
        root.addView(sensitivityValue);
        sensitivity = new SeekBar(this);
        sensitivity.setMax(100);
        sensitivity.setOnSeekBarChangeListener(simpleSeek(() ->
                sensitivityValue.setText(sensitivity.getProgress() + "%")));
        root.addView(sensitivity, matchWrap());

        screenOnly = new CheckBox(this);
        screenOnly.setText("Escuchar solo con la pantalla encendida");
        root.addView(screenOnly);

        chargingOnly = new CheckBox(this);
        chargingOnly.setText("Escuchar solo mientras el teléfono está cargando");
        root.addView(chargingOnly);

        vibrate = new CheckBox(this);
        vibrate.setText("Vibrar al detectar");
        root.addView(vibrate);

        beep = new CheckBox(this);
        beep.setText("Beep corto al detectar");
        root.addView(beep);

        autoBoot = new CheckBox(this);
        autoBoot.setText("Intentar iniciar automáticamente después de reiniciar");
        root.addView(autoBoot);

        root.addView(label("Cooldown entre activaciones", 15, true));
        cooldownValue = label("", 14, false);
        root.addView(cooldownValue);
        cooldown = new SeekBar(this);
        cooldown.setMax(10);
        cooldown.setOnSeekBarChangeListener(simpleSeek(() ->
                cooldownValue.setText(cooldown.getProgress() + " s")));
        root.addView(cooldown, matchWrap());

        root.addView(label("Fuente del micrófono", 15, true));
        audioSource = spinner(new String[]{
                "VOICE_RECOGNITION (recomendado)",
                "MIC",
                "UNPROCESSED"
        });
        root.addView(audioSource, matchWrap());

        root.addView(label("Acción al detectar", 15, true));
        mode = spinner(new String[]{
                "Asistente de voz predeterminado",
                "ACTION_ASSIST",
                "Activity personalizada",
                "URI / deep link"
        });
        root.addView(mode, matchWrap());

        root.addView(label("Activity personalizada (package/class)", 14, false));
        component = new EditText(this);
        component.setSingleLine(true);
        component.setText("com.openai.chatgpt/com.openai.voice.assistant.AssistantActivity");
        root.addView(component, matchWrap());

        root.addView(label("URI / deep link", 14, false));
        uri = new EditText(this);
        uri.setSingleLine(true);
        uri.setHint("https://...");
        root.addView(uri, matchWrap());

        Button save = button("Guardar configuración");
        save.setOnClickListener(v -> {
            savePrefs();
            sendService(WakeService.ACTION_RELOAD);
            toast("Configuración guardada");
        });
        root.addView(save, matchWrap());

        Button start = button("Iniciar escucha");
        start.setOnClickListener(v -> {
            savePrefs();
            requestBasePermissions(true);
        });
        root.addView(start, matchWrap());

        Button stop = button("Detener escucha");
        stop.setOnClickListener(v -> sendService(WakeService.ACTION_STOP));
        root.addView(stop, matchWrap());

        Button test = button("Probar apertura del asistente");
        test.setOnClickListener(v -> {
            savePrefs();
            boolean ok = AssistantLauncher.launch(this, Prefs.get(this));
            String detail = AssistantLauncher.getLastDetail();
            toast(ok ? detail : "Error: " + detail);
        });
        root.addView(test, matchWrap());

        Button overlay = button("Permitir mostrar sobre otras apps");
        overlay.setOnClickListener(v -> openOverlaySettings(false));
        root.addView(overlay, matchWrap());

        Button battery = button("Abrir ajustes de optimización de batería");
        battery.setOnClickListener(v -> {
            try {
                startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
            } catch (Throwable t) {
                startActivity(new Intent(Settings.ACTION_SETTINGS));
            }
        });
        root.addView(battery, matchWrap());

        TextView note = label(
                "Nota: en Android 14/15/16 activá “Mostrar sobre otras apps” para que Sol Wake pueda abrir ChatGPT desde segundo plano. Al detectar la frase se libera el micrófono antes de abrir ChatGPT.",
                13, false);
        note.setPadding(0, dp(14), 0, 0);
        root.addView(note);

        return scroll;
    }

    private void loadPrefs() {
        SharedPreferences p = Prefs.get(this);
        phrase.setText(Prefs.phrase(p));
        sensitivity.setProgress(p.getInt(Prefs.SENSITIVITY, 65));
        sensitivityValue.setText(sensitivity.getProgress() + "%");
        screenOnly.setChecked(p.getBoolean(Prefs.SCREEN_ONLY, true));
        chargingOnly.setChecked(p.getBoolean(Prefs.CHARGING_ONLY, false));
        vibrate.setChecked(p.getBoolean(Prefs.VIBRATE, true));
        beep.setChecked(p.getBoolean(Prefs.BEEP, false));
        autoBoot.setChecked(p.getBoolean(Prefs.AUTO_BOOT, false));
        cooldown.setProgress(p.getInt(Prefs.COOLDOWN, 2));
        cooldownValue.setText(cooldown.getProgress() + " s");
        mode.setSelection(p.getInt(Prefs.MODE, 0));
        component.setText(p.getString(
                Prefs.COMPONENT,
                "com.openai.chatgpt/com.openai.voice.assistant.AssistantActivity"));
        uri.setText(p.getString(Prefs.URI, ""));
        audioSource.setSelection(p.getInt(Prefs.AUDIO_SOURCE, 0));
    }

    private void savePrefs() {
        Prefs.get(this).edit()
                .putString(Prefs.PHRASE, phrase.getText().toString().trim())
                .putInt(Prefs.SENSITIVITY, sensitivity.getProgress())
                .putBoolean(Prefs.SCREEN_ONLY, screenOnly.isChecked())
                .putBoolean(Prefs.CHARGING_ONLY, chargingOnly.isChecked())
                .putBoolean(Prefs.VIBRATE, vibrate.isChecked())
                .putBoolean(Prefs.BEEP, beep.isChecked())
                .putBoolean(Prefs.AUTO_BOOT, autoBoot.isChecked())
                .putInt(Prefs.COOLDOWN, cooldown.getProgress())
                .putInt(Prefs.MODE, mode.getSelectedItemPosition())
                .putString(Prefs.COMPONENT, component.getText().toString().trim())
                .putString(Prefs.URI, uri.getText().toString().trim())
                .putInt(Prefs.AUDIO_SOURCE, audioSource.getSelectedItemPosition())
                .apply();
    }

    private void requestBasePermissions(boolean thenStart) {
        startAfterPermission = thenStart;
        if (Build.VERSION.SDK_INT < 23) {
            if (thenStart) startWakeService();
            return;
        }

        boolean micMissing = checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                != PackageManager.PERMISSION_GRANTED;
        boolean notifMissing = Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED;

        if (!micMissing && !notifMissing) {
            if (thenStart) startWithOverlayCheck();
            return;
        }

        if (Build.VERSION.SDK_INT >= 33) {
            requestPermissions(new String[]{
                    Manifest.permission.RECORD_AUDIO,
                    Manifest.permission.POST_NOTIFICATIONS
            }, 40);
        } else {
            requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, 40);
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode == 40 && startAfterPermission) {
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                    == PackageManager.PERMISSION_GRANTED) {
                startWithOverlayCheck();
            } else {
                toast("Necesito permiso de micrófono para escuchar la frase");
            }
        }
        startAfterPermission = false;
    }

    private void startWithOverlayCheck() {
        if (Build.VERSION.SDK_INT >= 23 && !Settings.canDrawOverlays(this)) {
            startAfterOverlay = true;
            openOverlaySettings(true);
            return;
        }
        startAfterOverlay = false;
        startWakeService();
    }

    private void openOverlaySettings(boolean explain) {
        if (Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(this)) {
            toast("“Mostrar sobre otras apps” ya está habilitado");
            if (explain) startWakeService();
            return;
        }

        try {
            Intent i = new Intent(
                    Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:" + getPackageName()));
            startActivity(i);
            if (explain) {
                toast("Habilitá “Mostrar sobre otras apps” y volvé a Sol Wake");
            }
        } catch (Throwable t) {
            startActivity(new Intent(Settings.ACTION_SETTINGS));
        }
    }

    private void startWakeService() {
        Intent i = new Intent(this, WakeService.class);
        i.setAction(WakeService.ACTION_START);
        if (Build.VERSION.SDK_INT >= 26) startForegroundService(i);
        else startService(i);
        toast("Escucha iniciada");
    }

    private void sendService(String action) {
        Intent i = new Intent(this, WakeService.class);
        i.setAction(action);
        try {
            if (WakeService.isRunning()) {
                startService(i);
            } else if (WakeService.ACTION_STOP.equals(action)) {
                stopService(i);
            }
        } catch (Throwable ignored) {
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (startAfterOverlay
                && (Build.VERSION.SDK_INT < 23 || Settings.canDrawOverlays(this))) {
            startAfterOverlay = false;
            startWakeService();
        }
        handler.post(statusTicker);
    }

    @Override
    protected void onPause() {
        super.onPause();
        handler.removeCallbacks(statusTicker);
    }

    private final Runnable statusTicker = new Runnable() {
        @Override public void run() {
            status.setText("Estado: " + WakeService.getStatus());
            handler.postDelayed(this, 1000);
        }
    };

    private LinearLayout.LayoutParams matchWrap() {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.bottomMargin = dp(10);
        return lp;
    }

    private Button button(String text) {
        Button b = new Button(this);
        b.setText(text);
        return b;
    }

    private TextView label(String text, int sp, boolean bold) {
        TextView t = new TextView(this);
        t.setText(text);
        t.setTextSize(sp);
        if (bold) t.setTypeface(t.getTypeface(), android.graphics.Typeface.BOLD);
        return t;
    }

    private Spinner spinner(String[] items) {
        Spinner s = new Spinner(this);
        ArrayAdapter<String> a = new ArrayAdapter<>(
                this, android.R.layout.simple_spinner_item, items);
        a.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        s.setAdapter(a);
        return s;
    }

    private SeekBar.OnSeekBarChangeListener simpleSeek(Runnable onChange) {
        return new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                onChange.run();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) {}
            @Override public void onStopTrackingTouch(SeekBar seekBar) {}
        };
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private void toast(String value) {
        Toast.makeText(this, value, Toast.LENGTH_SHORT).show();
    }
}
