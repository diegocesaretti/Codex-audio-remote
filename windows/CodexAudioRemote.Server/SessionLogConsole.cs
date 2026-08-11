using System.Runtime.CompilerServices;
using System.Text;

// Zero-touch session logger: it mirrors the existing Console output and automatically
// opens one timestamped log file per connected Android client/session. This keeps the
// proven audio path completely unchanged while giving us enough state history to debug
// abrupt conversation endings.
static class SessionLogBootstrap
{
    [ModuleInitializer]
    public static void Initialize()
    {
        var original = Console.Out;
        Console.SetOut(new SessionLogWriter(original));
        Console.SetError(new SessionLogWriter(original));
    }
}

sealed class SessionLogWriter : TextWriter
{
    readonly TextWriter original;
    readonly object sync = new();
    StreamWriter? session;
    string? sessionPath;

    public SessionLogWriter(TextWriter original) => this.original = original;
    public override Encoding Encoding => original.Encoding;

    public override void WriteLine(string? value)
    {
        var line = value ?? string.Empty;
        lock (sync)
        {
            original.WriteLine(line);

            if (line.Contains("Client connected:", StringComparison.OrdinalIgnoreCase))
                StartSession(line);

            if (session != null)
            {
                session.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}");
                session.Flush();
            }

            if (line.Contains("Client disconnected", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Client error:", StringComparison.OrdinalIgnoreCase))
                EndSession();
        }
    }

    public override void Write(char value)
    {
        lock (sync) original.Write(value);
    }

    void StartSession(string firstLine)
    {
        EndSession();
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            sessionPath = Path.Combine(dir, $"session-{stamp}.log");
            session = new StreamWriter(new FileStream(sessionPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
            session.AutoFlush = true;
            original.WriteLine($"Session log: {sessionPath}");
            session.WriteLine("Codex Audio Remote · session diagnostic log");
            session.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            session.WriteLine($"Executable: {Environment.ProcessPath}");
            session.WriteLine($"OS: {Environment.OSVersion}");
            session.WriteLine(new string('-', 72));
        }
        catch (Exception ex)
        {
            original.WriteLine($"Session log could not start: {ex.Message}");
            session = null;
            sessionPath = null;
        }
    }

    void EndSession()
    {
        if (session == null) return;
        try
        {
            session.WriteLine(new string('-', 72));
            session.WriteLine($"Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            session.Flush();
            session.Dispose();
        }
        catch { }
        finally
        {
            session = null;
            sessionPath = null;
        }
    }
}
