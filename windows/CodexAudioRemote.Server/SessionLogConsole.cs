using System.Runtime.CompilerServices;
using System.Text;

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

    public SessionLogWriter(TextWriter original) => this.original = original;
    public override Encoding Encoding => original.Encoding;

    public override void WriteLine(string? value)
    {
        var line = value ?? string.Empty;
        lock (sync)
        {
            original.WriteLine(line);

            if (line.Contains("Client connected", StringComparison.OrdinalIgnoreCase))
                StartSession();

            if (session is not null)
            {
                session.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {line}");
                session.Flush();
            }

            if (line.Contains("Current client disconnected", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Client error", StringComparison.OrdinalIgnoreCase))
                EndSession();
        }
    }

    public override void Write(char value)
    {
        lock (sync) original.Write(value);
    }

    void StartSession()
    {
        EndSession();
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"session-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            session = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            original.WriteLine("Session log: " + path);
            session.WriteLine("Codex Audio Remote v2 · session diagnostic log");
            session.WriteLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            session.WriteLine($"Executable: {Environment.ProcessPath}");
            session.WriteLine($"OS: {Environment.OSVersion}");
            session.WriteLine(new string('-', 72));
        }
        catch (Exception ex)
        {
            original.WriteLine("Session log could not start: " + ex.Message);
            session = null;
        }
    }

    void EndSession()
    {
        if (session is null) return;
        try
        {
            session.WriteLine(new string('-', 72));
            session.WriteLine($"Ended: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            session.Dispose();
        }
        catch { }
        finally { session = null; }
    }
}
