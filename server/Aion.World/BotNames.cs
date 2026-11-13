namespace Aion.World;

/// <summary>
/// Random game-style character names for the bots (USER 2026-11-08): two or three fantasy syllables, sometimes with a short ending,
/// 4-14 letters, letters only (the character name rules). A taken name is simply retried by the caller.
/// </summary>
public static class BotNames
{
    static readonly string[] Starts =
    [
        "Ael", "Ar", "Bel", "Cae", "Dar", "El", "Fae", "Gal", "Hel", "Il", "Ka", "Lor", "Mae", "Nyx", "Or", "Pyr", "Quel", "Rha", "Sae", "Tal",
        "Ul", "Val", "Wyn", "Xan", "Yr", "Zel", "Aur", "Bran", "Cyr", "Dra", "Eri", "Fen", "Gry", "Hal", "Ira", "Jor", "Kae", "Lys", "Mor", "Nae",
        "Ori", "Pha", "Ryn", "Syl", "Thar", "Vex", "Kor", "Sol", "Ven", "Zar",
    ];
    static readonly string[] Middles =
    [
        "a", "e", "i", "o", "u", "ae", "ia", "ri", "la", "na", "ra", "the", "va", "ze", "ly", "ro", "da", "mi", "so", "ka",
    ];
    static readonly string[] Ends =
    [
        "n", "th", "ra", "ria", "riel", "dor", "mir", "wen", "las", "ren", "ion", "is", "ar", "en", "os", "yn", "iel", "on", "ius", "ash",
        "ix", "or", "ael", "ith", "ys", "and", "ell", "ane", "ok", "us",
    ];
    static readonly string[] Tails = ["", "", "", "", "a", "e", "o", "x", "s"];

    /// <summary>A new random name (capitalised, 4-14 letters).</summary>
    public static string Next(Random rng)
    {
        while (true)
        {
            var name = Starts[rng.Next(Starts.Length)] + (rng.Next(3) == 0 ? Middles[rng.Next(Middles.Length)] : "")
                + Ends[rng.Next(Ends.Length)] + Tails[rng.Next(Tails.Length)];
            if (name.Length is >= 4 and <= 14) return char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        }
    }
}
