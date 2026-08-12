using System;
using System.Collections.Generic;
using System.Text.Json;

namespace AbyssMod.Services;

/// <summary>Pure schema/value rules shared by the runtime MasterData mapping.</summary>
public static class MasterDataSyncProtocol
{
    public static IEnumerable<string> ReadClassNames(
        string defaultClassName,
        JsonElement tableElement
    )
    {
        yield return defaultClassName;

        if (
            tableElement.ValueKind != JsonValueKind.Object
            || !tableElement.TryGetProperty("_class_aliases", out var aliases)
            || aliases.ValueKind != JsonValueKind.Array
        )
            yield break;

        var seen = new HashSet<string>(StringComparer.Ordinal) { defaultClassName };
        foreach (var aliasElement in aliases.EnumerateArray())
        {
            if (aliasElement.ValueKind != JsonValueKind.String)
                continue;

            string alias = aliasElement.GetString();
            if (!string.IsNullOrEmpty(alias) && seen.Add(alias))
                yield return alias;
        }
    }

    public static string ResolveRepositoryValue(string translated, bool legacySealRule)
    {
        _ = legacySealRule;
        return translated;
    }
}
