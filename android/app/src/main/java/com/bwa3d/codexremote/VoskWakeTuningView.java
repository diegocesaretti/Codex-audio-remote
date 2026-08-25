package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.graphics.Typeface;
import android.util.AttributeSet;
import android.view.View;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

import java.util.Locale;

/**
 * Advanced live controls for the Vosk post-recognition wake gate.
 * Values are persisted immediately; VoskWakeGate reads them on every decision.
 */
public class VoskWakeTuningView extends LinearLayout {
    private static final String PREFS = "settings";

    private SharedPreferences prefs;
    private CheckBox enabled;
    private LinearLayout panel;
    private CheckBox requireFinal;
    private CheckBox requirePrefix;
    private CheckBox audioEvidence;
    private CheckBox adaptiveNoise;

    public VoskWakeTuningView(Context context) { super(context); init(); }
    public VoskWakeTuningView(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public VoskWakeTuningView(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        setOrientation(VERTICAL);
        setPadding(0, dp(8), 0, dp(8));
        prefs = getContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);

        TextView title = new TextView(getContext());
        title.setText("Vosk · laboratorio de detección");
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        addView(title, fullWrap());

        TextView help = new TextView(getContext());
        help.setText("Sólo afecta Vosk. Los cambios se aplican al siguiente intento, sin reiniciar el servicio. Bajá filtros para detectar más fácil; subilos para reducir falsos positivos.");
        addView(help, fullWrap());

        enabled = new CheckBox(getContext());
        enabled.setText("Usar ajustes avanzados Vosk");
        enabled.setChecked(prefs.getBoolean("vosk_advanced", false));
        addView(enabled, fullWrap());

        panel = new LinearLayout(getContext());
        panel.setOrientation(VERTICAL);
        addView(panel, fullWrap());

        requireFinal = addCheck("Exigir resultado FINAL (recomendado)", "vosk_require_final", true);
        requirePrefix = addCheck("Exigir progresión de prefijo (hola → hola sol)", "vosk_require_prefix", true);
        audioEvidence = addCheck("Exigir evidencia acústica / RMS", "vosk_audio_evidence", true);
        adaptiveNoise = addCheck("Usar piso de ruido adaptativo", "vosk_adaptive_noise", true);

        addSlider("Confianza mínima", "vosk_min_conf_pct", 0, 100, 79, ValueType.PERCENT);
        addSlider("RMS mínimo absoluto", "vosk_min_rms", 0, 1200, 70, ValueType.INTEGER);
        addSlider("Factor sobre piso de ruido", "vosk_noise_factor_pct", 100, 300, 159, ValueType.FACTOR);
        addSlider("Ventana de audio", "vosk_audio_window_ms", 250, 4000, 1800, ValueType.MS);
        addSlider("Duración mínima del wake", "vosk_min_duration_ms", 0, 1500, 300, ValueType.MS);
        addSlider("Duración máxima del wake", "vosk_max_duration_ms", 500, 5000, 1900, ValueType.MS);
        addSlider("Prefijo → final mínimo", "vosk_prefix_min_ms", 0, 1000, 120, ValueType.MS);
        addSlider("Prefijo → final máximo", "vosk_prefix_max_ms", 250, 4000, 1800, ValueType.MS);
        addSlider("Parciales exactos requeridos", "vosk_min_exact_partials", 0, 5, 0, ValueType.INTEGER);
        addSlider("Timeout de candidato", "vosk_candidate_timeout_ms", 300, 5000, 2400, ValueType.MS);
        addSlider("Cooldown después de aceptar", "vosk_cooldown_ms", 0, 10000, 3500, ValueType.MS);

        TextView presetsTitle = new TextView(getContext());
        presetsTitle.setText("Presets rápidos");
        presetsTitle.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        presetsTitle.setPadding(0, dp(8), 0, 0);
        panel.addView(presetsTitle, fullWrap());

        Button sensitive = new Button(getContext());
        sensitive.setText("Sensible · mantiene protecciones");
        sensitive.setOnClickListener(v -> applyPreset("sensitive"));
        panel.addView(sensitive, fullWrap());

        Button current = new Button(getContext());
        current.setText("Base actual · volver al comportamiento conocido");
        current.setOnClickListener(v -> applyPreset("base"));
        panel.addView(current, fullWrap());

        Button strict = new Button(getContext());
        strict.setText("Estricto · menos falsos positivos");
        strict.setOnClickListener(v -> applyPreset("strict"));
        panel.addView(strict, fullWrap());

        Button extreme = new Button(getContext());
        extreme.setText("EXTREMO · máxima detección / muchos falsos positivos");
        extreme.setOnClickListener(v -> applyPreset("extreme"));
        panel.addView(extreme, fullWrap());

        enabled.setOnCheckedChangeListener((buttonView, isChecked) -> {
            prefs.edit().putBoolean("vosk_advanced", isChecked).apply();
            panel.setVisibility(isChecked ? View.VISIBLE : View.GONE);
            AndroidDebugLog.log("Vosk advanced tuning=" + isChecked);
        });
        panel.setVisibility(enabled.isChecked() ? View.VISIBLE : View.GONE);
    }

