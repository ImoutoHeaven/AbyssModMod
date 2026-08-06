#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AbyssMod.Services;

public enum NetherSlTarget
{
    Off = -1,
    NoEffect = 0,
    Silver = 1,
    Purple = 2,
    Gold = 3,
    Red = 4,
    UniqueWeapon = 5,
}

public enum NetherEncounterKind
{
    Unknown = 0,
    Battle = 1,
    MiniBoss = 2,
    Boss = 3,
}

public readonly record struct NetherFloorStrategyDecision(
    NetherSlTarget Target,
    bool Matched,
    int ClauseIndex,
    string Selector
);

public sealed class NetherFloorStrategy
{
    private readonly IReadOnlyList<Clause> _clauses;

    internal NetherFloorStrategy(IReadOnlyList<Clause> clauses) => _clauses = clauses;

    public NetherFloorStrategyDecision Resolve(int floorLevel)
    {
        if (floorLevel < 1)
            return new NetherFloorStrategyDecision(NetherSlTarget.Off, false, -1, string.Empty);

        NetherFloorStrategyDecision decision = new(NetherSlTarget.Off, false, -1, string.Empty);
        for (int index = 0; index < _clauses.Count; index++)
        {
            Clause clause = _clauses[index];
            if (clause.Matches(floorLevel))
                decision = new NetherFloorStrategyDecision(clause.Target, true, index, clause.Selector);
        }

        return decision;
    }

    internal sealed class Clause
    {
        private readonly IReadOnlyList<FloorSelector> _selectors;

        public Clause(IReadOnlyList<FloorSelector> selectors, string selector, NetherSlTarget target)
        {
            _selectors = selectors;
            Selector = selector;
            Target = target;
        }

        public string Selector { get; }
        public NetherSlTarget Target { get; }

        public bool Matches(int floorLevel)
        {
            foreach (FloorSelector selector in _selectors)
            {
                if (selector.Matches(floorLevel))
                    return true;
            }
            return false;
        }
    }

    internal readonly record struct FloorSelector(int Start, int? End)
    {
        public bool Matches(int floorLevel) => floorLevel >= Start && (End == null || floorLevel <= End.Value);
    }
}

public static class NetherFloorStrategyParser
{
    public static bool TryParse(string? value, out NetherFloorStrategy? strategy, out string error)
    {
        strategy = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "empty-strategy";
            return false;
        }

        string[] clauses = value.Split(';');
        var parsedClauses = new List<NetherFloorStrategy.Clause>(clauses.Length);
        for (int clauseIndex = 0; clauseIndex < clauses.Length; clauseIndex++)
        {
            string clause = clauses[clauseIndex].Trim();
            if (clause.Length == 0)
            {
                error = $"empty-clause:{clauseIndex}";
                return false;
            }

            int equalsIndex = clause.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex != clause.LastIndexOf('='))
            {
                error = $"invalid-clause:{clauseIndex}";
                return false;
            }

            if (!TryParseTarget(clause[(equalsIndex + 1)..].Trim(), out NetherSlTarget target))
            {
                error = $"invalid-target:{clauseIndex}";
                return false;
            }

            if (!TryParseSelectors(clause[..equalsIndex], out List<NetherFloorStrategy.FloorSelector>? selectors, out string normalizedSelector))
            {
                error = $"invalid-selector:{clauseIndex}";
                return false;
            }

