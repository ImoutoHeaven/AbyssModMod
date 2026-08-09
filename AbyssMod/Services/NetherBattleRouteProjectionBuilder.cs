#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AbyssMod.Services;

/// <summary>
/// A single active Nether-code master already mapped by the runtime bridge.  The runtime must
/// preserve the raw code ID and effect semantics rather than collapsing an unrecognised code to
/// a zero modifier.
/// </summary>
internal readonly record struct NetherCodeEffect(
    long CodeId,
    NetherCodeEffectKind EffectKind,
    int Amount
)
{
    public bool IsKnown { get; init; } = true;
    public bool OrderKnown { get; init; } = true;
}

/// <summary>
/// Authoritative inputs for one combat-shaped map floor. Min/max are the effective battle base
/// erosion range before active Nether-code modifiers; production currently supplies the server
/// baseline 5..5. They are nullable so missing battle authority cannot become a harmless zero.
/// </summary>
internal sealed record NetherBattleRouteProjectionInput(
    long FloorId,
    NetherFloorNodeType FloorKind,
    int? MinimumErosionPoint,
    int? MaximumErosionPoint,
    int CurrentErosion,
    IReadOnlyList<int> ActiveHpPermille,
    IReadOnlyList<NetherCodeEffect> ActiveCodeEffects,
    string CodeHash,
    NetherAutoClimbSettings Settings,
    int HardErosionLimit
)
{
    public bool HasMasterData { get; init; } = true;
    public bool IsCodeHashKnown { get; init; } = true;
    public bool AllInputsKnown { get; init; } = true;
}

/// <summary>
/// A fail-closed combat projection.  The input is retained only after every master/code field
/// is mapped; callers therefore cannot accidentally pass a fabricated input to route planning.
/// </summary>
internal sealed record NetherBattleRouteProjection
{
    public NetherFloorSafetyInput? EvaluatorInput { get; init; }
    public NetherFloorSafetyEvaluation? Evaluation { get; init; }
    public int? ProjectedMinimumErosion { get; init; }
    public int? ProjectedMaximumErosion { get; init; }
    public string ProjectionIdentity { get; init; } = string.Empty;
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public bool IsSafe => Evaluation is { IsSafe: true } && PauseReason == NetherPauseReason.None;
}

/// <summary>
/// Converts the exact combat base range and active code semantics into one evaluator
/// input.  It independently invokes <see cref="NetherErosionPolicy.ProjectBattle"/> for both
/// bounds before invoking <see cref="NetherFloorSafetyEvaluator"/>; disagreement is unsafe.
/// </summary>
internal sealed class NetherBattleRouteProjectionBuilder
{
    private readonly NetherErosionPolicy _erosionPolicy = new();
    private readonly NetherFloorSafetyEvaluator _safetyEvaluator = new();

    public NetherBattleRouteProjection Build(NetherBattleRouteProjectionInput input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (!TryValidateStaticInput(input, out NetherFloorSafetyKind kind, out NetherPauseReason inputReason, out string inputDetail))
            return Pause(inputReason, inputDetail);
        if (!TryMapModifiers(input.ActiveCodeEffects, out IReadOnlyList<NetherErosionModifier>? modifiers, out string modifierError))
            return Pause(NetherPauseReason.UnknownEffect, modifierError);

        int minimum = input.MinimumErosionPoint!.Value;
        int maximum = input.MaximumErosionPoint!.Value;
        bool isMandatoryTerminal = kind == NetherFloorSafetyKind.NecessaryTerminal;
        NetherErosionProjection minimumPolicy = _erosionPolicy.ProjectBattle(
            input.CurrentErosion,
            minimum,
            modifiers!,
            input.Settings.SoftErosionLimit,
            isMandatoryTerminal
        );
        NetherErosionProjection maximumPolicy = _erosionPolicy.ProjectBattle(
            input.CurrentErosion,
            maximum,
            modifiers!,
            input.Settings.SoftErosionLimit,
            isMandatoryTerminal
        );
        if (minimumPolicy.PauseReason is NetherPauseReason.UnknownEffect or NetherPauseReason.InvalidConfiguration)
            return Pause(minimumPolicy.PauseReason, "minimum-policy:" + minimumPolicy.Detail);
        if (maximumPolicy.PauseReason is NetherPauseReason.UnknownEffect or NetherPauseReason.InvalidConfiguration)
            return Pause(maximumPolicy.PauseReason, "maximum-policy:" + maximumPolicy.Detail);

        NetherFloorSafetyInput evaluatorInput = new(
            CurrentErosion: input.CurrentErosion,
            FloorMinimumErosion: minimum,
            FloorMaximumErosion: maximum,
            KnownModifierDelta: 0,
            Kind: kind,
            NodeType: input.FloorKind,
            CurrentHpPermille: input.ActiveHpPermille,
            MinimumHpPermille: input.Settings.MinimumCharacterHpPermille,
            SoftErosionLimit: input.Settings.SoftErosionLimit,
            HardErosionLimit: input.HardErosionLimit,
            AllInputsKnown: true
        )
        {
            ErosionModifiers = modifiers,
        };
        NetherFloorSafetyEvaluation evaluation = _safetyEvaluator.Evaluate(evaluatorInput);
        if (evaluation.ProjectedMinimumErosion != minimumPolicy.ProjectedErosion
            || evaluation.ProjectedMaximumErosion != maximumPolicy.ProjectedErosion)
        {
            return Pause(NetherPauseReason.UnknownEffect, "policy-evaluator-projection-mismatch");
        }

        return new NetherBattleRouteProjection
        {
            EvaluatorInput = evaluatorInput,
            Evaluation = evaluation,
            ProjectedMinimumErosion = evaluation.ProjectedMinimumErosion,
            ProjectedMaximumErosion = evaluation.ProjectedMaximumErosion,
            ProjectionIdentity = CreateIdentity(input),
            PauseReason = evaluation.PauseReason,
            Detail = evaluation.Detail,
        };
    }

