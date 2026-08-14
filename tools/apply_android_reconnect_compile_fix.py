from pathlib import Path

p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')
old = '''    private final Runnable reconnectRunnable = () -> {
        if (destroyed || connected) return;
        AndroidDebugLog.log("WS reconnect watchdog firing");
        connect();
        if (!connected) handler.postDelayed(reconnectRunnable, 4000);
    };'''
new = '''    private final Runnable reconnectRunnable = new Runnable() {
        @Override public void run() {
            if (destroyed || connected) return;
            AndroidDebugLog.log("WS reconnect watchdog firing");
            connect();
            if (!connected) handler.postDelayed(this, 4000);
        }
    };'''
if new in s:
    print('Android reconnect watchdog compile fix already applied')
elif old not in s:
    raise RuntimeError('Reconnect watchdog compile-fix anchor not found')
else:
    p.write_text(s.replace(old, new, 1), encoding='utf-8')
    print('Android reconnect watchdog compile fix applied')
