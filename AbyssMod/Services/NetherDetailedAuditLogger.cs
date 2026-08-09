#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>Only bounded, identifier-oriented F12 audit families may be emitted.</summary>
internal enum NetherDetailedAuditKind
{
    Snapshot,
    Route,
    Interactive,
    Battle,
    F11,
    Lease,
    Checkpoint,
    Native,
    Task,
    Reconcile,
}

internal readonly record struct NetherDetailedAuditField(string Name, string Value);

/// <summary>
/// Structured detailed-audit formatter with explicit opt-in, per-kind bounds and event
/// de-duplication.  It deliberately accepts an injected sink so production uses Logger while
/// characterization tests can assert the exact externally visible audit stream without Unity.
/// </summary>
internal sealed class NetherDetailedAuditLogger
{
    // Diagnostic builds emit a floor summary plus the native-resolved Event row and up to four
    // option rows. Keep a complete 130-floor run observable while bounding a pathological run.
    internal const int MaximumEntriesPerKind = 1024;
    private const int MaximumKeyLength = 64;
    // Unknown native event rows need enough room to preserve their exact target/content tuple.
    // This remains bounded so a malformed runtime object cannot create an unbounded log line.
    private const int MaximumValueLength = 192;
    // Route diagnostics need node identity, graph links, three safety gates, both erosion
    // projections, and the component failure. Keep all twelve while bounding every value.
    private const int MaximumFields = 12;
    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "description",
        "displayName",
        "localizedText",
        "rawPayload",
    };

    private readonly Action<string> _sink;
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly Dictionary<NetherDetailedAuditKind, int> _entriesByKind = new();

    public NetherDetailedAuditLogger(Action<string> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <summary>
    /// Returns true only when a new bounded entry reached the sink.  Disabled logging performs
    /// no formatting/deduping work, which keeps high-frequency polling completely silent.
    /// </summary>
    public bool Emit(
        bool enabled,
        NetherDetailedAuditKind kind,
        string key,
        params NetherDetailedAuditField[] fields
    )
    {
        if (!enabled)
            return false;
        if (_entriesByKind.TryGetValue(kind, out int count) && count >= MaximumEntriesPerKind)
            return false;

        string formatted = Format(kind, key, fields);
        if (!_emitted.Add(formatted))
            return false;

        _entriesByKind[kind] = count + 1;
        _sink(formatted);
        return true;
    }

    private static string Format(
        NetherDetailedAuditKind kind,
        string? key,
        IReadOnlyList<NetherDetailedAuditField>? fields
    )
    {
        string kindText = kind.ToString().ToLowerInvariant();
        var entries = new List<string>(MaximumFields + 2)
        {
            "audit=" + kindText,
            "key=" + Sanitize(key, MaximumKeyLength),
        };
        if (fields != null)
        {
            foreach (NetherDetailedAuditField field in fields.Take(MaximumFields))
            {
                if (string.IsNullOrWhiteSpace(field.Name) || SensitiveFieldNames.Contains(field.Name))
                    continue;
                entries.Add(SanitizeFieldName(field.Name) + "=" + Sanitize(field.Value, MaximumValueLength));
            }
        }
        return string.Join(" ", entries);
    }

    private static string SanitizeFieldName(string value) => Sanitize(value, 32);

    private static string Sanitize(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value))
            return "-";
        string normalized = value
            .Replace(" ", "_")
            .Replace(";", "_")
            .Replace(",", "_")
            .Replace("=", "_")
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
        return normalized.Length <= maximumLength
            ? normalized
            : normalized.Substring(0, maximumLength);
    }
}
