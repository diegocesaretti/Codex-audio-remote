package com.bwa3d.codexremote;

import android.content.Context;
import android.content.Intent;
import android.graphics.Typeface;
import android.os.Handler;
import android.os.Looper;
import android.text.InputType;
import android.util.AttributeSet;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.Spinner;
import android.widget.TextView;

import java.io.File;

/** Self-contained settings UI for selecting/configuring the wake engine. */
public class WakeEngineSettingsView extends LinearLayout {
    private static final String ACTION_WAKE_CHANGED = "com.bwa3d.codexremote.WAKE_WORD_CHANGED";

    private final Handler main = new Handler(Looper.getMainLooper());
    private Spinner engineSpinner;
    private EditText accessKeyEdit;
    private TextView status;
    private Button trainButton;
    private boolean loading;

    public WakeEngineSettingsView(Context context) { super(context); init(); }
    public WakeEngineSettingsView(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public WakeEngineSettingsView(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        setOrientation(VERTICAL);
        int pad = dp(8);
        setPadding(0, pad, 0, pad);

        TextView title = new TextView(getContext());
        title.setText("Motor de wake");
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        addView(title, fullWrap());

        engineSpinner = new Spinner(getContext());
        String[] engines = { "Porcupine · recomendado", "Vosk · compatibilidad" };
        ArrayAdapter<String> adapter = new ArrayAdapter<>(getContext(), android.R.layout.simple_spinner_item, engines);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        engineSpinner.setAdapter(adapter);
        addView(engineSpinner, fullWrap());

        TextView help = new TextView(getContext());
        help.setText("Porcupine reduce falsos positivos. La AccessKey queda guardada sólo en este Android y no se sube a GitHub.");
        addView(help, fullWrap());

        accessKeyEdit = new EditText(getContext());
        accessKeyEdit.setHint("Picovoice AccessKey");
        accessKeyEdit.setSingleLine(true);
        accessKeyEdit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        addView(accessKeyEdit, fullWrap());

        trainButton = new Button(getContext());
        trainButton.setText("Preparar / actualizar wake Porcupine");
        addView(trainButton, fullWrap());

        status = new TextView(getContext());
        addView(status, fullWrap());

        loading = true;
        String engine = PorcupineWakeSupport.selectedEngine(getContext());
        engineSpinner.setSelection(PorcupineWakeSupport.ENGINE_VOSK.equals(engine) ? 1 : 0);
        accessKeyEdit.setText(PorcupineWakeSupport.accessKey(getContext()));
        loading = false;

        engineSpinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (loading) return;
                String selected = position == 1 ? PorcupineWakeSupport.ENGINE_VOSK : PorcupineWakeSupport.ENGINE_PORCUPINE;
                PorcupineWakeSupport.prefs(getContext()).edit().putString("wake_engine", selected).apply();
                refreshStatus();
                broadcastWakeChanged();
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });

        trainButton.setOnClickListener(v -> trainWake());
        refreshStatus();
    }

    private void trainWake() {
        final String key = accessKeyEdit.getText() == null ? "" : accessKeyEdit.getText().toString().trim();
        final String phrase = PorcupineWakeSupport.prefs(getContext()).getString("wake_word", "hola sol");
        PorcupineWakeSupport.prefs(getContext()).edit().putString("porcupine_access_key", key).apply();

        trainButton.setEnabled(false);
        status.setText("Creando modelo ‘" + phrase + "’… requiere Internet sólo para este paso.");
        new Thread(() -> {
            try {
                PorcupineWakeSupport.train(getContext().getApplicationContext(), key, phrase);
                main.post(() -> {
                    loading = true;
                    engineSpinner.setSelection(0);
                    loading = false;
                    trainButton.setEnabled(true);
                    refreshStatus();
                    broadcastWakeChanged();
                });
            } catch (Exception e) {
                AndroidDebugLog.log("Porcupine training failed: " + e);
                main.post(() -> {
                    trainButton.setEnabled(true);
                    status.setText("No pude preparar Porcupine: " + friendlyError(e));
                });
            }
        }, "PorcupineTrainWake").start();
    }

    private void refreshStatus() {
        boolean selected = PorcupineWakeSupport.isSelected(getContext());
        boolean configured = PorcupineWakeSupport.isConfigured(getContext());
        String trained = PorcupineWakeSupport.trainedPhrase(getContext());
        File file = PorcupineWakeSupport.keywordFile(getContext());

        if (!selected) {
            status.setText("Activo: Vosk. Podés volver a Porcupine cuando quieras.");
        } else if (!configured) {
            status.setText("Porcupine seleccionado pero todavía no configurado: mientras tanto se usará Vosk.");
        } else {
            int sensitivity = PorcupineWakeSupport.prefs(getContext()).getInt("sensitivity", 60);
            status.setText("Porcupine listo · wake ‘" + (trained == null || trained.isEmpty() ? "personalizado" : trained)
                    + "’ · sensibilidad " + sensitivity + "% · modelo " + (file.length() / 1024) + " KB");
        }
    }

    private void broadcastWakeChanged() {
        try { getContext().sendBroadcast(new Intent(ACTION_WAKE_CHANGED).setPackage(getContext().getPackageName())); }
        catch (Exception ignored) { }
    }

    private static String friendlyError(Exception e) {
        String message = e.getMessage();
        if (message == null || message.trim().isEmpty()) return e.getClass().getSimpleName();
        return message;
    }

    private LayoutParams fullWrap() {
        return new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
