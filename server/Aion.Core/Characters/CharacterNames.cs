namespace Aion.Core.Characters;

/// <summary>
/// Staff name tags, shared by character creation and the in-world display. The client has no
/// GM name tag (no GM title, user flag or nameplate asset), so staff characters show a tag in
/// front of their name and players may not create names that look like one.
/// </summary>
public static class CharacterNames
{
    public const string AdminRole = "admin";
    public const string GameMasterRole = "gamemaster";

    /// <summary>
    /// Tag before a staff character's name in the world (not stored). Plain text: the
    /// nameplate shows rich-text style tags literally (live test with &lt;Mythic&gt;, 2026-10-14).
    /// </summary>
    public static string? StaffTag(string? role) => role switch
    {
        AdminRole => "[Admin] ",
        GameMasterRole => "[GM] ",
        _ => null,
    };

    /// <summary>The role shown for an account's roles: admin over gamemaster; null for players.</summary>
    public static string? DisplayRole(IEnumerable<string> roles)
    {
        var held = roles.ToHashSet(StringComparer.Ordinal);
        return held.Contains(AdminRole) ? AdminRole : held.Contains(GameMasterRole) ? GameMasterRole : null;
    }

    /// <summary>
    /// Names reserved for staff accounts: after leading brackets or separators, "GM" in
    /// capitals, "gm" or "admin" in any case followed by a non-letter or the end, or
    /// "GameMaster"/"Administrator". Ordinary names such as "Gmork" or "Adminah" stay allowed.
    /// </summary>
    public static bool IsStaffStyle(string name)
    {
        var rest = name.TrimStart('[', '(', '{', '<', ' ', '_', '-', '.');
        if (rest.StartsWith("GM", StringComparison.Ordinal)) return true;
        foreach (var word in new[] { "gm", "admin" })
            if (rest.StartsWith(word, StringComparison.OrdinalIgnoreCase) && (rest.Length == word.Length || !char.IsLetter(rest[word.Length])))
                return true;
        return rest.StartsWith("gamemaster", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("administrator", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name shown in the world: staff get their role's tag unless the name already reads as staff.</summary>
    public static string Display(string name, string? role) =>
        StaffTag(role) is { } tag && !IsStaffStyle(name) ? tag + name : name;

    /// <summary>
    /// The stored name behind a shown one: rich-text tags and a leading "[GM]"/"[Admin]" tag
    /// removed (the client may send the displayed name back, e.g. with chat messages).
    /// </summary>
    public static string Stored(string shown)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(shown, "<[^<>]*>", "").Trim();
        foreach (var tag in new[] { "[GM]", "[Admin]" })
            if (plain.StartsWith(tag, StringComparison.Ordinal)) return plain[tag.Length..].TrimStart();
        return plain;
    }
}
