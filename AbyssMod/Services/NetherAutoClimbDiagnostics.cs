#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal readonly record struct NetherAutoClimbDiagnosticField(string Name, string Value);

/// <summary>
/// Always-on, event-driven F12 diagnostics for failures that happen before detailed state
/// auditing can start. Values are bounded and single-line so a live LogOutput.log can be
/// attached without exposing UI text or creating per-frame log spam.
/// </summary>
internal static class NetherAutoClimbDiagnostics
{
    private const int MaximumFields = 12;
    private const int MaximumEventLength = 64;
    private const int MaximumNameLength = 32;
    private const int MaximumValueLength = 80;

    public static string Format(
        string eventName,
        params NetherAutoClimbDiagnosticField[] fields
    )
    {
        var entries = new List<string>(MaximumFields + 2)
        {
            "[F12][NetherClimb][Diag]",
            "event=" + Sanitize(eventName, MaximumEventLength),
        };
        if (fields != null)
        {
            entries.AddRange(
                fields
                    .Take(MaximumFields)
                    .Where(field => !string.IsNullOrWhiteSpace(field.Name))
                    .Select(field =>
                        Sanitize(field.Name, MaximumNameLength)
                        + "="
                        + Sanitize(field.Value, MaximumValueLength)
                    )
            );
        }
        return string.Join(" ", entries);
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "-";
        string normalized = value
            .Replace("\r", "_")
            .Replace("\n", "_")
            .Replace(" ", "_")
            .Replace(";", "_")
            .Replace(",", "_")
            .Replace("=", "_");
        while (normalized.Contains("__", StringComparison.Ordinal))
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        normalized = normalized.Trim('_');
        if (normalized.Length == 0)
            return "-";
        return normalized.Length <= maximumLength
            ? normalized
            : normalized.Substring(0, maximumLength);
    }
}
