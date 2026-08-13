internal sealed record ExternalConversationRequest(string AudioUrl, string Source = "home_assistant");

internal static class ExternalConversationHub
{
    static readonly object Sync = new();
    static Func<ExternalConversationRequest, Task>? handler;

    public static void Register(Func<ExternalConversationRequest, Task> value)
    {
        lock (Sync) handler = value;
    }

    public static void Clear(Func<ExternalConversationRequest, Task> value)
    {
        lock (Sync)
        {
            if (handler == value) handler = null;
        }
    }

    public static async Task<bool> TryStartAsync(ExternalConversationRequest request)
    {
        Func<ExternalConversationRequest, Task>? current;
        lock (Sync) current = handler;
        if (current is null) return false;
        await current(request);
        return true;
    }
}
