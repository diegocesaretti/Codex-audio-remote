package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.widget.CheckBox;

public class WakeScreenCheckBox extends CheckBox {
    public WakeScreenCheckBox(Context context) { this(context, null); }

    public WakeScreenCheckBox(Context context, AttributeSet attrs) {
        super(context, attrs);
        SharedPreferences prefs = context.getSharedPreferences("settings", Context.MODE_PRIVATE);
        setChecked(prefs.getBoolean("wake_screen_on", true));
        setOnCheckedChangeListener((buttonView, isChecked) ->
                prefs.edit().putBoolean("wake_screen_on", isChecked).apply());
    }
}
