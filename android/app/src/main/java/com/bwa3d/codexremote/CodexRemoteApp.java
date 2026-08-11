package com.bwa3d.codexremote;

import android.app.Application;
import android.os.Build;

public class CodexRemoteApp extends Application {
    @Override public void onCreate() {
        super.onCreate();
        AndroidDebugLog.install(this);
        AndroidDebugLog.log("Application started · API " + Build.VERSION.SDK_INT);
    }
}
