package com.bwa3d.codexremote;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.widget.Toast;

/** Tiny transparent helper that lets each lifecycle cue keep access to a downloaded audio file. */
public class AudioCuePickerActivity extends Activity {
    public static final String EXTRA_CUE = "cue";
    private static final int REQUEST_AUDIO = 7001;
    private AudioCuePlayer.Cue cue = AudioCuePlayer.Cue.WAKE;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        String name = getIntent() == null ? null : getIntent().getStringExtra(EXTRA_CUE);
        if (name != null) {
            try { cue = AudioCuePlayer.Cue.valueOf(name); } catch (Exception ignored) { }
        }

        Intent pick = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        pick.addCategory(Intent.CATEGORY_OPENABLE);
        pick.setType("audio/*");
        pick.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        try {
            startActivityForResult(pick, REQUEST_AUDIO);
        } catch (Exception e) {
            Toast.makeText(this, "No se pudo abrir el selector de audio", Toast.LENGTH_LONG).show();
            finish();
        }
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_AUDIO && resultCode == RESULT_OK && data != null) {
            Uri uri = data.getData();
            if (uri != null) {
                try {
                    int flags = data.getFlags() &
                            (Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
                    getContentResolver().takePersistableUriPermission(uri, flags & Intent.FLAG_GRANT_READ_URI_PERMISSION);
                } catch (Exception e) {
                    AndroidDebugLog.log("Cue URI persist warning · " + e.getMessage());
                }
                AudioCuePlayer.setCustomUri(this, cue, uri);
                Toast.makeText(this, "Sonido seleccionado: " + AudioCuePlayer.describeSelection(this, cue), Toast.LENGTH_SHORT).show();
            }
        }
        finish();
    }
}
