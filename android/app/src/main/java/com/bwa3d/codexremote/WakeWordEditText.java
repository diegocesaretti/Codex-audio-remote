package com.bwa3d.codexremote;

import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.TextWatcher;
import android.util.AttributeSet;
import android.widget.EditText;

public class WakeWordEditText extends EditText {
    public static final String ACTION_WAKE_WORD_CHANGED = "com.bwa3d.codexremote.WAKE_WORD_CHANGED";
    private final Handler handler = new Handler(Looper.getMainLooper());
    private boolean loading;
    private final Runnable broadcastChange = () -> {
        try { getContext().sendBroadcast(new Intent(ACTION_WAKE_WORD_CHANGED).setPackage(getContext().getPackageName())); }
        catch (Exception ignored) { }
    };

    public WakeWordEditText(Context context) { super(context); init(); }
    public WakeWordEditText(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public WakeWordEditText(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        loading = true;
        SharedPreferences p = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE);
        setText(p.getString("wake_word", "hola sol"));
        setSelection(length());
        loading = false;
        addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) {
                if (loading) return;
                String value = s == null ? "" : s.toString().trim().toLowerCase(java.util.Locale.ROOT).replaceAll("\\s+", " ");
                if (value.isEmpty()) value = "hola sol";
                getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putString("wake_word", value).apply();
                handler.removeCallbacks(broadcastChange);
                handler.postDelayed(broadcastChange, 600);
            }
        });
    }
}
