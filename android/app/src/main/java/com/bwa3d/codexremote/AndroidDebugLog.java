package com.bwa3d.codexremote;

import android.content.Context;

import java.io.File;
import java.io.FileWriter;
import java.io.PrintWriter;
import java.text.SimpleDateFormat;
import java.util.Date;
import java.util.Locale;

public final class AndroidDebugLog {
    private static final Object LOCK = new Object();
    private static File logFile;
    private static boolean installed;

    private AndroidDebugLog() { }

    public static void install(Context context) {
        synchronized (LOCK) {
            if (logFile == null) {
                File base = context.getExternalFilesDir(null);
                if (base == null) base = context.getFilesDir();
                File dir = new File(base, "logs");
                if (!dir.exists()) dir.mkdirs();
                logFile = new File(dir, "android-debug.log");
            }
            if (installed) return;
            installed = true;
            final Thread.UncaughtExceptionHandler previous = Thread.getDefaultUncaughtExceptionHandler();
            Thread.setDefaultUncaughtExceptionHandler((thread, throwable) -> {
                log("UNCAUGHT on " + thread.getName() + ": " + throwable);
                writeThrowable(throwable);
                if (previous != null) previous.uncaughtException(thread, throwable);
            });
            log("Logger installed");
        }
    }

    public static void log(String message) {
        synchronized (LOCK) {
            if (logFile == null) return;
            try (FileWriter fw = new FileWriter(logFile, true); PrintWriter out = new PrintWriter(fw)) {
                String ts = new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US).format(new Date());
                out.println(ts + " | " + message);
            } catch (Exception ignored) { }
        }
    }

    public static void writeThrowable(Throwable t) {
        synchronized (LOCK) {
            if (logFile == null || t == null) return;
            try (FileWriter fw = new FileWriter(logFile, true); PrintWriter out = new PrintWriter(fw)) {
                t.printStackTrace(out);
            } catch (Exception ignored) { }
        }
    }
}
