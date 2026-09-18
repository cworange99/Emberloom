using System.Text.RegularExpressions;
using Emberloom.Core.Colonists;

namespace Emberloom.Core.Culture;

/// <summary>
/// The colony's story, on disk. The colony writes it out after every telling, and reads it back
/// when it changes: whatever the file says is the story the colony tells from then on, and the
/// next retelling drifts from that rather than from what a model last wrote.
/// <para>
/// This is the one place the colony's culture can be authored by hand. It is deliberately a plain
/// file rather than a setting: a story is prose, and editing prose in a text editor is the whole
/// point. A file with nothing in it means the colony has no story yet, not that it has an empty one.
/// </para>
/// </summary>
public static class MythFile
{
    public const string Heading = "# What the colony tells itself about its makers";

    /// <summary>The file as the colony writes it: a heading, a note about editing, and the story.</summary>
    public static string Compose(Colony colony)
    {
        var myth = colony.Myth;

        var told = myth is null
            ? "as yet untold — write a story below and the colony will tell it"
            : $"told {myth.Version} time{(myth.Version == 1 ? string.Empty : "s")}, last on day {myth.ToldOnDay:F0}" +
              (myth.ByHand ? ", by hand" : string.Empty);

        return $"""
            {Heading}

            <!-- {told}.
                 Edit the text below this note and save. The colony takes what you write as its
                 story from then on, and its next retelling drifts from yours. -->

            {myth?.Story ?? string.Empty}
            """.TrimEnd() + "\n";
    }

    /// <summary>The story out of the file: everything that is not the heading or a note.</summary>
    public static string ReadStory(string text)
    {
        var body = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

        var lines = body
            .Replace("\r\n", "\n")
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'));

        return string.Join("\n", lines).Trim();
    }
}
