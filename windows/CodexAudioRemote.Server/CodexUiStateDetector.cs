using Accessibility;
using System.Diagnostics;
using System.Runtime.InteropServices;

internal enum CodexUiState
{
    Unknown,
    Listening,
    Thinking,
    Speaking
}

internal sealed record CodexUiSnapshot(CodexUiState State, string? MatchedText, int ElementsScanned, bool WindowFound)
{
    public bool Busy => State is CodexUiState.Thinking or CodexUiState.Speaking;
}

internal static class CodexUiStateDetector
{
    const uint OBJID_CLIENT = 0xFFFFFFFC;
    static readonly Guid IID_IAccessible = new("618736e0-3c3d-11cf-810c-00aa00389b71");

    static readonly string[] ThinkingTerms =
    {
        "pensando", "thinking", "procesando", "processing", "trabajando", "working",
        "ejecutando", "running", "generando", "generating"
    };

    static readonly string[] SpeakingTerms =
    {
        "hablando", "speaking", "respondiendo", "responding"
    };

    static readonly string[] ListeningTerms =
    {
        "escuchando", "listening"
    };

    public static CodexUiSnapshot Detect()
    {
        try
        {
            var processes = Process.GetProcesses()
                .Where(p => IsCodexProcess(p.ProcessName) && p.MainWindowHandle != IntPtr.Zero)
                .ToArray();

            if (processes.Length == 0)
                return new(CodexUiState.Unknown, null, 0, false);

            int scanned = 0;
            CodexUiSnapshot? listening = null;
            foreach (var process in processes)
            {
                var acc = FromWindow(process.MainWindowHandle);
                if (acc is null) continue;

                var match = Scan(acc, ref scanned, 0, ref listening);
                if (match is not null) return match;
                if (scanned >= 1400) break;
            }

            return listening ?? new(CodexUiState.Unknown, null, scanned, true);
        }
        catch
        {
            return new(CodexUiState.Unknown, null, 0, false);
        }
    }

    static CodexUiSnapshot? Scan(IAccessible acc, ref int scanned, int depth, ref CodexUiSnapshot? listening)
    {
        if (depth > 24 || scanned >= 1400) return null;
        scanned++;

        var own = Classify(SafeName(acc, 0));
        if (own.State is CodexUiState.Thinking or CodexUiState.Speaking)
            return new(own.State, own.Text, scanned, true);
        if (own.State == CodexUiState.Listening && listening is null)
            listening = new(CodexUiState.Listening, own.Text, scanned, true);

        int count;
        try { count = Math.Min(acc.accChildCount, 400); }
        catch { return null; }

        for (int i = 1; i <= count && scanned < 1400; i++)
        {
            scanned++;
            var name = SafeName(acc, i);
            var match = Classify(name);
            if (match.State is CodexUiState.Thinking or CodexUiState.Speaking)
                return new(match.State, match.Text, scanned, true);
            if (match.State == CodexUiState.Listening && listening is null)
                listening = new(CodexUiState.Listening, match.Text, scanned, true);

            object? child = null;
            try { child = acc.get_accChild(i); } catch { }
            if (child is IAccessible childAcc)
            {
                var nested = Scan(childAcc, ref scanned, depth + 1, ref listening);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    static IAccessible? FromWindow(IntPtr hwnd)
    {
        try
        {
            var iid = IID_IAccessible;
            var hr = AccessibleObjectFromWindow(hwnd, OBJID_CLIENT, ref iid, out var obj);
            return hr >= 0 ? obj as IAccessible : null;
        }
        catch { return null; }
    }

    static string? SafeName(IAccessible acc, object childId)
    {
        try
        {
            var name = acc.get_accName(childId);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch { return null; }
    }

    static bool IsCodexProcess(string name) =>
        name.Contains("codex", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("openai", StringComparison.OrdinalIgnoreCase);

    static (CodexUiState State, string? Text) Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (CodexUiState.Unknown, null);
        var normalized = text.Trim().ToLowerInvariant();
        if (ThinkingTerms.Any(normalized.Contains)) return (CodexUiState.Thinking, text);
        if (SpeakingTerms.Any(normalized.Contains)) return (CodexUiState.Speaking, text);
        if (ListeningTerms.Any(normalized.Contains)) return (CodexUiState.Listening, text);
        return (CodexUiState.Unknown, null);
    }

    [DllImport("oleacc.dll")]
    static extern int AccessibleObjectFromWindow(
        IntPtr hwnd,
        uint dwId,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppvObject);
}
