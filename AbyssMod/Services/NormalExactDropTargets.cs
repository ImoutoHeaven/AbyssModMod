using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

public enum NormalExactDropTargetMode
{
    Disabled = 0,
    Enabled = 1,
    Invalid = 2,
}

public readonly record struct NormalExactDropTarget(int ContentType, long ContentId)
{
    public string Token => TryFormatTypeName(ContentType, out string typeName)
        ? $"{typeName}:{ContentId.ToString(CultureInfo.InvariantCulture)}"
        : $"Unknown({ContentType}):{ContentId.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryFormatTypeName(int contentType, out string typeName)
    {
        typeName = contentType switch
        {
            BattleSessionAutoSLPolicy.WeaponContentType => "Weapon",
            BattleSessionAutoSLPolicy.ArmorContentType => "Armor",
            BattleSessionAutoSLPolicy.AccessoryContentType => "Accessory",
            _ => string.Empty,
        };
        return typeName.Length != 0;
    }
}

public sealed class NormalExactDropTargetParseResult
{
    public NormalExactDropTargetMode Mode { get; }
    public IReadOnlyList<NormalExactDropTarget> Targets { get; }
    public string Error { get; }
    public string Description { get; }

    internal NormalExactDropTargetParseResult(
        NormalExactDropTargetMode mode,
        IReadOnlyList<NormalExactDropTarget> targets,
        string error,
        string description
    )
    {
        Mode = mode;
        Targets = targets;
        Error = error;
        Description = description;
    }
}

public static class NormalExactDropTargetParser
{
    public static NormalExactDropTargetParseResult Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new NormalExactDropTargetParseResult(
                NormalExactDropTargetMode.Disabled,
                Array.Empty<NormalExactDropTarget>(),
                string.Empty,
                "none"
            );

        var normalized = new List<NormalExactDropTarget>();
        var seen = new HashSet<NormalExactDropTarget>();
        foreach (string rawEntry in raw.Split(','))
        {
            string entry = rawEntry.Trim();
            int separator = entry.IndexOf(':');
            if (entry.Length == 0)
                return Invalid("<empty>", "empty-entry");
            if (separator <= 0 || separator != entry.LastIndexOf(':'))
                return Invalid(entry, "expected-Type:id");

            string typeText = entry[..separator].Trim();
            string idText = entry[(separator + 1)..].Trim();
            if (!TryParseContentType(typeText, out int contentType))
                return Invalid(entry, $"unsupported-type:{typeText}");
            if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                || id <= 0)
                return Invalid(entry, $"invalid-id:{idText}");

            var target = new NormalExactDropTarget(contentType, id);
            if (seen.Add(target))
                normalized.Add(target);
        }

        return new NormalExactDropTargetParseResult(
            NormalExactDropTargetMode.Enabled,
            normalized,
            string.Empty,
            string.Join(",", normalized.Select(target => target.Token))
        );
    }

    private static bool TryParseContentType(string value, out int contentType)
    {
        if (value.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
        {
            contentType = BattleSessionAutoSLPolicy.WeaponContentType;
            return true;
        }
        if (value.Equals("Armor", StringComparison.OrdinalIgnoreCase))
        {
            contentType = BattleSessionAutoSLPolicy.ArmorContentType;
            return true;
        }
        if (value.Equals("Accessory", StringComparison.OrdinalIgnoreCase))
        {
            contentType = BattleSessionAutoSLPolicy.AccessoryContentType;
            return true;
        }

        contentType = 0;
        return false;
    }

    private static NormalExactDropTargetParseResult Invalid(string entry, string reason)
    {
        string error = $"invalid-normal-exact-target:{entry}:{reason}";
        return new NormalExactDropTargetParseResult(
            NormalExactDropTargetMode.Invalid,
            Array.Empty<NormalExactDropTarget>(),
            error,
            $"invalid:{error}"
        );
    }
}
