package com.bwa3d.codexremote;

import android.content.Context;
import android.util.AttributeSet;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Spinner;

public class OverlayColorSpinner extends Spinner {
    private boolean loading = true;
    private static final String[] LABELS = { "Grafito", "Azul", "Verde", "Violeta", "Cálido", "Rojo oscuro" };

    public OverlayColorSpinner(Context context) { super(context); init(); }
    public OverlayColorSpinner(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public OverlayColorSpinner(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        ArrayAdapter<String> adapter = new ArrayAdapter<>(getContext(), android.R.layout.simple_spinner_item, LABELS);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        setAdapter(adapter);
        int saved = getContext().getSharedPreferences("settings", Context.MODE_PRIVATE).getInt("overlay_color", 0);
        setSelection(Math.max(0, Math.min(LABELS.length - 1, saved)), false);
        setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (loading) { loading = false; return; }
                getContext().getSharedPreferences("settings", Context.MODE_PRIVATE)
                        .edit().putInt("overlay_color", position).apply();
            }
            @Override public void onNothingSelected(AdapterView<?> parent) { }
        });
        post(() -> loading = false);
    }
}
