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
import android.view.Display;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public class TvAccessibilityService extends AccessibilityService {
    private static volatile TvAccessibilityService instance;
    private static volatile String lastPackage = "";

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
        LocalHttpServer.ensureStarted(getApplicationContext());
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        if (event != null && event.getPackageName() != null) {
            lastPackage = event.getPackageName().toString();
        }
    }

    @Override
    public void onInterrupt() {
    }

    @Override
    public void onDestroy() {
        if (instance == this) instance = null;
        super.onDestroy();
    }

    public static boolean isConnected() {
        return instance != null;
    }

    public static JSONObject snapshot() {
        JSONObject out = new JSONObject();
        try {
            TvAccessibilityService svc = instance;
            out.put("accessibility_connected", svc != null);
            out.put("android_api", Build.VERSION.SDK_INT);
            out.put("package", lastPackage == null ? "" : lastPackage);
            if (svc == null) return out;

            AccessibilityNodeInfo root = svc.getRootInActiveWindow();
            if (root != null && root.getPackageName() != null) {
                out.put("package", root.getPackageName().toString());
            }

            AccessibilityNodeInfo focused = svc.findFocus(AccessibilityNodeInfo.FOCUS_ACCESSIBILITY);
            if (focused == null) focused = svc.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if (focused != null) out.put("focused", nodeSummary(focused));

            int[] count = new int[]{0};
            if (root != null) out.put("tree", nodeJson(root, 0, count));
            out.put("tree_nodes", count[0]);
        } catch (Throwable t) {
            try { out.put("error", t.toString()); } catch (Throwable ignored) {}
        }
        return out;
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
            obj.put("focused", node.isFocused());
            obj.put("accessibility_focused", node.isAccessibilityFocused());
            obj.put("enabled", node.isEnabled());
            Rect r = new Rect();
            node.getBoundsInScreen(r);
            JSONArray bounds = new JSONArray();
            bounds.put(r.left); bounds.put(r.top); bounds.put(r.right); bounds.put(r.bottom);
            obj.put("bounds", bounds);
        } catch (Throwable ignored) {}
        return obj;
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
        if (children.length() > 0) {
            try { obj.put("children", children); } catch (Throwable ignored) {}
        }
        return obj;
    }

    private static void putText(JSONObject obj, String key, CharSequence value) {
        if (value == null) return;
        String s = value.toString();
        if (!s.isEmpty()) {
            try { obj.put(key, s); } catch (Throwable ignored) {}
        }
    }

    public static boolean goBack() {
        return instance != null && instance.performGlobalAction(GLOBAL_ACTION_BACK);
    }

    public static boolean goHome() {
        return instance != null && instance.performGlobalAction(GLOBAL_ACTION_HOME);
    }

    public static boolean clickFocused() {
        TvAccessibilityService svc = instance;
        if (svc == null) return false;
        AccessibilityNodeInfo node = svc.findFocus(AccessibilityNodeInfo.FOCUS_ACCESSIBILITY);
        if (node == null) node = svc.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        return clickNodeOrParent(node);
    }

    public static boolean clickText(String text) {
        TvAccessibilityService svc = instance;
        if (svc == null || text == null || text.trim().isEmpty()) return false;
        AccessibilityNodeInfo root = svc.getRootInActiveWindow();
        if (root == null) return false;
        List<AccessibilityNodeInfo> matches = root.findAccessibilityNodeInfosByText(text);
        if (matches == null) return false;
        for (AccessibilityNodeInfo node : matches) {
            if (clickNodeOrParent(node)) return true;
        }
        return false;
    }

    private static boolean clickNodeOrParent(AccessibilityNodeInfo node) {
        AccessibilityNodeInfo current = node;
        for (int i = 0; current != null && i < 6; i++) {
            if (current.isClickable() && current.isEnabled()) {
                return current.performAction(AccessibilityNodeInfo.ACTION_CLICK);
            }
            current = current.getParent();
        }
        return false;
    }

    public static boolean setText(String text) {
        TvAccessibilityService svc = instance;
        if (svc == null) return false;
        AccessibilityNodeInfo node = svc.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
        if (node == null || !node.isEditable()) {
            node = findEditable(svc.getRootInActiveWindow(), 0);
        }
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
        float px = x * dm.widthPixels;
        float py = y * dm.heightPixels;
        Path path = new Path();
        path.moveTo(px, py);
        GestureDescription.StrokeDescription stroke = new GestureDescription.StrokeDescription(path, 0, 80);
        GestureDescription.Builder builder = new GestureDescription.Builder();
        builder.addStroke(stroke);
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
            case "center":
            case "ok": return svc.performGlobalAction(GLOBAL_ACTION_DPAD_CENTER);
            default: return false;
        }
    }

    public static boolean launchApp(Context context, String query) {
        if (context == null || query == null || query.trim().isEmpty()) return false;
        PackageManager pm = context.getPackageManager();
        String q = query.trim();

        Intent direct = pm.getLeanbackLaunchIntentForPackage(q);
        if (direct == null) direct = pm.getLaunchIntentForPackage(q);
        if (direct != null) {
            direct.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            context.startActivity(direct);
            return true;
        }

        List<ResolveInfo> all = new ArrayList<>();
        Intent leanback = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LEANBACK_LAUNCHER);
        all.addAll(pm.queryIntentActivities(leanback, 0));
        Intent launcher = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        all.addAll(pm.queryIntentActivities(launcher, 0));

        String needle = q.toLowerCase(Locale.US);
        ResolveInfo best = null;
        for (ResolveInfo info : all) {
            String pkg = info.activityInfo.packageName;
            CharSequence labelCs = info.loadLabel(pm);
            String label = labelCs == null ? "" : labelCs.toString();
            String haystack = (label + " " + pkg).toLowerCase(Locale.US);
            if (haystack.equals(needle) || label.equalsIgnoreCase(q) || pkg.equalsIgnoreCase(q)) {
                best = info;
                break;
            }
            if (best == null && haystack.contains(needle)) best = info;
        }
        if (best == null) return false;

        Intent intent = pm.getLeanbackLaunchIntentForPackage(best.activityInfo.packageName);
        if (intent == null) intent = pm.getLaunchIntentForPackage(best.activityInfo.packageName);
        if (intent == null) return false;
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        context.startActivity(intent);
        return true;
    }
}
