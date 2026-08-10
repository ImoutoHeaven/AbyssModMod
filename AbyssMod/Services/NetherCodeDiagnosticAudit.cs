#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// A deliberately narrow audit row for an unmapped Nether code.  It contains only master and
/// ability identifiers needed to establish a future semantic mapping; display text, player data,
/// and arbitrary asset contents are excluded.
/// </summary>
internal readonly record struct NetherCodeMasterAudit(
    long codeId,
    int category,
    int effectType,
    long effectParameter1,
    long effectParameter2,
    long effectParameter3,
    int rarity,
    int power,
    string assetId,
    long abilityId,
    string effectLevelType,
    string scopeType,
    string targetType,
    string abilityEffectType
);

internal static class NetherCodeDiagnosticAudit
{
    internal const int MaximumEntries = 8;
    private const int MaximumIdentifierLength = 96;

    public static string? Format(bool detailedLogging, IEnumerable<NetherCodeMasterAudit> audits)
    {
        if (!detailedLogging || audits == null)
            return null;

        string[] entries = audits
            .Take(MaximumEntries)
            .Select(FormatOne)
            .ToArray();
        return entries.Length == 0
            ? null
            : "code-master-audit=" + string.Join(";", entries);
    }

    private static string FormatOne(NetherCodeMasterAudit audit) => string.Concat(
        "id=", audit.codeId.ToString(CultureInfo.InvariantCulture),
        ",category=", audit.category.ToString(CultureInfo.InvariantCulture),
        ",effectType=", audit.effectType.ToString(CultureInfo.InvariantCulture),
        ",p1=", audit.effectParameter1.ToString(CultureInfo.InvariantCulture),
        ",p2=", audit.effectParameter2.ToString(CultureInfo.InvariantCulture),
        ",p3=", audit.effectParameter3.ToString(CultureInfo.InvariantCulture),
        ",rarity=", audit.rarity.ToString(CultureInfo.InvariantCulture),
        ",power=", audit.power.ToString(CultureInfo.InvariantCulture),
        ",asset=", Sanitize(audit.assetId),
        ",abilityId=", audit.abilityId.ToString(CultureInfo.InvariantCulture),
        ",levelType=", Sanitize(audit.effectLevelType),
        ",scope=", Sanitize(audit.scopeType),
        ",target=", Sanitize(audit.targetType),
        ",abilityEffect=", Sanitize(audit.abilityEffectType)
    );

    private static string Sanitize(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return "-";
        string normalized = identifier
            .Replace(";", "_")
            .Replace(",", "_")
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
        return normalized.Length <= MaximumIdentifierLength
            ? normalized
            : normalized.Substring(0, MaximumIdentifierLength);
    }
}
