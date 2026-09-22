using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

internal static class NovelScenarioCandidateCollector
{
    private static readonly HashSet<string> MessageCommands = new(
        [
            "message",
            "l2dmessage",
            "dotmessage",
            "asyncdotmessage",
            "messagetextcenter",
            "messagetextunder",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    public static IReadOnlyList<string> Collect(IEnumerable<IReadOnlyList<string>> rows)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (rows == null)
            return candidates;

        foreach (var row in rows)
        {
            if (row == null || row.Count == 0)
                continue;

            if (MessageCommands.Contains(row[0]))
            {
                if (row.Count > 2)
                    Add(row[2]);
                continue;
            }

            if (!string.Equals(row[0], "select", StringComparison.OrdinalIgnoreCase))
                continue;

            if (row.Count > 2)
                Add(row[2]);
        }

        return candidates;

        void Add(string text)
        {
            if (MachineTranslationTextProtection.HasKana(text) && seen.Add(text))
                candidates.Add(text);
        }
    }
}
