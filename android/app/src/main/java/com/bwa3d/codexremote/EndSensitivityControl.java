package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

public class EndSensitivityControl extends LinearLayout {
    private final TextView label;
    private final SeekBar seek;

    public EndSensitivityControl(Context context) { this(context, null); }

    public EndSensitivityControl(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        label = new TextView(context);
        seek = new SeekBar(context);
        seek.setMax(100);
        addView(label, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        addView(seek, new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        SharedPreferences prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        int value = Math.max(0, Math.min(100, prefs.getInt("end_sensitivity", 30)));
        seek.setProgress(value);
        updateLabel(value);
        seek.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                updateLabel(progress);
                if (fromUser) prefs.edit().putInt("end_sensitivity", progress).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        });
    }

    private void updateLabel(int value) {
        double required = RemoteService.endPhraseMinConfidence(value);
        String mode = value <= 10 ? "Muy estricta" : value <= 35 ? "Estricta" : value <= 65 ? "Normal" : "Permisiva";
        label.setText("Sensibilidad frases de fin: " + value + "% · " + mode + " · confianza mín. " + Math.round(required * 100.0) + "%");
    }
}
