using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AbyssMod.Services;

/// <summary>Shared ui_texts recursive-table counting and manifest hashing rules.</summary>
public static class ContextualUiTextProtocol
{
    public static int CountEntries(Dictionary<string, Dictionary<string, string>> table) =>
        table?.Values.Where(entries => entries != null).Sum(entries => entries.Count) ?? 0;

    public static string ComputeHash(Dictionary<string, Dictionary<string, string>> table)
    {
        using var md5 = MD5.Create();

        foreach (var (path, entries) in table.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (entries == null)
                continue;

            foreach (var (sourceText, translatedText) in entries.OrderBy(
                pair => pair.Key,
                StringComparer.Ordinal
            ))
            {
                Append(md5, $"{path}\u0001{sourceText}");
                Append(md5, "\0");
                Append(md5, translatedText ?? string.Empty);
                Append(md5, "\0");
            }
        }

        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(md5.Hash).ToLowerInvariant();
    }

    private static void Append(HashAlgorithm hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
}
