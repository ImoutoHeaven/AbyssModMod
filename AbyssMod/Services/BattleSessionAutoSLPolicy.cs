using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

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
    public IReadOnlyList<string> MatchedTargetDetails { get; }
    public string Error { get; }
    public bool ShouldRetry => Error.Length == 0 && Targets.Count == 0;

    public BattleSessionDropEvaluation(
        IReadOnlyList<BattleDropItem> targets,
        string error = ""
    )
    {
        Targets = targets;
        MatchedTargetDetails = Array.Empty<string>();
        Error = error;
    }

    public BattleSessionDropEvaluation(
        IReadOnlyList<BattleDropItem> targets,
        IReadOnlyList<string> matchedTargetDetails,
        string error = ""
    )
    {
        Targets = targets;
        MatchedTargetDetails = matchedTargetDetails;
        Error = error;
    }
}

public static class BattleSessionAutoSLRoutingPolicy
{
    public static bool ShouldInterceptExploration(bool isIdleExplorationEncounter)
    {
        // Idle exploration has a distinct close -> start retry transport, but it is
        // still eligible for the same response-side target evaluation.
        _ = isIdleExplorationEncounter;
        return true;
    }
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

    public static BattleSessionDropEvaluation EvaluateNormal(
        BattleDropProbeReport report,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity,
        BattleSessionNormalContentTypeFilter contentTypes,
        string normalExactTargets,
        NormalEquipmentMasterIndex? normalEquipmentMaster = null
    )
    {
        NormalExactDropTargetParseResult exact = NormalExactDropTargetParser.Parse(
            normalExactTargets
        );
        if (exact.Mode == NormalExactDropTargetMode.Invalid)
            return Error(exact.Error);
        if (exact.Mode == NormalExactDropTargetMode.Disabled)
            return Evaluate(report, stopMode, minimumRarity, contentTypes);
        if (report == null)
            return Error("missing-report");
        if (report.Error.Length != 0)
            return Error(report.Error);

        NormalExactDropTarget[] exactTargets = exact.Targets
            .Where(target => target.MatchMode == NormalDropTargetMatchMode.Exact)
            .ToArray();
        NormalExactDropTarget[] familyTargets = exact.Targets
            .Where(target => target.MatchMode == NormalDropTargetMatchMode.FamilyAtOrAbove)
            .ToArray();
        if (familyTargets.Length != 0 && normalEquipmentMaster == null)
            return Error("normal-family-master-unavailable");

        var configuredExact = new HashSet<NormalExactDropTarget>(exactTargets);
        var familyAnchors = new List<(NormalExactDropTarget Target, NormalEquipmentMasterInfo Info)>();
        foreach (NormalExactDropTarget target in familyTargets)
        {
            if (!normalEquipmentMaster!.TryGet(
                    target.ContentType,
                    target.ContentId,
                    out NormalEquipmentMasterInfo anchor
                ))
                return Error($"missing-normal-family-anchor:{target.Token}");
            familyAnchors.Add((target, anchor));
        }

        var targets = new List<BattleDropItem>();
        var matchedTargetDetails = new List<string>();
        foreach (BattleDropItem item in report.Items)
        {
            var actualTarget = new NormalExactDropTarget(item.ContentType, item.ContentId);
            if (configuredExact.Contains(actualTarget))
            {
                targets.Add(item);
                matchedTargetDetails.Add($"{actualTarget.Token}=>{actualTarget.Token}");
                continue;
            }

            var applicableFamilies = familyAnchors
                .Where(pair => pair.Target.ContentType == item.ContentType)
                .ToArray();
            if (applicableFamilies.Length == 0)
                continue;
            if (!normalEquipmentMaster!.TryGet(
                    item.ContentType,
                    item.ContentId,
                    out NormalEquipmentMasterInfo candidate
                ))
            {
                return Error(
                    $"missing-normal-family-candidate:{item.ContentType}:{item.ContentId}"
                );
            }
            var matchedFamily = applicableFamilies.FirstOrDefault(pair =>
                normalEquipmentMaster!.IsSameFamilyAtOrAbove(pair.Info, candidate));
            if (matchedFamily.Target.MatchMode == NormalDropTargetMatchMode.FamilyAtOrAbove)
            {
                targets.Add(item);
                matchedTargetDetails.Add(
                    $"{matchedFamily.Target.Token}=>{actualTarget.Token}"
                        + $"(group={candidate.GroupNo},rank={candidate.Rank},rarity={candidate.Rarity})"
                );
            }
        }
        return new BattleSessionDropEvaluation(targets, matchedTargetDetails);
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

    public static string DescribeNormalStopCondition(
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity,
        BattleSessionNormalContentTypeFilter contentTypes,
        string normalExactTargets
    )
    {
        NormalExactDropTargetParseResult exact = NormalExactDropTargetParser.Parse(
            normalExactTargets
        );
        return exact.Mode switch
        {
            NormalExactDropTargetMode.Enabled => $"exactTargets={exact.Description}",
            NormalExactDropTargetMode.Invalid => $"exactTargets={exact.Description}",
            _ => DescribeNormalStopCondition(stopMode, minimumRarity, contentTypes),
        };
    }

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
