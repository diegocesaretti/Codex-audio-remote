package com.bwa3d.codexremote;

import java.util.Locale;

/**
 * Conservative decision layer in front of Vosk wake recognition.
 *
 * Vosk is an ASR rather than a dedicated keyword spotter. With a constrained grammar it can map
 * unrelated speech to the configured phrase, so a recognised string is never sufficient by
 * itself. The gate requires a believable hypothesis progression, a final result, plausible
 * timing and acoustic evidence on the Android 6 direct-capture path.
 */
final class VoskWakeGate {
    static final class Decision {
        final boolean accepted;
        final boolean loggable;
        final String reason;

        Decision(boolean accepted, boolean loggable, String reason) {
            this.accepted = accepted;
            this.loggable = loggable;
            this.reason = reason;
        }

        static Decision accept(String reason) { return new Decision(true, true, reason); }
        static Decision reject(String reason, boolean loggable) { return new Decision(false, loggable, reason); }
    }

    private static final long CANDIDATE_TIMEOUT_MS = 2400L;
    private static final long AUDIO_WINDOW_MS = 1800L;
    private static final long COOLDOWN_MS = 3500L;
    private static final long MIN_WAKE_DURATION_MS = 300L;
    private static final long MAX_WAKE_DURATION_MS = 1900L;
    private static final long MIN_PREFIX_TO_FINAL_MS = 120L;
    private static final long MAX_PREFIX_TO_FINAL_MS = 1800L;

    // Android 6 devices can expose very small PCM amplitudes. Keep this low; false-positive
    // protection comes mostly from the lexical/progression/final-result gates, not from loudness.
    private static final int MIN_REQUIRED_PEAK_RMS = 70;
    private static final double LOW_SENSITIVITY_NOISE_FACTOR = 1.95;
    private static final double HIGH_SENSITIVITY_NOISE_FACTOR = 1.35;

    private String candidateText = "";
    private int stableHits;
    private int exactPartialHits;
    private long candidateSinceMs;
    private long lastCandidateMs;
    private long lastAcceptedMs;

    private boolean prefixSeen;
    private long prefixSeenMs;

    private double noiseFloor = 90.0;
    private int peakRms;
    private long peakRmsMs;

    synchronized void reset() {
        clearCandidate();
        peakRms = 0;
        peakRmsMs = 0L;
    }

    synchronized void noteFinalSilence() {
        clearCandidate();
    }

    synchronized void observeAudio(int rms, long nowMs) {
        if (rms < 0) return;

        // Asymmetric adaptive floor: follows quieter conditions quickly, rises slowly when the
        // room stays noisy, and clamps each upward step so one speech burst cannot poison it.
        double upwardClamp = Math.max(noiseFloor + 120.0, noiseFloor * 2.5);
        double observed = Math.min(rms, upwardClamp);
        double alpha = observed < noiseFloor ? 0.06 : 0.012;
        noiseFloor = (noiseFloor * (1.0 - alpha)) + (observed * alpha);
        noiseFloor = Math.max(25.0, Math.min(3500.0, noiseFloor));

        if (rms >= peakRms || nowMs - peakRmsMs > AUDIO_WINDOW_MS) {
            peakRms = rms;
            peakRmsMs = nowMs;
        }
    }

