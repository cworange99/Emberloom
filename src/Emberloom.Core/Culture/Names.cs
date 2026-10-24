using Emberloom.Core.Colonists;

namespace Emberloom.Core.Culture;

/// <summary>
/// What the colony calls its people. Every mind is named the moment it is printed or born — M9,
/// 23 October 2026.
/// <para>
/// Before this, a name had to be *earned* by one of three deeds: being first to reach a place,
/// breaking into a blocked one, or recovering an artifact. All three are one-shot per place, so
/// they ran out: the 3,787-day run ended with four named people out of thirty-seven, and the rest
/// lived and died as C-22. Worse, the candidates came only from Kindling, and Kindling's fallback
/// returns an empty list — so a mind written while Ollama was unreachable could never be named at
/// all, and said so nowhere. A colony of numbers is a colony nobody can care about.
/// </para>
/// <para>
/// This generator lives in Core, deliberately. Asking the mind for names is the obvious
/// implementation and it is wrong twice over: it would leave a colony with no model nameless, and
/// it would point Core at Mind, which is the dependency direction the whole two-layer split exists
/// to prevent. Kindling's candidates still matter — see <see cref="CultureRules.TakeAName"/> — but
/// as an *upgrade* on a name that already exists, never as the only source of one.
/// </para>
/// </summary>
public static class Names
{
    // Built to sound invented but name-like: Luma, Vael, Oryn, Asha, Zyra. Two syllables, usually
    // four to six letters, and never an English word — the M7 lesson, where the model handed back
    // "spark" and "stone" and the feed read like a typo rather than a person.
    private static readonly string[] Openings =
        ["l", "v", "m", "k", "s", "r", "n", "z", "d", "t", "th", "sh", "br", "dr", "ky", "ly", "ve", "ser", "mar", "ar"];

    private static readonly string[] Middles =
        ["a", "e", "i", "o", "u", "ae", "ia", "ei", "y", "au", "oa", "ue"];

    private static readonly string[] Endings =
        ["n", "l", "r", "s", "th", "m", "ra", "na", "la", "ma", "sha", "ryn", "vel", "den", "tha", "lin", "ven", string.Empty];

    /// <summary>
    /// A name for a mind, fixed by its designation and the colony it was born into. Derived rather
    /// than drawn from the colony's random stream for the same reason lifespans are: a seed has to
    /// keep growing the same colony, and taking a number out of the stream would reshuffle
    /// everything downstream of it.
    /// <para>
    /// The colony's seed is mixed in because without it every colony ever started shared one cast:
    /// C-01 was Arima in all of them. The seed is what makes a new colony a new people.
    /// </para>
    /// </summary>
    public static string For(string designation, int colonySeed) =>
        Compose(StableHash($"{colonySeed}:{designation}"));

    /// <summary>
    /// A name nobody living already goes by. The colony is small and the alphabet is not, but a
    /// long run will still collide eventually; walking the hash forward keeps it deterministic
    /// rather than falling back on the random stream.
    /// </summary>
    public static string Unused(string designation, Colony colony)
    {
        var taken = colony.Colonists
            .Select(c => c.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hash = StableHash($"{colony.Seed}:{designation}");
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var name = Compose(hash + attempt * 7919);
            if (!taken.Contains(name))
            {
                return name;
            }
        }

        // Sixty-four collisions means the colony has outgrown the generator. A name with the
        // designation's digits on it is ugly, but it is still a name and still unique.
        return Compose(hash) + "-" + designation.TrimStart('C', '-');
    }

    /// <summary>
    /// Whether a word is one of these — used to tell a name the colony coined for itself from one
    /// the Loom handed out, and to keep the two kinds of word apart in the lexicon.
    /// </summary>
    public static bool LooksGenerated(string word) =>
        word.Length is >= 3 and <= 10 && word.All(char.IsLetter);

    /// <summary>
    /// A name, and a well-formed one. The three slots can combine into things no one would call a
    /// person — "Veaeryn" from a double vowel meeting a vowel ending, "Sox" from the shortest of
    /// each — so a composition that fails <see cref="IsWellFormed"/> is rejected and the hash walks
    /// on. Deterministic either way: the same designation in the same colony always lands the same.
    /// </summary>
    private static string Compose(int hash)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Build(hash + attempt * 104729);
            if (IsWellFormed(candidate))
            {
                return candidate;
            }
        }

        return Build(hash);
    }

    /// <summary>
    /// Pronounceable, and long enough to be somebody. Three vowels or three consonants in a row is
    /// a sound no one would say twice, and anything under four letters reads as an abbreviation.
    /// </summary>
    private static bool IsWellFormed(string name)
    {
        if (name.Length is < 4 or > 8)
        {
            return false;
        }

        var vowels = 0;
        var consonants = 0;

        foreach (var ch in name)
        {
            if (IsVowel(ch))
            {
                vowels++;
                consonants = 0;
            }
            else
            {
                consonants++;
                vowels = 0;
            }

            if (vowels >= 3 || consonants >= 3)
            {
                return false;
            }
        }

        return true;
    }

    private static string Build(int hash)
    {
        // Mixed before it is split into three. Straight division gave neighbouring designations
        // neighbouring names — C-11 through C-14 came out Vima, Sima, Lyoryn, Rima, which is worse
        // than numbers, because they are not even distinguishable at a glance.
        var opening = Openings[Mix(hash, 1) % Openings.Length];
        var middle = Middles[Mix(hash, 2) % Middles.Length];
        var ending = Endings[Mix(hash, 3) % Endings.Length];

        // Two vowel clusters running together read as a stutter — "Luaa" rather than "Luma". When
        // the ending starts on a vowel and the middle ends on one, drop the ending's consonant-less
        // form and let the name be short: Vae, Zyo, Lia are all fine names.
        if (ending.Length > 0 && IsVowel(ending[0]) && IsVowel(middle[^1]))
        {
            ending = ending[1..];
        }

        var name = opening + middle + ending;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static bool IsVowel(char ch) => "aeiouy".Contains(char.ToLowerInvariant(ch));

    /// <summary>
    /// One avalanche round per syllable slot, so a one-character change in the designation changes
    /// every part of the name rather than only the last one.
    /// </summary>
    private static int Mix(int hash, int slot)
    {
        unchecked
        {
            var h = (uint)hash * 2654435761u + (uint)slot * 2246822519u;
            h ^= h >> 15;
            h *= 2246822519u;
            h ^= h >> 13;
            return (int)(h & 0x7fffffff);
        }
    }

    private static int StableHash(string text)
    {
        var h = 0;
        foreach (var ch in text)
        {
            h = (h * 31 + ch) & 0x7fffffff;
        }

        return h;
    }
}
