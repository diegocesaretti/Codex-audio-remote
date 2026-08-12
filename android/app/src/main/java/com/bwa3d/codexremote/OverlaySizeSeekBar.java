package com.bwa3d.codexremote;

import android.content.Context;
import android.util.AttributeSet;
import android.widget.SeekBar;

public class OverlaySizeSeekBar extends SeekBar {
    public OverlaySizeSeekBar(Context context) { super(context); init(); }
    public OverlaySizeSeekBar(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public OverlaySizeSeekBar(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        setMax(100);
        setProgress(getContext().getSharedPreferences("settings", Context.MODE_PRIVATE).getInt("overlay_size", 55));
        setOnSeekBarChangeListener(new OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                if (fromUser) getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putInt("overlay_size", progress).apply();
            }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        });
    }
}
