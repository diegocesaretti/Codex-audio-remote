package com.bwa3d.codexremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.text.Editable;
import android.text.TextWatcher;
import android.util.AttributeSet;
import android.widget.EditText;

public class PersistentEndPhrasesEditText extends EditText {
    private static final String DEFAULT_VALUE = "gracias sol, chau sol, adiós sol, listo sol";
    private boolean loading;

    public PersistentEndPhrasesEditText(Context context) { super(context); init(); }
    public PersistentEndPhrasesEditText(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public PersistentEndPhrasesEditText(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        loading = true;
        SharedPreferences p = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE);
        setText(p.getString("end_phrases", DEFAULT_VALUE));
        setSelection(length());
        loading = false;
        addTextChangedListener(new TextWatcher() {
            @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) { }
            @Override public void onTextChanged(CharSequence s, int start, int before, int count) { }
            @Override public void afterTextChanged(Editable s) {
                if (loading) return;
                getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putString("end_phrases", s == null ? "" : s.toString().trim()).apply();
            }
        });
    }
}
