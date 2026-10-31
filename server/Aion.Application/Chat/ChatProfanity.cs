namespace Aion.Application.Chat;

/// <summary>
/// Chat message word filter (OURS until ChattingFilter.dat is exported to tools/data).
/// Matches name-check banned patterns used in development tests.
/// </summary>
public static class ChatProfanity
{
    static readonly string[] Blocked =
    [
        "badword",
    ];

    public static bool Rejects(string content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        foreach (var word in Blocked)
            if (content.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
