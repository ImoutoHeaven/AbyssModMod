#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

public sealed class UiTranslatedValueCache
{
    private Dictionary<string, string>? _table;
    private HashSet<string>? _translatedValues;

    public bool Contains(Dictionary<string, string> table, string value)
    {
        if (!ReferenceEquals(_table, table))
        {
            _table = table;
            _translatedValues = new HashSet<string>(table.Values, StringComparer.Ordinal);
        }

        return _translatedValues!.Contains(value);
    }
}
