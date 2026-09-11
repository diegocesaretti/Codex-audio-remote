package com.bwa3d.codextv;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Path;
import android.graphics.Rect;
import android.os.Build;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.Display;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.concurrent.atomic.AtomicLong;

public class TvAccessibilityService extends AccessibilityService {
    private static volatile TvAccessibilityService instance;
    private static volatile String lastPackage = "";
    private static final AtomicLong EVENT_SEQUENCE = new AtomicLong(0L);
    private static final Object EVENT_LOCK = new Object();
    private static volatile long lastEventAtElapsed = 0L;
    private static volatile int lastEventType = 0;

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
        markUiEvent(AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED);
        LocalHttpServer.ensureStarted(getApplicationContext());
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        if (event != null) {
            if (event.getPackageName() != null) lastPackage = event.getPackageName().toString();
            markUiEvent(event.getEventType());
        }
    }

    private static void markUiEvent(int eventType) {
        lastEventType = eventType;
        lastEventAtElapsed = SystemClock.elapsedRealtime();
        EVENT_SEQUENCE.incrementAndGet();
        synchronized (EVENT_LOCK) { EVENT_LOCK.notifyAll(); }
    }

    public static long eventSequence() { return EVENT_SEQUENCE.get(); }
    public static long lastEventAgeMs() {
        long at = lastEventAtElapsed;
        return at == 0L ? -1L : Math.max(0L, SystemClock.elapsedRealtime() - at);
    }

    public static JSONObject waitForUiSettled(long sinceSequence, long quietMs, long timeoutMs) {
        long quiet = Math.max(60L, Math.min(750L, quietMs));
        long timeout = Math.max(100L, Math.min(5000L, timeoutMs));
        long deadline = SystemClock.elapsedRealtime() + timeout;
        long observed = EVENT_SEQUENCE.get();
        boolean changed = observed > sinceSequence;
        while (SystemClock.elapsedRealtime() < deadline) {
            long age = lastEventAgeMs();
            if ((changed || sinceSequence < 0L) && age >= quiet) break;
            long remain = deadline - SystemClock.elapsedRealtime();
            if (remain <= 0L) break;
            synchronized (EVENT_LOCK) {
                try { EVENT_LOCK.wait(Math.min(remain, Math.max(quiet, 80L))); }
                catch (InterruptedException ignored) { Thread.currentThread().interrupt(); break; }
            }
            long next = EVENT_SEQUENCE.get();
            if (next > observed || next > sinceSequence) changed = true;
            observed = next;
        }
        JSONObject result = new JSONObject();
        try {
            result.put("sequence", EVENT_SEQUENCE.get());
            result.put("changed", changed);
            result.put("quiet_ms", quiet);
            result.put("last_event_age_ms", lastEventAgeMs());
            result.put("last_event_type", lastEventType);
        } catch (Throwable ignored) {}
        return result;
    }

    @Override public void onInterrupt() {}

    @Override
    public void onDestroy() {
        if (instance == this) instance = null;
        markUiEvent(AccessibilityEvent.TYPE_WINDOWS_CHANGED);
        super.onDestroy();
    }

    public static boolean isConnected() { return instance != null; }

    public static JSONObject snapshot() {
        JSONObject out = new JSONObject();
        try {
            TvAccessibilityService svc = instance;
            out.put("accessibility_connected", svc != null);
            out.put("android_api", Build.VERSION.SDK_INT);
            out.put("package", lastPackage == null ? "" : lastPackage);
            out.put("ui_event_sequence", EVENT_SEQUENCE.get());
            out.put("last_ui_event_age_ms", lastEventAgeMs());
            if (svc == null) return out;

            AccessibilityNodeInfo root = svc.getRootInActiveWindow();
            if (root != null && root.getPackageName() != null) out.put("package", root.getPackageName().toString());

            AccessibilityNodeInfo focused = bestFocusedNode(svc, root);
            if (focused != null) {
                JSONObject focus = nodeSummary(focused);
                addNormalizedBounds(svc, focus, focused);
                focus.put("source", "accessibility");
                focus.put("confidence", 0.96);
                out.put("focused", focus);
                out.put("focus_hint", focus);
            }

            int[] count = new int[]{0};
            if (root != null) out.put("tree", nodeJson(root, 0, count));
            out.put("tree_nodes", count[0]);
        } catch (Throwable t) {
            try { out.put("error", t.toString()); } catch (Throwable ignored) {}
        }
        return out;
    }

    private static AccessibilityNodeInfo bestFocusedNode(TvAccessibilityService svc, AccessibilityNodeInfo root) {
        AccessibilityNodeInfo node = svc.findFocus(AccessibilityNodeInfo.FOCUS_ACCESSIBILITY);
        if (node == null) node = svc.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        if (node != null) return node;
        return findSelectedOrFocused(root, 0);
    }

    private static AccessibilityNodeInfo findSelectedOrFocused(AccessibilityNodeInfo node, int depth) {
        if (node == null || depth > 10) return null;
        if ((node.isFocused() || node.isAccessibilityFocused() || node.isSelected()) && node.isVisibleToUser()) return node;
        for (int i = 0; i < node.getChildCount(); i++) {
            AccessibilityNodeInfo found = findSelectedOrFocused(node.getChild(i), depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    public static Rect focusedBounds() {
        TvAccessibilityService svc = instance;
        if (svc == null) return null;
        AccessibilityNodeInfo root = svc.getRootInActiveWindow();
        AccessibilityNodeInfo node = bestFocusedNode(svc, root);
        if (node == null) return null;
        Rect r = new Rect();
        node.getBoundsInScreen(r);
        return r.isEmpty() ? null : r;
    }

    private static JSONObject nodeSummary(AccessibilityNodeInfo node) {
        JSONObject obj = new JSONObject();
        try {
            putText(obj, "text", node.getText());
            putText(obj, "description", node.getContentDescription());
            putText(obj, "class", node.getClassName());
            putText(obj, "view_id", node.getViewIdResourceName());
            obj.put("clickable", node.isClickable());
            obj.put("editable", node.isEditable());
            obj.put("focusable", node.isFocusable());
            obj.put("focused", node.isFocused());
            obj.put("accessibility_focused", node.isAccessibilityFocused());
            obj.put("selected", node.isSelected());
            obj.put("visible", node.isVisibleToUser());
            obj.put("enabled", node.isEnabled());
            Rect r = new Rect();
            node.getBoundsInScreen(r);
            JSONArray bounds = new JSONArray();
            bounds.put(r.left); bounds.put(r.top); bounds.put(r.right); bounds.put(r.bottom);
            obj.put("bounds", bounds);
        } catch (Throwable ignored) {}
        return obj;
    }

    private static void addNormalizedBounds(TvAccessibilityService svc, JSONObject obj, AccessibilityNodeInfo node) {
        try {
            WindowManager wm = (WindowManager) svc.getSystemService(WINDOW_SERVICE);
            if (wm == null) return;
            android.util.DisplayMetrics dm = new android.util.DisplayMetrics();
            wm.getDefaultDisplay().getRealMetrics(dm);
            Rect r = new Rect(); node.getBoundsInScreen(r);
            JSONArray n = new JSONArray();
            n.put((double) r.left / Math.max(1, dm.widthPixels));
            n.put((double) r.top / Math.max(1, dm.heightPixels));
            n.put((double) r.right / Math.max(1, dm.widthPixels));
            n.put((double) r.bottom / Math.max(1, dm.heightPixels));
            obj.put("bounds_normalized", n);
            obj.put("center_normalized", new JSONArray().put((r.left + r.right) / (2.0 * Math.max(1, dm.widthPixels))).put((r.top + r.bottom) / (2.0 * Math.max(1, dm.heightPixels))));
        } catch (Throwable ignored) {}
    }

    private static JSONObject nodeJson(AccessibilityNodeInfo node, int depth, int[] count) {
        JSONObject obj = nodeSummary(node);
        count[0]++;
        if (depth >= 8 || count[0] >= 300) return obj;
        JSONArray children = new JSONArray();
        for (int i = 0; i < node.getChildCount() && count[0] < 300; i++) {
            AccessibilityNodeInfo child = node.getChild(i);
            if (child != null) children.put(nodeJson(child, depth + 1, count));
        }
        if (children.length() > 0) try { obj.put("children", children); } catch (Throwable ignored) {}
        return obj;
    }

    private static void putText(JSONObject obj, String key, CharSequence value) {
        if (value == null) return;
        String s = value.toString();
        if (!s.isEmpty()) try { obj.put(key, s); } catch (Throwable ignored) {}
    }

    public static boolean goBack() { return instance != null && instance.performGlobalAction(GLOBAL_ACTION_BACK); }
    public static boolean goHome() { return instance != null && instance.performGlobalAction(GLOBAL_ACTION_HOME); }

    public static boolean clickFocused() {
        TvAccessibilityService svc = instance;
        if (svc == null) return false;
        return clickNodeOrParent(bestFocusedNode(svc, svc.getRootInActiveWindow()));
    }

    public static boolean clickText(String text) {
        TvAccessibilityService svc = instance;
        if (svc == null || text == null || text.trim().isEmpty()) return false;
        AccessibilityNodeInfo root = svc.getRootInActiveWindow();
        if (root == null) return false;
        List<AccessibilityNodeInfo> matches = root.findAccessibilityNodeInfosByText(text);
        if (matches == null) return false;
        for (AccessibilityNodeInfo node : matches) if (clickNodeOrParent(node)) return true;
        return false;
    }

    private static boolean clickNodeOrParent(AccessibilityNodeInfo node) {
        AccessibilityNodeInfo current = node;
        for (int i = 0; current != null && i < 6; i++) {
            if (current.isClickable() && current.isEnabled()) return current.performAction(AccessibilityNodeInfo.ACTION_CLICK);
            current = current.getParent();
        }
        return false;
    }

    public static boolean setText(String text) {
        TvAccessibilityService svc = instance;
        if (svc == null) return false;
        AccessibilityNodeInfo node = svc.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        if (node == null || !node.isEditable()) node = findEditable(svc.getRootInActiveWindow(), 0);
        if (node == null) return false;
        Bundle args = new Bundle();
        args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, text == null ? "" : text);
        return node.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args);
    }

    private static AccessibilityNodeInfo findEditable(AccessibilityNodeInfo node, int depth) {
        if (node == null || depth > 10) return null;
        if (node.isEditable()) return node;
        for (int i = 0; i < node.getChildCount(); i++) {
            AccessibilityNodeInfo result = findEditable(node.getChild(i), depth + 1);
            if (result != null) return result;
        }
        return null;
    }

    public static boolean tapNormalized(float x, float y) {
        TvAccessibilityService svc = instance;
        if (svc == null || x < 0f || x > 1f || y < 0f || y > 1f) return false;
        WindowManager wm = (WindowManager) svc.getSystemService(WINDOW_SERVICE);
        if (wm == null) return false;
        Display display = wm.getDefaultDisplay();
        android.util.DisplayMetrics dm = new android.util.DisplayMetrics();
        display.getRealMetrics(dm);
        Path path = new Path(); path.moveTo(x * dm.widthPixels, y * dm.heightPixels);
        GestureDescription.Builder builder = new GestureDescription.Builder();
        builder.addStroke(new GestureDescription.StrokeDescription(path, 0, 80));
        return svc.dispatchGesture(builder.build(), null, null);
    }

    public static boolean dpad(String direction) {
        TvAccessibilityService svc = instance;
        if (svc == null || Build.VERSION.SDK_INT < 33) return false;
        String d = direction == null ? "" : direction.toLowerCase(Locale.US);
        switch (d) {
            case "up": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_UP);
            case "down": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_DOWN);
            case "left": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_LEFT);
            case "right": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_RIGHT);
            case "center": case "ok": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_CENTER);
            default: return false;
        }
    }

    public static boolean launchApp(Context context, String query) {
        if (context == null || query == null || query.trim().isEmpty()) return false;
        PackageManager pm = context.getPackageManager();
        String q = query.trim();
        Intent direct = pm.getLeanbackLaunchIntentForPackage(q);
        if (direct == null) direct = pm.getLaunchIntentForPackage(q);
        if (direct != null) { direct.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK); context.startActivity(direct); return true; }

        List<ResolveInfo> all = new ArrayList<>();
        all.addAll(pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LEANBACK_LAUNCHER), 0));
        all.addAll(pm.queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0));
        String needle = q.toLowerCase(Locale.US);
        ResolveInfo best = null;
        for (ResolveInfo info : all) {
            String pkg = info.activityInfo.packageName;
            CharSequence labelCs = info.loadLabel(pm);
            String label = labelCs == null ? "" : labelCs.toString();
            String haystack = (label + " " + pkg).toLowerCase(Locale.US);
            if (haystack.equals(needle) || label.equalsIgnoreCase(q) || pkg.equalsIgnoreCase(q)) { best = info; break; }
            if (best == null && haystack.contains(needle)) best = info;
        }
        if (best == null) return false;
        Intent intent = pm.getLeanbackLaunchIntentForPackage(best.activityInfo.packageName);
        if (intent == null) intent = pm.getLaunchIntentForPackage(best.activityInfo.packageName);
        if (intent == null) return false;
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK); context.startActivity(intent); return true;
    }
}
