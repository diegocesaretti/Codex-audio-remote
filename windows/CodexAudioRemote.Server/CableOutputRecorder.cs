// Kept as a no-op compatibility shim so Program.cs does not need to know whether
// audio diagnostics are enabled. Production builds do not write any recordings.
sealed class CableOutputRecorder : IDisposable
{
    public static CableOutputRecorder? TryCreate(string namePart) => new CableOutputRecorder();
    public void Dispose() { }
}
