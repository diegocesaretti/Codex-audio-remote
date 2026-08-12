package com.bwa3d.codexremote;

import android.app.Application;
import android.content.Intent;
import android.os.Build;

import java.io.BufferedInputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;

public class CodexRemoteApp extends Application {
    @Override public void onCreate() {
        super.onCreate();
        AndroidDebugLog.install(this);
        AndroidDebugLog.log("Application started · API " + Build.VERSION.SDK_INT);
        if (Build.VERSION.SDK_INT <= 23) {
            try {
                startService(new Intent(this, LegacyKeepAliveService.class));
                AndroidDebugLog.log("API23 keep-alive service requested");
            } catch (Exception e) {
                AndroidDebugLog.log("API23 keep-alive start failed: " + e);
            }
        }
        ensureBundledVoskModel();
    }

    private void ensureBundledVoskModel() {
        File modelDir = new File(getFilesDir(), "vosk-es-small");
        if (new File(modelDir, "am").exists()) {
            AndroidDebugLog.log("Bundled Vosk model already installed");
            return;
        }
        try {
            AndroidDebugLog.log("Installing bundled Vosk model…");
            if (modelDir.exists()) deleteRecursive(modelDir);
            modelDir.mkdirs();
            String root = modelDir.getCanonicalPath() + File.separator;
            try (InputStream raw = getAssets().open("vosk-es.zip");
                 ZipInputStream zin = new ZipInputStream(new BufferedInputStream(raw))) {
                ZipEntry e;
                byte[] buffer = new byte[32768];
                while ((e = zin.getNextEntry()) != null) {
                    String name = e.getName();
                    int slash = name.indexOf('/');
                    if (slash >= 0) name = name.substring(slash + 1);
                    if (name.isEmpty()) continue;
                    File out = new File(modelDir, name);
                    if (!out.getCanonicalPath().startsWith(root)) throw new SecurityException("Bad zip path");
                    if (e.isDirectory()) {
                        out.mkdirs();
                        continue;
                    }
                    File parent = out.getParentFile();
                    if (parent != null) parent.mkdirs();
                    try (FileOutputStream fos = new FileOutputStream(out)) {
                        int n;
                        while ((n = zin.read(buffer)) > 0) fos.write(buffer, 0, n);
                    }
                }
            }
            AndroidDebugLog.log("Bundled Vosk model installed · am=" + new File(modelDir, "am").exists());
        } catch (Exception e) {
            AndroidDebugLog.log("Bundled Vosk install failed: " + e);
        }
    }

    private static void deleteRecursive(File f) {
        if (f.isDirectory()) {
            File[] children = f.listFiles();
            if (children != null) for (File c : children) deleteRecursive(c);
        }
        f.delete();
    }
}
