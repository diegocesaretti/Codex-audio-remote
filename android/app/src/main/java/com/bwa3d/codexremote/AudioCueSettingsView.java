package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

/** Self-contained settings panel for local wake/uplink/end chimes. */
public class AudioCueSettingsView extends LinearLayout {
    private final SharedPreferences prefs;
    private final TextView volumeLabel;

    public AudioCueSettingsView(Context context) { this(context, null); }

    public AudioCueSettingsView(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);

        CheckBox wake = check("Sonido al detectar wake word", "cue_wake_enabled", true);
        CheckBox uplink = check("Sonido cuando el uplink está listo", "cue_uplink_enabled", true);
        CheckBox end = check("Sonido al terminar la conversación", "cue_end_enabled", true);
        addView(wake);
        addView(uplink);
        addView(end);

        volumeLabel = new TextView(context);
        int current = Math.max(0, Math.min(100, prefs.getInt("cue_volume", 45)));
        updateVolumeLabel(current);
        addView(volumeLabel, matchWrap());

        SeekBar volume = new SeekBar(context);
        volume.setMax(100);
        volume.setProgress(current);
        volume.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                updateVolumeLabel(progress);
                if (fromUser) prefs.edit().putInt("cue_volume", progress).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        });
        addView(volume, matchWrap());

        LinearLayout tests = new LinearLayout(context);
        tests.setOrientation(HORIZONTAL);
        Button testWake = testButton("Wake", AudioCuePlayer.Cue.WAKE);
        Button testUp = testButton("Uplink", AudioCuePlayer.Cue.UPLINK);
        Button testEnd = testButton("Fin", AudioCuePlayer.Cue.END);
        tests.addView(testWake, weighted());
        tests.addView(testUp, weighted());
        tests.addView(testEnd, weighted());
        addView(tests, matchWrap());
    }

    private CheckBox check(String label, String key, boolean fallback) {
        CheckBox box = new CheckBox(getContext());
        box.setText(label);
        box.setChecked(prefs.getBoolean(key, fallback));
        box.setOnCheckedChangeListener((button, checked) -> prefs.edit().putBoolean(key, checked).apply());
        return box;
    }

    private Button testButton(String label, AudioCuePlayer.Cue cue) {
        Button button = new Button(getContext());
        button.setText(label);
        button.setAllCaps(false);
        button.setOnClickListener(v -> AudioCuePlayer.play(getContext(), cue));
        return button;
    }

    private void updateVolumeLabel(int value) {
        volumeLabel.setText("Volumen de sonidos: " + value + "%");
    }

    private static LayoutParams matchWrap() {
        return new LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
    }

    private static LayoutParams weighted() {
        return new LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f);
    }
}
