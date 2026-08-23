package com.bwa3d.codexremote;

import android.app.AlertDialog;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.graphics.Color;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.AttributeSet;
import android.view.View;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

/** Self-contained settings panel for the fullscreen wake GIF overlay. */
public class WakeGifSettingsView extends LinearLayout {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private CheckBox enabled;
    private CheckBox keepListening;
    private TextView selectedGif;
    private Button colorButton;
    private Spinner scaleSpinner;
    private WakeGifOverlayController previewController;
    private boolean receiverRegistered;

    private final BroadcastReceiver gifChangedReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) { refresh(); }
    };

    public WakeGifSettingsView(Context context) { super(context); init(); }
    public WakeGifSettingsView(Context context, AttributeSet attrs) { super(context, attrs); init(); }
    public WakeGifSettingsView(Context context, AttributeSet attrs, int defStyleAttr) { super(context, attrs, defStyleAttr); init(); }

    private void init() {
        setOrientation(VERTICAL);
        int gap = dp(6);

        enabled = new CheckBox(getContext());
        enabled.setText("Usar GIF fullscreen al detectar wake");
        enabled.setChecked(WakeGifPrefs.enabled(getContext()));
        enabled.setOnCheckedChangeListener((buttonView, isChecked) -> WakeGifPrefs.setEnabled(getContext(), isChecked));
        addView(enabled, matchWrap());

        selectedGif = new TextView(getContext());
        addView(selectedGif, matchWrap());

        Button choose = new Button(getContext());
        choose.setText("Elegir GIF local");
        choose.setOnClickListener(v -> {
            Intent i = new Intent(getContext(), WakeGifPickerActivity.class);
            i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            getContext().startActivity(i);
        });
        addView(choose, matchWrap(gap));

        Button clear = new Button(getContext());
        clear.setText("Quitar GIF seleccionado");
        clear.setOnClickListener(v -> {
            WakeGifPrefs.clearUri(getContext());
            refresh();
        });
        addView(clear, matchWrap());

        TextView scaleLabel = new TextView(getContext());
        scaleLabel.setText("Ajuste del GIF");
        addView(scaleLabel, matchWrap(gap));

        scaleSpinner = new Spinner(getContext());
        ArrayAdapter<String> scaleAdapter = new ArrayAdapter<>(getContext(), android.R.layout.simple_spinner_item,
                new String[]{"Contain · ver GIF completo", "Cover · llenar pantalla"});
        scaleAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        scaleSpinner.setAdapter(scaleAdapter);
        scaleSpinner.setSelection("cover".equals(WakeGifPrefs.scaleMode(getContext())) ? 1 : 0);
        scaleSpinner.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(android.widget.AdapterView<?> parent, View view, int position, long id) {
                WakeGifPrefs.setScaleMode(getContext(), position == 1 ? "cover" : "contain");
            }
            @Override public void onNothingSelected(android.widget.AdapterView<?> parent) { }
        });
        addView(scaleSpinner, matchWrap());

        colorButton = new Button(getContext());
        colorButton.setOnClickListener(v -> showColorDialog());
        addView(colorButton, matchWrap(gap));

        keepListening = new CheckBox(getContext());
        keepListening.setText("Mantener GIF visible mientras Sol está escuchando");
        keepListening.setChecked(WakeGifPrefs.keepDuringListening(getContext()));
        keepListening.setOnCheckedChangeListener((buttonView, isChecked) -> WakeGifPrefs.setKeepDuringListening(getContext(), isChecked));
        addView(keepListening, matchWrap());

        Button preview = new Button(getContext());
        preview.setText("Probar overlay GIF");
        preview.setOnClickListener(v -> preview());
        addView(preview, matchWrap(gap));

        TextView help = new TextView(getContext());
        help.setText("El GIF se guarda como acceso al archivo local. El overlay entra y sale con un fade corto. Requiere permiso de overlay sobre otras apps.");
        addView(help, matchWrap());

        refresh();
    }

    private void refresh() {
        if (selectedGif != null) selectedGif.setText("GIF: " + WakeGifPrefs.describeGif(getContext()));
        if (colorButton != null) {
            int color = WakeGifPrefs.backgroundColor(getContext());
            colorButton.setText("Color de fondo: " + WakeGifPrefs.colorHex(getContext()));
            colorButton.setBackgroundColor(color);
            colorButton.setTextColor(luminance(color) > 0.56 ? Color.BLACK : Color.WHITE);
        }
    }

    private void preview() {
        if (WakeGifPrefs.uri(getContext()) == null) {
            Toast.makeText(getContext(), "Primero elegí un GIF local", Toast.LENGTH_SHORT).show();
            return;
        }
        if (previewController != null) previewController.destroy();
        previewController = new WakeGifOverlayController(getContext());
        if (!previewController.canShow()) {
            Toast.makeText(getContext(), "Falta permitir overlay sobre otras apps", Toast.LENGTH_LONG).show();
            return;
        }
        previewController.showPreview();
        final WakeGifOverlayController controller = previewController;
        handler.postDelayed(() -> {
            controller.hide();
            handler.postDelayed(() -> {
                if (previewController == controller) {
                    controller.destroy();
                    previewController = null;
                }
            }, 400L);
        }, 2600L);
    }

    private void showColorDialog() {
        int initial = WakeGifPrefs.backgroundColor(getContext());
        LinearLayout box = new LinearLayout(getContext());
        box.setOrientation(VERTICAL);
        int pad = dp(18);
        box.setPadding(pad, pad, pad, 0);

        TextView preview = new TextView(getContext());
        preview.setText("Vista del fondo");
        preview.setGravity(android.view.Gravity.CENTER);
        preview.setMinHeight(dp(70));
        box.addView(preview, matchWrap());

        SeekBar red = colorBar(box, "Rojo", Color.red(initial));
        SeekBar green = colorBar(box, "Verde", Color.green(initial));
        SeekBar blue = colorBar(box, "Azul", Color.blue(initial));

        Runnable paint = () -> {
            int color = Color.rgb(red.getProgress(), green.getProgress(), blue.getProgress());
            preview.setBackgroundColor(color);
            preview.setTextColor(luminance(color) > 0.56 ? Color.BLACK : Color.WHITE);
            preview.setText(String.format("#%06X", 0xFFFFFF & color));
        };
        SeekBar.OnSeekBarChangeListener listener = new SeekBar.OnSeekBarChangeListener() {
            @Override public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) { paint.run(); }
            @Override public void onStartTrackingTouch(SeekBar seekBar) { }
            @Override public void onStopTrackingTouch(SeekBar seekBar) { }
        };
        red.setOnSeekBarChangeListener(listener);
        green.setOnSeekBarChangeListener(listener);
        blue.setOnSeekBarChangeListener(listener);
        paint.run();

        new AlertDialog.Builder(getContext())
                .setTitle("Color de fondo del wake overlay")
                .setView(box)
                .setNegativeButton("Cancelar", null)
                .setPositiveButton("Usar color", (dialog, which) -> {
                    int color = Color.rgb(red.getProgress(), green.getProgress(), blue.getProgress());
                    WakeGifPrefs.setBackgroundColor(getContext(), color);
                    refresh();
                })
                .show();
    }

    private SeekBar colorBar(LinearLayout parent, String label, int value) {
        TextView text = new TextView(getContext());
        text.setText(label);
        parent.addView(text, matchWrap(dp(8)));
        SeekBar bar = new SeekBar(getContext());
        bar.setMax(255);
        bar.setProgress(value);
        parent.addView(bar, matchWrap());
        return bar;
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (!receiverRegistered) {
            IntentFilter filter = new IntentFilter(WakeGifPickerActivity.ACTION_CHANGED);
            if (Build.VERSION.SDK_INT >= 33) getContext().registerReceiver(gifChangedReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
            else getContext().registerReceiver(gifChangedReceiver, filter);
            receiverRegistered = true;
        }
        refresh();
    }

    @Override protected void onDetachedFromWindow() {
        if (receiverRegistered) {
            try { getContext().unregisterReceiver(gifChangedReceiver); } catch (Exception ignored) { }
            receiverRegistered = false;
        }
        handler.removeCallbacksAndMessages(null);
        if (previewController != null) {
            previewController.destroy();
            previewController = null;
        }
        super.onDetachedFromWindow();
    }

    private LayoutParams matchWrap() { return matchWrap(0); }
    private LayoutParams matchWrap(int topMargin) {
        LayoutParams lp = new LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT);
        lp.topMargin = topMargin;
        return lp;
    }
    private int dp(int value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private static double luminance(int color) {
        return (0.2126 * Color.red(color) + 0.7152 * Color.green(color) + 0.0722 * Color.blue(color)) / 255.0;
    }
}
