using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

public enum BattleSessionAutoSLStopMode
{
    IsRare = 0,
    Rarity = 1,
    IsRareOrRarity = 2,
    IsRareAndRarity = 3,
}

public enum BattleSessionDropRarity
{
    NoEffect = 0,
    Silver = 1,
    Purple = 2,
    Gold = 3,
    Red = 4,
    UniqueWeapon = 5,
}

[Flags]
public enum BattleSessionNormalContentTypeFilter
{
    /// <summary>Do not filter by content_type; preserve the legacy behavior.</summary>
    Any = 0,
    Weapon = 1 << 0,
    Armor = 1 << 1,
    Accessory = 1 << 2,
}

public sealed class BattleSessionDropEvaluation
{
    public IReadOnlyList<BattleDropItem> Targets { get; }
    public string Error { get; }
    public bool ShouldRetry => Error.Length == 0 && Targets.Count == 0;

    public BattleSessionDropEvaluation(
        IReadOnlyList<BattleDropItem> targets,
        string error = ""
    )
    {
        Targets = targets;
        Error = error;
    }
}

public static class BattleSessionAutoSLRoutingPolicy
{
    public static bool ShouldInterceptExploration(bool isIdleExplorationEncounter) =>
        !isIdleExplorationEncounter;
}

public static class BattleSessionAutoSLPolicy
{
    public const int WeaponContentType = 70;
    public const int ArmorContentType = 80;
    public const int AccessoryContentType = 90;

    private const BattleSessionNormalContentTypeFilter SupportedNormalContentTypes =
        BattleSessionNormalContentTypeFilter.Weapon
        | BattleSessionNormalContentTypeFilter.Armor
        | BattleSessionNormalContentTypeFilter.Accessory;

    public static float ClampCooldown(float seconds) => Math.Max(0f, seconds);

    public static BattleSessionDropEvaluation Evaluate(
        BattleDropProbeReport report,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity,
        BattleSessionNormalContentTypeFilter contentTypes =
            BattleSessionNormalContentTypeFilter.Any
    )
    {
        if (report == null)
            return Error("missing-report");
        if (report.Error.Length != 0)
            return Error(report.Error);
        string validationError = GetStopConditionError(stopMode, minimumRarity);
        if (validationError.Length != 0)
            return Error(validationError);
        validationError = GetNormalContentTypeFilterError(contentTypes);
        if (validationError.Length != 0)
            return Error(validationError);

        var targets = new List<BattleDropItem>();
        foreach (BattleDropItem item in report.Items)
        {
            if (MatchesNormalContentType(item, contentTypes)
                && Matches(item, stopMode, minimumRarity))
                targets.Add(item);
        }

        return new BattleSessionDropEvaluation(targets);
    }

    public static bool Matches(
        BattleDropItem item,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity
    )
    {
        bool matchesRareFlag = item.IsRare;
        bool matchesRarity = item.RarityLevel >= (int)minimumRarity;

        return stopMode switch
        {
            BattleSessionAutoSLStopMode.IsRare => matchesRareFlag,
            BattleSessionAutoSLStopMode.Rarity => matchesRarity,
            BattleSessionAutoSLStopMode.IsRareOrRarity => matchesRareFlag || matchesRarity,
            BattleSessionAutoSLStopMode.IsRareAndRarity => matchesRareFlag && matchesRarity,
            _ => false,
        };
    }

    public static string DescribeStopCondition(
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity
    )
    {
        string rarity = $"rarity>={minimumRarity}({(int)minimumRarity})";
        return stopMode switch
        {
            BattleSessionAutoSLStopMode.IsRare => "isRare",
            BattleSessionAutoSLStopMode.Rarity => rarity,
            BattleSessionAutoSLStopMode.IsRareOrRarity => $"isRare-or-{rarity}",
            BattleSessionAutoSLStopMode.IsRareAndRarity => $"isRare-and-{rarity}",
            _ => $"unknown({(int)stopMode})",
        };
    }

    public static string DescribeNormalStopCondition(
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity,
        BattleSessionNormalContentTypeFilter contentTypes
    ) => DescribeStopCondition(stopMode, minimumRarity)
        + $", contentTypes={DescribeNormalContentTypes(contentTypes)}";

    public static string DescribeNormalContentTypes(
        BattleSessionNormalContentTypeFilter contentTypes
    )
    {
        if (contentTypes == BattleSessionNormalContentTypeFilter.Any)
            return "Any";

        string error = GetNormalContentTypeFilterError(contentTypes);
        if (error.Length != 0)
            return $"unknown({(int)contentTypes})";

        var names = new List<string>();
        if ((contentTypes & BattleSessionNormalContentTypeFilter.Weapon) != 0)
            names.Add($"Weapon({WeaponContentType})");
        if ((contentTypes & BattleSessionNormalContentTypeFilter.Armor) != 0)
            names.Add($"Armor({ArmorContentType})");
        if ((contentTypes & BattleSessionNormalContentTypeFilter.Accessory) != 0)
            names.Add($"Accessory({AccessoryContentType})");
        return string.Join("|", names);
    }

    public static string GetStopConditionError(
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity
    )
    {
        if (!Enum.IsDefined(typeof(BattleSessionAutoSLStopMode), stopMode))
            return $"unsupported-stop-mode:{(int)stopMode}";
        if (!Enum.IsDefined(typeof(BattleSessionDropRarity), minimumRarity))
            return $"unsupported-minimum-rarity:{(int)minimumRarity}";
        return string.Empty;
    }

    public static string GetNormalContentTypeFilterError(
        BattleSessionNormalContentTypeFilter contentTypes
    )
    {
        if ((contentTypes & ~SupportedNormalContentTypes) != 0)
            return $"unsupported-normal-content-types:{(int)contentTypes}";
        return string.Empty;
    }

    public static bool MatchesNormalContentType(
        BattleDropItem item,
        BattleSessionNormalContentTypeFilter contentTypes
    )
    {
        if (contentTypes == BattleSessionNormalContentTypeFilter.Any)
            return true;

        BattleSessionNormalContentTypeFilter itemType = item.ContentType switch
        {
            WeaponContentType => BattleSessionNormalContentTypeFilter.Weapon,
            ArmorContentType => BattleSessionNormalContentTypeFilter.Armor,
            AccessoryContentType => BattleSessionNormalContentTypeFilter.Accessory,
            _ => BattleSessionNormalContentTypeFilter.Any,
        };
        return itemType != BattleSessionNormalContentTypeFilter.Any
            && (contentTypes & itemType) != 0;
    }

    public static bool ShouldRetry(BattleDropProbeReport report) =>
        ShouldRetry(
            report,
            BattleSessionAutoSLStopMode.IsRare,
            BattleSessionDropRarity.Gold
        );

    public static bool ShouldRetry(
        BattleDropProbeReport report,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity
    ) => Evaluate(report, stopMode, minimumRarity).ShouldRetry;

    private static BattleSessionDropEvaluation Error(string error) =>
        new(Array.Empty<BattleDropItem>(), error);
}