            parsedClauses.Add(new NetherFloorStrategy.Clause(selectors!, normalizedSelector, target));
        }

        strategy = new NetherFloorStrategy(parsedClauses);
        return true;
    }

    private static bool TryParseTarget(string value, out NetherSlTarget target)
    {
        target = value switch
        {
            var name when string.Equals(name, "Off", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.Off,
            var name when string.Equals(name, "NoEffect", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.NoEffect,
            var name when string.Equals(name, "Silver", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.Silver,
            var name when string.Equals(name, "Purple", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.Purple,
            var name when string.Equals(name, "Gold", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.Gold,
            var name when string.Equals(name, "Red", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.Red,
            var name when string.Equals(name, "UniqueWeapon", StringComparison.OrdinalIgnoreCase) => NetherSlTarget.UniqueWeapon,
            _ => (NetherSlTarget)int.MinValue,
        };
        return target != (NetherSlTarget)int.MinValue;
    }

    private static bool TryParseSelectors(
        string value,
        out List<NetherFloorStrategy.FloorSelector>? selectors,
        out string normalizedSelector
    )
    {
        selectors = new List<NetherFloorStrategy.FloorSelector>();
        normalizedSelector = string.Empty;
        string[] floorSpecs = value.Split(',');
        foreach (string floorSpec in floorSpecs)
        {
            string normalized = floorSpec.Trim();
            if (!TryParseFloorSpec(normalized, out NetherFloorStrategy.FloorSelector selector, out string canonicalSelector))
            {
                selectors = null;
                return false;
            }
            if (normalizedSelector.Length > 0)
                normalizedSelector += ",";
            normalizedSelector += canonicalSelector;
            selectors.Add(selector);
        }

        return selectors.Count > 0;
    }

    private static bool TryParseFloorSpec(
        string value,
        out NetherFloorStrategy.FloorSelector selector,
        out string canonicalSelector
    )
    {
        selector = default;
        canonicalSelector = string.Empty;
        if (value == "*")
        {
            selector = new NetherFloorStrategy.FloorSelector(1, null);
            canonicalSelector = "*";
            return true;
        }

        int dashIndex = value.IndexOf('-');
        if (dashIndex < 0)
        {
            if (!TryParsePositive(value, out int exact))
                return false;
            selector = new NetherFloorStrategy.FloorSelector(exact, exact);
            canonicalSelector = exact.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (dashIndex == 0 || dashIndex != value.LastIndexOf('-'))
            return false;

        string startText = value[..dashIndex].Trim();
        string endText = value[(dashIndex + 1)..].Trim();
        if (!TryParsePositive(startText, out int start))
            return false;
        if (endText == "*")
        {
            selector = new NetherFloorStrategy.FloorSelector(start, null);
            canonicalSelector = start.ToString(CultureInfo.InvariantCulture) + "-*";
            return true;
        }
        if (!TryParsePositive(endText, out int end) || start > end)
            return false;

        selector = new NetherFloorStrategy.FloorSelector(start, end);
        canonicalSelector = start.ToString(CultureInfo.InvariantCulture) + "-" + end.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryParsePositive(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result) && result >= 1;

}

public static class NetherEncounterClassifier
{
    public static NetherEncounterKind Classify(int rawFloorType) => rawFloorType switch
    {
        1 => NetherEncounterKind.Battle,
        2 => NetherEncounterKind.Boss,
        3 => NetherEncounterKind.MiniBoss,
        4 => NetherEncounterKind.Battle,
        _ => NetherEncounterKind.Unknown,
    };
}

public static class NetherFloorStrategySelector
{
    public static NetherFloorStrategyDecision Resolve(
        string? battleStrategy,
        string? miniBossStrategy,
        string? bossStrategy,
        NetherEncounterKind encounterKind,
        int floorLevel,
        out string error
    )
    {
        string? selected = encounterKind switch
        {
            NetherEncounterKind.Battle => battleStrategy,
            NetherEncounterKind.MiniBoss => miniBossStrategy,
            NetherEncounterKind.Boss => bossStrategy,
            _ => null,
        };
        if (selected == null)
        {
            error = "unknown-encounter";
            return new NetherFloorStrategyDecision(NetherSlTarget.Off, false, -1, string.Empty);
        }

        if (!NetherFloorStrategyParser.TryParse(selected, out NetherFloorStrategy? strategy, out error))
            return new NetherFloorStrategyDecision(NetherSlTarget.Off, false, -1, string.Empty);

        return strategy!.Resolve(floorLevel);
    }
}

public static class NetherStrategyDefaults
{
    public const string Battle = "1-49=Off;50-*=Gold";
    public const string MiniBoss = "1-49=Off;50-*=Gold";
    public const string Boss = "1-49=Off;50-99=Gold;100-*=Red";
}

public readonly record struct NetherStrategySettings(
    string? BattleStrategy,
    string? MiniBossStrategy,
    string? BossStrategy
)
{
    public static NetherStrategySettings Default => new(
        NetherStrategyDefaults.Battle,
        NetherStrategyDefaults.MiniBoss,
        NetherStrategyDefaults.Boss
    );
}

public enum NetherRuntimeDecisionKind
{
    Evaluate = 0,
    AcceptOff = 1,
    AcceptError = 2,
}

public enum NetherAttemptLogLevel
{
    Info = 0,
    Warning = 1,
}

public readonly record struct NetherRuntimeDecision(
    NetherRuntimeDecisionKind Kind,
    NetherSlTarget Target,
    NetherEncounterKind EncounterKind,
    int RawFloorType,
    string ConfigKey,
    string StrategyText,
    NetherFloorStrategyDecision StrategyDecision,
    string Reason
)
{
    public bool RequiresDropEvaluation => Kind == NetherRuntimeDecisionKind.Evaluate;
    public bool ShouldRetry => false;
}

public readonly record struct NetherAttemptLogContext(
    string RawPreserveItemIds,
    NetherPreserveMode PreserveMode,
    NetherRuntimeDecision Decision
)
{
    public NetherAttemptLogLevel Level => Decision.EncounterKind == NetherEncounterKind.Unknown
        || Decision.Reason == "strategy-unmatched"
        ? NetherAttemptLogLevel.Warning
        : NetherAttemptLogLevel.Info;
}

public static class NetherRuntimeDecisionEngine
{
    public static NetherRuntimeDecision CreateBypass(string reason) => Create(
        NetherRuntimeDecisionKind.AcceptOff,
        NetherSlTarget.Off,
        NetherEncounterKind.Unknown,
        0,
        "none",
        string.Empty,
        new NetherFloorStrategyDecision(NetherSlTarget.Off, false, -1, string.Empty),
        reason
    );

    public static NetherRuntimeDecision Resolve(
        int rawFloorType,
        int floorLevel,
        NetherStrategySettings settings
    )
    {
        NetherEncounterKind encounterKind = NetherEncounterClassifier.Classify(rawFloorType);
        if (encounterKind == NetherEncounterKind.Unknown)
            return Create(NetherRuntimeDecisionKind.AcceptOff, NetherSlTarget.Off, encounterKind, rawFloorType,
                "none", string.Empty, new NetherFloorStrategyDecision(NetherSlTarget.Off, false, -1, string.Empty), "unknown-floor-type");

        (string configKey, string? strategyText) = encounterKind switch
        {
            NetherEncounterKind.Battle => ("NetherBattleStrategy", settings.BattleStrategy),
            NetherEncounterKind.MiniBoss => ("NetherMiniBossStrategy", settings.MiniBossStrategy),
            NetherEncounterKind.Boss => ("NetherBossStrategy", settings.BossStrategy),
            _ => ("none", null),
        };
        string rawText = strategyText ?? string.Empty;
        NetherFloorStrategyDecision floorDecision = NetherFloorStrategySelector.Resolve(
            settings.BattleStrategy,
            settings.MiniBossStrategy,
            settings.BossStrategy,
            encounterKind,
            floorLevel,
            out string error
        );
        if (error.Length != 0)
            return Create(NetherRuntimeDecisionKind.AcceptError, NetherSlTarget.Off, encounterKind, rawFloorType,
                configKey, rawText, floorDecision, error);

        if (!floorDecision.Matched)
            return Create(NetherRuntimeDecisionKind.AcceptOff, NetherSlTarget.Off, encounterKind, rawFloorType,
                configKey, rawText, floorDecision, "strategy-unmatched");
        if (floorDecision.Target == NetherSlTarget.Off)
            return Create(NetherRuntimeDecisionKind.AcceptOff, NetherSlTarget.Off, encounterKind, rawFloorType,
                configKey, rawText, floorDecision, "strategy-off");

        return Create(NetherRuntimeDecisionKind.Evaluate, floorDecision.Target, encounterKind, rawFloorType,
            configKey, rawText, floorDecision, string.Empty);
    }

    private static NetherRuntimeDecision Create(
        NetherRuntimeDecisionKind kind, NetherSlTarget target, NetherEncounterKind encounterKind,
        int rawFloorType, string configKey, string strategyText, NetherFloorStrategyDecision strategyDecision,
        string reason
    ) => new(kind, target, encounterKind, rawFloorType, configKey, strategyText, strategyDecision, reason);
}