    synchronized Decision evaluate(String text, String target, int sensitivity, boolean isFinal,
                                   double confidence, long resultDurationMs, boolean requireAudioEvidence) {
        long now = System.currentTimeMillis();
        text = clean(text);
        target = clean(target);
        if (text.isEmpty() || target.isEmpty()) return Decision.reject("empty", false);
        if (now - lastAcceptedMs < COOLDOWN_MS) return Decision.reject("cooldown", false);
        if (lastCandidateMs > 0 && now - lastCandidateMs > CANDIDATE_TIMEOUT_MS) clearCandidate();

        String[] targetParts = target.split(" ");
        boolean multiWordTarget = targetParts.length >= 2;
        boolean exact = text.equals(target);
        boolean validPrefix = target.startsWith(text + " ");

        if (!exact && !validPrefix) {
            boolean related = sharesWakeToken(text, targetParts);
            clearCandidate();
            return Decision.reject("lexical mismatch: " + text, related);
        }

        if (validPrefix) {
            if (!prefixSeen) {
                prefixSeen = true;
                prefixSeenMs = now;
                candidateSinceMs = now;
            }
            lastCandidateMs = now;
            if (!text.equals(candidateText)) {
                candidateText = text;
                stableHits = 1;
            } else {
                stableHits++;
            }
            return Decision.reject("prefix " + text + " · hits=" + stableHits, false);
        }

        // Exact target. Partial exact hypotheses are evidence only; they never fire the wake.
        if (!text.equals(candidateText)) {
            candidateText = text;
            stableHits = 1;
            if (candidateSinceMs == 0L) candidateSinceMs = now;
        } else {
            stableHits++;
        }
        lastCandidateMs = now;

        if (!isFinal) {
            exactPartialHits++;
            return Decision.reject("exact partial evidence · hits=" + exactPartialHits, false);
        }

        // A multi-word wake must build naturally from a prefix. This is the most important guard
        // against constrained-grammar hallucinations that jump directly from arbitrary speech to
        // the complete wake phrase.
        if (multiWordTarget) {
            if (!prefixSeen) {
                clearCandidate();
                return Decision.reject("final exact without prefix progression", true);
            }
            long prefixAge = now - prefixSeenMs;
            if (prefixAge < MIN_PREFIX_TO_FINAL_MS || prefixAge > MAX_PREFIX_TO_FINAL_MS) {
                clearCandidate();
                return Decision.reject("implausible prefix timing " + prefixAge + "ms", true);
            }
        } else {
            // Single-word wakes are intrinsically more ambiguous. Require several matching
            // partials before the final result can be accepted.
            if (exactPartialHits < 3) {
                clearCandidate();
                return Decision.reject("single-word final lacks partial stability", true);
            }
        }

        long observedDuration = candidateSinceMs > 0 ? now - candidateSinceMs : -1L;
        long duration = resultDurationMs > 0 ? resultDurationMs : observedDuration;
        if (duration > 0 && duration < MIN_WAKE_DURATION_MS) {
            clearCandidate();
            return Decision.reject("too short " + duration + "ms", true);
        }
        if (duration > MAX_WAKE_DURATION_MS) {
            clearCandidate();
            return Decision.reject("too long " + duration + "ms", true);
        }

        if (requireAudioEvidence) {
            long peakAge = peakRmsMs <= 0 ? Long.MAX_VALUE : now - peakRmsMs;
            int clampedSensitivity = Math.max(0, Math.min(100, sensitivity));
            double t = clampedSensitivity / 100.0;
            double factor = LOW_SENSITIVITY_NOISE_FACTOR
                    + ((HIGH_SENSITIVITY_NOISE_FACTOR - LOW_SENSITIVITY_NOISE_FACTOR) * t);
            int requiredPeak = (int)Math.max(MIN_REQUIRED_PEAK_RMS, noiseFloor * factor);
            if (peakAge > AUDIO_WINDOW_MS || peakRms < requiredPeak) {
                clearCandidate();
                return Decision.reject("weak audio rms=" + peakRms + " need=" + requiredPeak
                        + " floor=" + (int)noiseFloor + " factor=" + String.format(Locale.US, "%.2f", factor), true);
            }
        }

        // Confidence is intentionally secondary: constrained Vosk grammars can report a high
        // confidence for the wrong phrase. We use it only after progression/timing/audio pass.
        int clampedSensitivity = Math.max(0, Math.min(100, sensitivity));
        double minConfidence = 0.90 - (clampedSensitivity * 0.0018); // 0.90 -> 0.72
        if (confidence >= 0.0 && confidence < minConfidence) {
            clearCandidate();
            return Decision.reject(String.format(Locale.US, "low confidence %.2f < %.2f", confidence, minConfidence), true);
        }

        return accept(now, "final exact + progression"
                + " · partials=" + exactPartialHits + confidenceSuffix(confidence));
    }

    private Decision accept(long now, String reason) {
        lastAcceptedMs = now;
        clearCandidate();
        return Decision.accept(reason);
    }

    private void clearCandidate() {
        candidateText = "";
        stableHits = 0;
        exactPartialHits = 0;
        candidateSinceMs = 0L;
        lastCandidateMs = 0L;
        prefixSeen = false;
        prefixSeenMs = 0L;
    }

    private static boolean sharesWakeToken(String text, String[] targetParts) {
        String padded = " " + text + " ";
        for (String part : targetParts) {
            if (!part.isEmpty() && padded.contains(" " + part + " ")) return true;
        }
        return false;
    }

    private static String confidenceSuffix(double confidence) {
        return confidence < 0.0 ? "" : String.format(Locale.US, " · conf=%.2f", confidence);
    }

    private static String clean(String value) {
        if (value == null) return "";
        return value.trim().toLowerCase(Locale.ROOT).replaceAll("\\s+", " ");
    }
}
