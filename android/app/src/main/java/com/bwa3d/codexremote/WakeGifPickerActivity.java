package com.bwa3d.codexremote;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.widget.Toast;

/** Transparent helper activity that grants persistent access to a user-selected local GIF. */
public class WakeGifPickerActivity extends Activity {
    public static final String ACTION_CHANGED = "com.bwa3d.codexremote.WAKE_GIF_CHANGED";
    private static final int REQUEST_GIF = 7101;

    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent pick = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        pick.addCategory(Intent.CATEGORY_OPENABLE);
        pick.setType("image/gif");
        pick.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        try {
            startActivityForResult(pick, REQUEST_GIF);
        } catch (Exception e) {
            Toast.makeText(this, "No se pudo abrir el selector de GIF", Toast.LENGTH_LONG).show();
            finish();
        }
    }

    @Override protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQUEST_GIF && resultCode == RESULT_OK && data != null) {
            Uri uri = data.getData();
            if (uri != null) {
                try {
                    int flags = data.getFlags() & Intent.FLAG_GRANT_READ_URI_PERMISSION;
                    getContentResolver().takePersistableUriPermission(uri, flags);
                } catch (Exception e) {
                    AndroidDebugLog.log("Wake GIF URI persist warning · " + e.getMessage());
                }
                WakeGifPrefs.setUri(this, uri);
                Intent changed = new Intent(ACTION_CHANGED);
                changed.setPackage(getPackageName());
                sendBroadcast(changed);
                Toast.makeText(this, "GIF seleccionado", Toast.LENGTH_SHORT).show();
            }
        }
        finish();
    }
}