    private CheckBox addCheck(String label, String key, boolean defaultValue) {
        CheckBox box = new CheckBox(getContext());
        box.setText(label);
        box.setChecked(prefs.getBoolean(key, defaultValue));
        box.setOnCheckedChangeListener((buttonView, isChecked) -> {
            prefs.edit().putBoolean(key, isChecked).apply();
            AndroidDebugLog.log("Vosk tuning " + key + "=" + isChecked);
        });
        panel.addView(box, fullWrap());
        return box;
    }

    private void addSlider(String label, String key, int min, int max, int defaultValue, ValueType type) {
        TextView text = new TextView(getContext());
        int value = clamp(prefs.getInt(key, defaultValue), min, max);
        text.setText(formatLabel(label, value, type));
        panel.addView(text, fullWrap());

        SeekBar seek = new SeekBar(getContext());
        seek.setMax(max - min);
        seek.setProgress(value - min);
        seek.setTag(new SliderTag(label, key, min, max, defaultValue, type, text));
        seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                SliderTag tag = (SliderTag)seekBar.getTag();
                int v = tag.min + progress;
                tag.labelView.setText(formatLabel(tag.label, v, tag.type));
                if (fromUser) prefs.edit().putInt(tag.key, v).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) {
                SliderTag tag = (SliderTag)seekBar.getTag();
                int v = tag.min + seekBar.getProgress();
                AndroidDebugLog.log("Vosk tuning " + tag.key + "=" + v);
            }
        });
        panel.addView(seek, fullWrap());
    }

    private void applyPreset(String preset) {
        SharedPreferences.Editor e = prefs.edit().putBoolean("vosk_advanced", true);
        if ("extreme".equals(preset)) {
            e.putBoolean("vosk_require_final", false)
                    .putBoolean("vosk_require_prefix", false)
                    .putBoolean("vosk_audio_evidence", false)
                    .putBoolean("vosk_adaptive_noise", false)
                    .putInt("vosk_min_conf_pct", 0)
                    .putInt("vosk_min_rms", 0)
                    .putInt("vosk_noise_factor_pct", 100)
                    .putInt("vosk_audio_window_ms", 3500)
                    .putInt("vosk_min_duration_ms", 0)
                    .putInt("vosk_max_duration_ms", 4500)
                    .putInt("vosk_prefix_min_ms", 0)
                    .putInt("vosk_prefix_max_ms", 3500)
                    .putInt("vosk_min_exact_partials", 0)
                    .putInt("vosk_candidate_timeout_ms", 4500)
                    .putInt("vosk_cooldown_ms", 800);
        } else if ("sensitive".equals(preset)) {
            e.putBoolean("vosk_require_final", true)
                    .putBoolean("vosk_require_prefix", true)
                    .putBoolean("vosk_audio_evidence", true)
                    .putBoolean("vosk_adaptive_noise", true)
                    .putInt("vosk_min_conf_pct", 62)
                    .putInt("vosk_min_rms", 25)
                    .putInt("vosk_noise_factor_pct", 118)
                    .putInt("vosk_audio_window_ms", 2600)
                    .putInt("vosk_min_duration_ms", 100)
                    .putInt("vosk_max_duration_ms", 3000)
                    .putInt("vosk_prefix_min_ms", 0)
                    .putInt("vosk_prefix_max_ms", 2600)
                    .putInt("vosk_min_exact_partials", 0)
                    .putInt("vosk_candidate_timeout_ms", 3400)
                    .putInt("vosk_cooldown_ms", 1800);
        } else if ("strict".equals(preset)) {
            e.putBoolean("vosk_require_final", true)
                    .putBoolean("vosk_require_prefix", true)
                    .putBoolean("vosk_audio_evidence", true)
                    .putBoolean("vosk_adaptive_noise", true)
                    .putInt("vosk_min_conf_pct", 88)
                    .putInt("vosk_min_rms", 100)
                    .putInt("vosk_noise_factor_pct", 190)
                    .putInt("vosk_audio_window_ms", 1500)
                    .putInt("vosk_min_duration_ms", 350)
                    .putInt("vosk_max_duration_ms", 1700)
                    .putInt("vosk_prefix_min_ms", 150)
                    .putInt("vosk_prefix_max_ms", 1500)
                    .putInt("vosk_min_exact_partials", 1)
                    .putInt("vosk_candidate_timeout_ms", 2000)
                    .putInt("vosk_cooldown_ms", 4500);
        } else {
            e.putBoolean("vosk_require_final", true)
                    .putBoolean("vosk_require_prefix", true)
                    .putBoolean("vosk_audio_evidence", true)
                    .putBoolean("vosk_adaptive_noise", true)
                    .putInt("vosk_min_conf_pct", 79)
                    .putInt("vosk_min_rms", 70)
                    .putInt("vosk_noise_factor_pct", 159)
                    .putInt("vosk_audio_window_ms", 1800)
                    .putInt("vosk_min_duration_ms", 300)
                    .putInt("vosk_max_duration_ms", 1900)
                    .putInt("vosk_prefix_min_ms", 120)
                    .putInt("vosk_prefix_max_ms", 1800)
                    .putInt("vosk_min_exact_partials", 0)
                    .putInt("vosk_candidate_timeout_ms", 2400)
                    .putInt("vosk_cooldown_ms", 3500);
        }
        e.apply();
        AndroidDebugLog.log("Vosk tuning preset=" + preset);
        rebuild();
    }

    private void rebuild() {
        removeAllViews();
        init();
    }

    private static String formatLabel(String label, int value, ValueType type) {
        switch (type) {
            case PERCENT: return label + ": " + value + "%";
            case FACTOR: return label + ": " + String.format(Locale.US, "%.2f×", value / 100.0);
            case MS: return label + ": " + value + " ms";
            default: return label + ": " + value;
        }
    }

    private LayoutParams fullWrap() {
        return new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static int clamp(int value, int min, int max) { return Math.max(min, Math.min(max, value)); }

    private enum ValueType { INTEGER, PERCENT, FACTOR, MS }

    private static final class SliderTag {
        final String label;
        final String key;
        final int min;
        final int max;
        final int defaultValue;
        final ValueType type;
        final TextView labelView;

        SliderTag(String label, String key, int min, int max, int defaultValue, ValueType type, TextView labelView) {
            this.label = label;
            this.key = key;
            this.min = min;
            this.max = max;
            this.defaultValue = defaultValue;
            this.type = type;
            this.labelView = labelView;
        }
    }
}