package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.widget.CheckBox;

public class PreferenceCheckBox extends CheckBox {
    public PreferenceCheckBox(Context context) { super(context); init(); }
    public PreferenceCheckBox(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public PreferenceCheckBox(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        SharedPreferences p = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE);
        setChecked(p.getBoolean("show_transcript", false));
        setOnCheckedChangeListener((buttonView, isChecked) ->
                getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putBoolean("show_transcript", isChecked).apply());
    }
}
