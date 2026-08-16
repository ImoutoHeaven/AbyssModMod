using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

public static class TavernFirstCardHookPlan
{
    public const string InterceptionType = "Project.Tavern.Top.GameViewController";
    public const string InterceptionMethod = "CreateGameData";
    public const bool InterceptsStaticGenericUniTask = false;

    public static bool IsNativeAbiSafe =>
        InterceptionType == "Project.Tavern.Top.GameViewController"
        && InterceptionMethod == "CreateGameData"
        && !InterceptsStaticGenericUniTask;
}

public enum TavernFirstCardTarget
{
    Off = 0,
    Cook = 1,
    Waitress = 2,
    Drink = 3,
}

public readonly record struct TavernCardEffect
{
    public int TargetType { get; }
    public int EffectType { get; }
    public int EffectParam { get; }

    public TavernCardEffect(int targetType, int effectType, int effectParam)
    {
        TargetType = targetType;
        EffectType = effectType;
        EffectParam = effectParam;
    }
}

public sealed class TavernCardCandidate
{
    public long ServerCardId { get; }
    public long MasterCardId { get; }
    public IReadOnlyList<TavernCardEffect> Effects { get; }

    public TavernCardCandidate(
        long serverCardId,
        long masterCardId,
        IReadOnlyList<TavernCardEffect> effects
    )
    {
        ServerCardId = serverCardId;
        MasterCardId = masterCardId;
        Effects = effects;
    }
}

public sealed class TavernFirstCardEvaluation
{
    public IReadOnlyList<TavernCardCandidate> Matches { get; }
    public bool ShouldRetry { get; }

    public TavernFirstCardEvaluation(
        IReadOnlyList<TavernCardCandidate> matches,
        bool shouldRetry
    )
    {
        Matches = matches;
        ShouldRetry = shouldRetry;
    }
}

public static class TavernFirstCardAutoSLPolicy
{
    private const int TargetTypeAll = 1;
    private const int EffectTypeValueRate = 10;
    private const int EffectTypeAddCategory = 20;
    private const int FivePercentEffectParam = 50;

    public static bool IsFirstCardTurn(int selectedCount) => selectedCount == 0;

    public static bool TryParseTarget(string configured, out TavernFirstCardTarget target)
    {
        string value = configured?.Trim() ?? string.Empty;
        if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
            target = TavernFirstCardTarget.Off;
        else if (value.Equals("cook", StringComparison.OrdinalIgnoreCase))
            target = TavernFirstCardTarget.Cook;
        else if (value.Equals("waitress", StringComparison.OrdinalIgnoreCase))
            target = TavernFirstCardTarget.Waitress;
        else if (value.Equals("drink", StringComparison.OrdinalIgnoreCase))
            target = TavernFirstCardTarget.Drink;
        else
        {
            target = TavernFirstCardTarget.Off;
            return false;
        }

        return true;
    }

    public static TavernFirstCardEvaluation Evaluate(
        IReadOnlyList<TavernCardCandidate> candidates,
        TavernFirstCardTarget target
    )
    {
        if (target == TavernFirstCardTarget.Off)
            return new TavernFirstCardEvaluation([], shouldRetry: false);

        var matches = new List<TavernCardCandidate>();
        foreach (TavernCardCandidate candidate in candidates)
        {
            int? category = null;
            TavernCardEffect? valueRate = null;
            foreach (TavernCardEffect effect in candidate.Effects)
            {
                if (
                    !category.HasValue
                    && effect.EffectType == EffectTypeAddCategory
                )
                    category = effect.EffectParam;
                if (!valueRate.HasValue && effect.EffectType == EffectTypeValueRate)
                    valueRate = effect;
            }

            bool matchesFivePercentAll = valueRate.HasValue
                && valueRate.Value.TargetType == TargetTypeAll
                && valueRate.Value.EffectParam == FivePercentEffectParam;
            if (category == (int)target && matchesFivePercentAll)
                matches.Add(candidate);
        }

        return new TavernFirstCardEvaluation(matches, shouldRetry: matches.Count == 0);
    }
}
