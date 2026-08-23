package com.bwa3d.codexremote;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.TextView;

import java.util.EnumMap;

/** Settings panel for wake/uplink/listening-end/conversation-end sounds and downloaded audio files. */
public class AudioCueSettingsView extends LinearLayout {
    private final SharedPreferences prefs;
    private final TextView volumeLabel;
    private final EnumMap<AudioCuePlayer.Cue, TextView> selectionLabels = new EnumMap<>(AudioCuePlayer.Cue.class);

    public AudioCueSettingsView(Context context) { this(context, null); }

    public AudioCueSettingsView(Context context, AttributeSet attrs) {
        super(context, attrs);
        setOrientation(VERTICAL);
        prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);

        addView(check("Sonido al detectar wake word", "cue_wake_enabled", true));
        addCueRow("Wake", AudioCuePlayer.Cue.WAKE);

        addView(check("Sonido cuando el uplink está listo", "cue_uplink_enabled", true));
        addCueRow("Uplink listo", AudioCuePlayer.Cue.UPLINK);

        addView(check("Sonido al terminar la escucha / cerrar micrófono", "cue_listen_end_enabled", true));
        addCueRow("Fin escucha", AudioCuePlayer.Cue.LISTEN_END);

        addView(check("Sonido al cerrar completamente la conversación", "cue_end_enabled", true));
        addCueRow("Fin conversación", AudioCuePlayer.Cue.END);

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

        TextView help = new TextView(context);
        help.setText("Podés usar MP3, WAV, OGG, M4A u otro audio que Android pueda reproducir. El permiso al archivo elegido queda guardado sin dar acceso general al almacenamiento.");
        addView(help, matchWrap());
    }

    private void addCueRow(String label, AudioCuePlayer.Cue cue) {
        TextView selected = new TextView(getContext());
        selected.setText(label + ": " + AudioCuePlayer.describeSelection(getContext(), cue));
        selectionLabels.put(cue, selected);
        addView(selected, matchWrap());

        LinearLayout row = new LinearLayout(getContext());
        row.setOrientation(HORIZONTAL);

        Button choose = new Button(getContext());
        choose.setText("Elegir");
        choose.setAllCaps(false);
        choose.setOnClickListener(v -> {
            Intent intent = new Intent(getContext(), AudioCuePickerActivity.class);
            intent.putExtra(AudioCuePickerActivity.EXTRA_CUE, cue.name());
            getContext().startActivity(intent);
        });

        Button builtIn = new Button(getContext());
        builtIn.setText("Interno");
        builtIn.setAllCaps(false);
        builtIn.setOnClickListener(v -> {
            AudioCuePlayer.clearCustomUri(getContext(), cue);
            refreshSelection(cue, label);
        });

        Button test = new Button(getContext());
        test.setText("Probar");
        test.setAllCaps(false);
        test.setOnClickListener(v -> AudioCuePlayer.play(getContext(), cue));

        row.addView(choose, weighted());
        row.addView(builtIn, weighted());
        row.addView(test, weighted());
        addView(row, matchWrap());
    }

    @Override public void onWindowFocusChanged(boolean hasWindowFocus) {
        super.onWindowFocusChanged(hasWindowFocus);
        if (hasWindowFocus) refreshAllSelections();
    }

    private void refreshAllSelections() {
        refreshSelection(AudioCuePlayer.Cue.WAKE, "Wake");
        refreshSelection(AudioCuePlayer.Cue.UPLINK, "Uplink listo");
        refreshSelection(AudioCuePlayer.Cue.LISTEN_END, "Fin escucha");
        refreshSelection(AudioCuePlayer.Cue.END, "Fin conversación");
    }

    private void refreshSelection(AudioCuePlayer.Cue cue, String label) {
        TextView view = selectionLabels.get(cue);
        if (view != null) view.setText(label + ": " + AudioCuePlayer.describeSelection(getContext(), cue));
    }

    private CheckBox check(String label, String key, boolean fallback) {
        CheckBox box = new CheckBox(getContext());
        box.setText(label);
        box.setChecked(prefs.getBoolean(key, fallback));
        box.setOnCheckedChangeListener((button, checked) -> prefs.edit().putBoolean(key, checked).apply());
        return box;
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
