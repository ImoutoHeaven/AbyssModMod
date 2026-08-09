#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

internal enum NetherErosionOperation
{
    Addition,
    Rate,
}

internal readonly record struct NetherErosionModifier(
    NetherErosionOperation operation,
    int amount,
    bool isIncrease = true,
    bool known = true,
    bool orderKnown = true
)
{
    public NetherErosionOperation Operation => operation;
    public int Amount => amount;
    public bool IsIncrease => isIncrease;
    public bool Known => known;
    public bool OrderKnown => orderKnown;
}

internal readonly record struct NetherErosionProjection(
    bool IsAllowed,
    int ProjectedErosion,
    NetherPauseReason PauseReason,
    string Detail
);

internal readonly record struct NetherErosionObservation(
    bool IsDrift,
    bool RequiresRebaseline,
    NetherPauseReason PauseReason,
    string Detail
);

internal sealed class NetherErosionPolicy
{
    public NetherErosionProjection ProjectBattle(
        int currentErosion,
        int baseDelta,
        IReadOnlyList<NetherErosionModifier> modifiers,
        int softLimit,
        bool isMandatoryBoss
    )
    {
        if (!IsValidLimit(softLimit))
            return Pause(currentErosion, NetherPauseReason.InvalidConfiguration, "invalid-soft-limit");
        if (modifiers == null)
            throw new ArgumentNullException(nameof(modifiers));

        foreach (NetherErosionModifier modifier in modifiers)
        {
            if (!modifier.Known || !modifier.OrderKnown)
                return Pause(currentErosion, NetherPauseReason.UnknownEffect, "unknown-effect-order");
        }

        try
        {
            int delta = baseDelta;
            foreach (NetherErosionModifier modifier in modifiers)
            {
                if (modifier.Operation == NetherErosionOperation.Addition)
                    delta = checked(delta + Signed(modifier));
            }
            foreach (NetherErosionModifier modifier in modifiers)
            {
                if (modifier.Operation != NetherErosionOperation.Rate)
                    continue;
                int factor = checked(1000 + Signed(modifier));
                if (factor < 0)
                    return Pause(currentErosion, NetherPauseReason.UnknownEffect, "negative-erosion-rate");
                delta = checked(delta * factor / 1000);
            }
            return Evaluate(currentErosion, delta, softLimit, isMandatoryBoss);
        }
        catch (OverflowException)
        {
            return Pause(currentErosion, NetherPauseReason.UnknownEffect, "erosion-overflow");
        }
    }

    public NetherErosionProjection ProjectEffects(
        int currentErosion,
        IReadOnlyList<NetherEffect> effects,
        int softLimit,
        bool isMandatoryBoss
    )
    {
        if (!IsValidLimit(softLimit))
            return Pause(currentErosion, NetherPauseReason.InvalidConfiguration, "invalid-soft-limit");
        if (effects == null)
            throw new ArgumentNullException(nameof(effects));
        if (effects.Count > 4)
            return Pause(currentErosion, NetherPauseReason.UnknownEffect, "too-many-event-effects");

        try
        {
            int delta = 0;
            foreach (NetherEffect effect in effects)
            {
                if (!effect.Known || !effect.ContentKnown)
                    return Pause(currentErosion, NetherPauseReason.UnknownEffect, "unknown-event-effect");
                delta = effect.Kind switch
                {
                    NetherEffectKind.Erosion => checked(delta + effect.Amount),
                    NetherEffectKind.ErosionHeal => checked(delta - effect.Amount),
                    NetherEffectKind.Heal or NetherEffectKind.Damage or NetherEffectKind.NetherGoldUsed
                        or NetherEffectKind.TreasureKeyUsed or NetherEffectKind.AbyssCodeTransform
                        or NetherEffectKind.Battle or NetherEffectKind.Item or NetherEffectKind.NetherGoldGain
                        or NetherEffectKind.TreasureKeyGain or NetherEffectKind.AbyssCodeOffer => delta,
                    _ => throw new UnknownEffectException(),
                };
            }
            return Evaluate(currentErosion, delta, softLimit, isMandatoryBoss);
        }
        catch (UnknownEffectException)
        {
            return Pause(currentErosion, NetherPauseReason.UnknownEffect, "unknown-event-effect");
        }
        catch (OverflowException)
        {
            return Pause(currentErosion, NetherPauseReason.UnknownEffect, "erosion-overflow");
        }
    }

    public NetherErosionObservation CompareObserved(
        int predictedErosion,
        int observedErosion,
        string predictedCodeFingerprint,
        string observedCodeFingerprint
    )
    {
        if (!string.Equals(predictedCodeFingerprint, observedCodeFingerprint, StringComparison.Ordinal))
            return new NetherErosionObservation(false, true, NetherPauseReason.None, "code-fingerprint-changed");
        if (predictedErosion != observedErosion)
            return new NetherErosionObservation(true, false, NetherPauseReason.ErosionDrift, "erosion-drift");
        return new NetherErosionObservation(false, false, NetherPauseReason.None, string.Empty);
    }

    private static NetherErosionProjection Evaluate(
        int currentErosion,
        int delta,
        int softLimit,
        bool isMandatoryBoss
    )
    {
        try
        {
            int projected = checked(currentErosion + delta);
            if (projected < 0 || projected >= 100)
                return Pause(projected, NetherPauseReason.UnsafeErosion, "hard-erosion-limit");
            if (!isMandatoryBoss && projected >= softLimit)
                return Pause(projected, NetherPauseReason.UnsafeErosion, "soft-erosion-limit");
            return new NetherErosionProjection(true, projected, NetherPauseReason.None, string.Empty);
        }
        catch (OverflowException)
        {
            return Pause(currentErosion, NetherPauseReason.UnknownEffect, "erosion-overflow");
        }
    }

    private static int Signed(NetherErosionModifier modifier) => modifier.IsIncrease ? modifier.Amount : -modifier.Amount;
    private static bool IsValidLimit(int softLimit) => softLimit is >= 1 and <= 99;
    private static NetherErosionProjection Pause(int projected, NetherPauseReason reason, string detail) => new(false, projected, reason, detail);

    private sealed class UnknownEffectException : Exception { }
}