    private static bool TryValidateStaticInput(
        NetherBattleRouteProjectionInput input,
        out NetherFloorSafetyKind kind,
        out NetherPauseReason reason,
        out string detail
    )
    {
        kind = NetherFloorSafetyKind.Optional;
        reason = NetherPauseReason.UnknownMasterData;
        detail = string.Empty;
        if (!input.AllInputsKnown || !input.HasMasterData)
        {
            detail = "unknown-battle-master-input";
            return false;
        }
        if (input.FloorId <= 0 || !input.MinimumErosionPoint.HasValue || !input.MaximumErosionPoint.HasValue)
        {
            detail = "missing-m-nether-map-floor-erosion";
            return false;
        }
        if (input.MinimumErosionPoint.Value > input.MaximumErosionPoint.Value)
        {
            detail = "invalid-m-nether-map-floor-erosion-range";
            return false;
        }
        if (input.Settings == null)
        {
            detail = "missing-nether-safety-settings";
            return false;
        }
        if (!input.IsCodeHashKnown || input.CodeHash == null)
        {
            reason = NetherPauseReason.UnknownEffect;
            detail = "unknown-code-fingerprint";
            return false;
        }
        if (input.ActiveHpPermille == null || input.ActiveHpPermille.Count == 0)
        {
            detail = "missing-active-party-hp";
            return false;
        }

        switch (input.FloorKind)
        {
            case NetherFloorNodeType.Battle:
            case NetherFloorNodeType.MiniBoss:
                kind = NetherFloorSafetyKind.Optional;
                return true;
            case NetherFloorNodeType.Boss:
                kind = NetherFloorSafetyKind.NecessaryTerminal;
                return true;
            default:
                detail = "non-combat-route-floor:" + input.FloorKind;
                return false;
        }
    }

    private static bool TryMapModifiers(
        IReadOnlyList<NetherCodeEffect>? effects,
        out IReadOnlyList<NetherErosionModifier>? modifiers,
        out string error
    )
    {
        modifiers = null;
        if (effects == null)
        {
            error = "missing-active-nether-code-effects";
            return false;
        }

        var mapped = new List<NetherErosionModifier>();
        foreach (NetherCodeEffect effect in effects)
        {
            if (effect.CodeId <= 0 || effect.Amount < 0 || !effect.IsKnown || !effect.OrderKnown)
            {
                error = "unknown-active-nether-code-effect:" + effect.CodeId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            switch (effect.EffectKind)
            {
                case NetherCodeEffectKind.ErosionAdditionUp:
                    mapped.Add(new NetherErosionModifier(NetherErosionOperation.Addition, effect.Amount, isIncrease: true));
                    break;
                case NetherCodeEffectKind.ErosionAdditionDown:
                    mapped.Add(new NetherErosionModifier(NetherErosionOperation.Addition, effect.Amount, isIncrease: false));
                    break;
                case NetherCodeEffectKind.ErosionRateUp:
                    mapped.Add(new NetherErosionModifier(NetherErosionOperation.Rate, effect.Amount, isIncrease: true));
                    break;
                case NetherCodeEffectKind.ErosionRateDown:
                    mapped.Add(new NetherErosionModifier(NetherErosionOperation.Rate, effect.Amount, isIncrease: false));
                    break;
                case NetherCodeEffectKind.Safe:
                case NetherCodeEffectKind.Risk:
                case NetherCodeEffectKind.Rush:
                case NetherCodeEffectKind.Impact:
                case NetherCodeEffectKind.ResearchOnly:
                    break;
                default:
                    error = "unknown-active-nether-code-effect-kind:" + effect.CodeId.ToString(CultureInfo.InvariantCulture);
                    return false;
            }
        }

        modifiers = mapped;
        error = string.Empty;
        return true;
    }

    private static string CreateIdentity(NetherBattleRouteProjectionInput input) => string.Join(
        ":",
        "route-battle",
        input.FloorId.ToString(CultureInfo.InvariantCulture),
        ((int)input.FloorKind).ToString(CultureInfo.InvariantCulture),
        input.CurrentErosion.ToString(CultureInfo.InvariantCulture),
        input.MinimumErosionPoint!.Value.ToString(CultureInfo.InvariantCulture),
        input.MaximumErosionPoint!.Value.ToString(CultureInfo.InvariantCulture),
        input.CodeHash
    );

    private static NetherBattleRouteProjection Pause(NetherPauseReason reason, string detail) => new()
    {
        PauseReason = reason,
        Detail = detail,
    };
}
