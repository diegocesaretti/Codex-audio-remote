package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.util.AttributeSet;
import android.widget.CheckBox;

public class StartOnOpenCheckBox extends CheckBox {
    public StartOnOpenCheckBox(Context context) { super(context); init(); }
    public StartOnOpenCheckBox(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public StartOnOpenCheckBox(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        SharedPreferences p = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE);
        setChecked(p.getBoolean("start_on_open", false));
        setOnCheckedChangeListener((buttonView, isChecked) ->
                getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putBoolean("start_on_open", isChecked).apply());
    }
}
