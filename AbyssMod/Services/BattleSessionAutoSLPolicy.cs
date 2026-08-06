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

public static class BattleSessionAutoSLPolicy
{
    public static float ClampCooldown(float seconds) => Math.Max(0f, seconds);

    public static BattleSessionDropEvaluation Evaluate(
        BattleDropProbeReport report,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity
    )
    {
        if (report == null)
            return Error("missing-report");
        if (report.Error.Length != 0)
            return Error(report.Error);
        string validationError = GetStopConditionError(stopMode, minimumRarity);
        if (validationError.Length != 0)
            return Error(validationError);

        var targets = new List<BattleDropItem>();
        foreach (BattleDropItem item in report.Items)
        {
            if (Matches(item, stopMode, minimumRarity))
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
