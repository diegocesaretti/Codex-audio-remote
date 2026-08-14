from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if new in text:
        return text
    if old not in text:
        raise RuntimeError(f"Patch anchor not found: {label}")
    return text.replace(old, new, 1)


p = Path('android/app/src/main/java/com/bwa3d/codexremote/RemoteService.java')
s = p.read_text(encoding='utf-8')

old_init = '''    private void initVosk() {
        if (voskModel != null || modelLoading) return;
        File modelDir = new File(getFilesDir(), "vosk-es-small");
        if (new File(modelDir, "am").exists()) { loadModel(modelDir); return; }
        modelLoading = true;
        new Thread(() -> {
            File zip = new File(getCacheDir(), "vosk-es.zip");
            try {
                updateNotification("Descargando wake model (~39 MB)…");
                downloadFile(MODEL_URL, zip);
                if (modelDir.exists()) deleteRecursive(modelDir);
                modelDir.mkdirs(); unzipStripRoot(zip, modelDir); loadModel(modelDir);
            } catch (Exception e) { AndroidDebugLog.log("Vosk model download/load error: " + e); updateNotification("Wake manual · error descargando modelo"); }
            finally { modelLoading = false; if (zip.exists()) zip.delete(); }
        }, "VoskModelSetup").start();
    }

    private void loadModel(File dir) {
        try {
            voskModel = new Model(dir.getAbsolutePath());
            AndroidDebugLog.log("Vosk model loaded");
            updateNotification(connected ? "Conectado · " + wakeWord() : "Wake listo · esperando PC");
            handler.post(this::startWakeRecognition);
        } catch (Exception e) { AndroidDebugLog.log("Vosk model invalid: " + e); updateNotification("Wake model inválido"); }
    }'''

new_init = '''    private void initVosk() {
        if (voskModel != null || modelLoading) return;
        File modelDir = new File(getFilesDir(), "vosk-es-small");
        if (new File(modelDir, "am").exists() && loadModel(modelDir)) return;

        // A half-downloaded/corrupt model used to leave wake permanently dead because merely
        // having an "am" directory skipped provisioning. Rebuild it from the APK instead.
        if (modelDir.exists()) deleteRecursive(modelDir);
        modelLoading = true;
        new Thread(() -> {
            File zip = new File(getCacheDir(), "vosk-es.zip");
            try {
                boolean bundled = false;
                try (java.io.InputStream in = getAssets().open("vosk-es.zip");
                     FileOutputStream fos = new FileOutputStream(zip)) {
                    updateNotification("Preparando wake offline…");
                    byte[] b = new byte[32768];
                    int n;
                    long copied = 0;
                    while ((n = in.read(b)) > 0) { fos.write(b, 0, n); copied += n; }
                    bundled = copied > 1024 * 1024;
                    AndroidDebugLog.log("Bundled Vosk model copied from APK · bytes=" + copied);
                } catch (Exception assetError) {
                    AndroidDebugLog.log("Bundled Vosk model unavailable: " + assetError);
                }

                if (!bundled) {
                    updateNotification("Descargando wake model (~39 MB)…");
                    AndroidDebugLog.log("Falling back to Vosk model download");
                    downloadFile(MODEL_URL, zip);
                }

                if (modelDir.exists()) deleteRecursive(modelDir);
                modelDir.mkdirs();
                unzipStripRoot(zip, modelDir);
                if (!loadModel(modelDir)) throw new IllegalStateException("Bundled/downloaded Vosk model could not be loaded");
            } catch (Exception e) {
                AndroidDebugLog.log("Vosk model provision/load error: " + e);
                updateNotification("Wake no disponible · revisar log");
            } finally {
                modelLoading = false;
                if (zip.exists()) zip.delete();
            }
        }, "VoskModelSetup").start();
    }

    private boolean loadModel(File dir) {
        try {
            if (voskModel != null) { try { voskModel.close(); } catch (Exception ignored) { } voskModel = null; }
            voskModel = new Model(dir.getAbsolutePath());
            AndroidDebugLog.log("Vosk model loaded · path=" + dir.getAbsolutePath() + " · wake=" + wakeWord());
            updateNotification(connected ? "Conectado · wake " + wakeWord() : "Wake listo · esperando PC");
            handler.post(this::startWakeRecognition);
            return true;
        } catch (Exception e) {
            voskModel = null;
            AndroidDebugLog.log("Vosk model invalid: " + e);
            updateNotification("Wake model inválido · reconstruyendo");
            return false;
        }
    }'''

s = replace_once(s, old_init, new_init, 'offline bundled Vosk provisioning')

# Improve the logs enough that a future wake failure tells us whether recognition actually armed.
s = replace_once(
    s,
    '''            Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());
            speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE); speechService.startListening(this);''',
    '''            Recognizer recognizer = new Recognizer(voskModel, WAKE_SAMPLE_RATE, wakeGrammar());
            speechService = new SpeechService(recognizer, WAKE_SAMPLE_RATE);
            speechService.startListening(this);
            AndroidDebugLog.log("Wake SpeechService ARMED · word=" + wakeWord() + " · grammar=" + wakeGrammar());''',
    'wake armed diagnostic')

p.write_text(s, encoding='utf-8')
print('Bundled offline Vosk wake model patch applied')
