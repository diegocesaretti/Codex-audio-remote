package com.diegocesaretti.solwake;

import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.Date;
import java.util.Locale;

public final class DebugLog {
    private static final int MAX_LINES = 220;
    private static final ArrayDeque<String> lines = new ArrayDeque<>();

    private DebugLog() {}

    public static synchronized void add(String message) {
        String time = new SimpleDateFormat("HH:mm:ss.SSS", Locale.US).format(new Date());
        lines.addLast(time + "  " + message);
        while (lines.size() > MAX_LINES) lines.removeFirst();
    }

    public static synchronized String snapshot() {
        StringBuilder out = new StringBuilder();
        for (String line : lines) out.append(line).append('\n');
        return out.toString();
    }

    public static synchronized void clear() {
        lines.clear();
    }
}
