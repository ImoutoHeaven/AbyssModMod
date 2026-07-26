using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AbyssMod.Services;

public static class StaticBundleProtocol
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string ComputeHash(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> bundle
    )
    {
        if (bundle == null)
            return null;

        var entries = new List<(string Key, string Value)>();
        foreach (var table in bundle.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var fields = bundle[table];
            if (fields == null)
                continue;

            foreach (var field in fields.Keys.OrderBy(key => key, StringComparer.Ordinal))
            {
                var values = fields[field];
                if (values == null)
                    continue;

                foreach (var source in values.Keys.OrderBy(key => key, StringComparer.Ordinal))
                    entries.Add(($"{table}\x01{field}\x01{source}", values[source]));
            }
        }

        var text = new StringBuilder();
        foreach (var (key, value) in entries)
        {
            text.Append(key);
            text.Append('\0');
            text.Append(value);
            text.Append('\0');
        }

        return Convert.ToHexString(MD5.HashData(Utf8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    public static Dictionary<string, string> Flatten(
        Dictionary<string, Dictionary<string, string>> fields
    )
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (fields == null)
            return result;

        foreach (var field in fields.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            var values = fields[field];
            if (values == null)
                continue;

            foreach (var (source, translated) in values)
                result[source] = translated;
        }

        return result;
    }

    public static Dictionary<string, string> GetFieldTable(
        Dictionary<string, Dictionary<string, Dictionary<string, string>>> bundle,
        string table,
        string field
    )
    {
        if (bundle != null
            && bundle.TryGetValue(table, out var fields)
            && fields != null
            && fields.TryGetValue(field, out var values))
            return values;

        return bundle != null && bundle.TryGetValue(table, out var fallback)
            ? Flatten(fallback)
            : null;
    }
}
