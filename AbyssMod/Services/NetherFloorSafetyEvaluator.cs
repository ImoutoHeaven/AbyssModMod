#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Distinguishes a discretionary node from the one terminal action that is necessary to
/// leave a segment.  A terminal exception applies only to the soft erosion limit; the hard
/// limit always remains strict.
/// </summary>
internal enum NetherFloorSafetyKind
{
    Optional,
    NecessaryTerminal,
}

/// <summary>
/// All values are authoritative inputs captured for one floor decision.  A caller that cannot
/// map any one of them must set <see cref="AllInputsKnown"/> false rather than synthesizing a
/// zero delta or a safe HP value.
/// </summary>
internal readonly record struct NetherFloorSafetyInput(
    int CurrentErosion,
    int FloorMinimumErosion,
    int FloorMaximumErosion,
    int KnownModifierDelta,
    NetherFloorSafetyKind Kind,
    NetherFloorNodeType NodeType,
    IReadOnlyList<int> CurrentHpPermille,
    int MinimumHpPermille,
    int SoftErosionLimit,
    int HardErosionLimit,
    bool AllInputsKnown
)
{
    /// <summary>
    /// Exact modifiers mapped from the active Nether-code masters.  The scalar delta remains
    /// for already-normalized sources; modifiers are then applied in the native policy order.
    /// A null list is unknown, never equivalent to an empty modifier portfolio.
    /// </summary>
    public IReadOnlyList<NetherErosionModifier>? ErosionModifiers { get; init; } = Array.Empty<NetherErosionModifier>();
}

/// <summary>
/// A numeric projection is supplied only when it was calculated without overflow from known
/// inputs.  Null bounds therefore mean unknown, not an implicit zero-risk projection.
/// </summary>
internal readonly record struct NetherFloorSafetyEvaluation(
    bool IsSafe,
    int? ProjectedMinimumErosion,
    int? ProjectedMaximumErosion,
    NetherPauseReason PauseReason,
    string Detail
);

/// <summary>
/// Fail-closed per-floor erosion and HP gate.  The route builder can use its bounds to compose
/// a reverse terminal worst case without relaxing the action-level soft/hard rules.
/// </summary>
internal sealed class NetherFloorSafetyEvaluator
{
    private readonly NetherErosionPolicy _erosionPolicy = new();

    public NetherFloorSafetyEvaluation Evaluate(NetherFloorSafetyInput input)
    {
        if (!input.AllInputsKnown)
            return Unknown("unknown-authoritative-input");
        if (!IsKnownKind(input.Kind) || input.NodeType is NetherFloorNodeType.Unknown or NetherFloorNodeType.Default)
            return Unknown("unknown-floor-safety-kind");
        if (!HasValidLimits(input))
            return Pause(NetherPauseReason.InvalidConfiguration, "invalid-safety-limits");
        if (input.FloorMinimumErosion > input.FloorMaximumErosion)
            return Unknown("invalid-floor-erosion-range");
        if (!HasKnownHp(input.CurrentHpPermille, input.MinimumHpPermille))
            return Unknown("unknown-current-hp");
        if (input.ErosionModifiers == null)
            return Unknown("missing-erosion-modifier-list");

        if (!TryProject(input, input.FloorMinimumErosion, out int projectedMinimum, out NetherPauseReason minimumReason, out string minimumDetail))
        {
            return Pause(minimumReason, minimumDetail);
        }
        if (!TryProject(input, input.FloorMaximumErosion, out int projectedMaximum, out NetherPauseReason maximumReason, out string maximumDetail))
        {
            return Pause(maximumReason, maximumDetail);
        }

        if (projectedMinimum < 0 || projectedMaximum < projectedMinimum)
            return Pause(
                NetherPauseReason.UnsafeErosion,
                "invalid-projected-erosion-range",
                projectedMinimum,
                projectedMaximum
            );

        if (input.Kind == NetherFloorSafetyKind.Optional && projectedMaximum >= input.SoftErosionLimit)
        {
            return Pause(
                NetherPauseReason.UnsafeErosion,
                "optional-soft-erosion-limit",
                projectedMinimum,
                projectedMaximum
            );
        }
        if (input.Kind == NetherFloorSafetyKind.NecessaryTerminal && projectedMaximum >= input.HardErosionLimit)
        {
            return Pause(
                NetherPauseReason.UnsafeErosion,
                "terminal-hard-erosion-limit",
                projectedMinimum,
                projectedMaximum
            );
        }
        // Necessary terminal status relaxes only the erosion soft cap.  It never authorizes
        // entering a Boss below the configured party HP floor.
        if (IsCombat(input.NodeType)
            && HasHpBelowMinimum(input.CurrentHpPermille, input.MinimumHpPermille))
        {
            return Pause(
                NetherPauseReason.UnsafeHp,
                "optional-battle-hp-floor",
                projectedMinimum,
                projectedMaximum
            );
        }

        return new NetherFloorSafetyEvaluation(
            IsSafe: true,
            ProjectedMinimumErosion: projectedMinimum,
            ProjectedMaximumErosion: projectedMaximum,
            PauseReason: NetherPauseReason.None,
            Detail: string.Empty
        );
    }

    private bool TryProject(
        NetherFloorSafetyInput input,
        int floorErosion,
        out int projected,
        out NetherPauseReason pauseReason,
        out string detail
    )
    {
        projected = 0;
        pauseReason = NetherPauseReason.None;
        detail = string.Empty;
        try
        {
            int baseDelta = checked(floorErosion + input.KnownModifierDelta);
            // The policy is deliberately evaluated as mandatory here so it supplies the exact
            // native modifier arithmetic for both bounds.  The caller below applies the single
            // optional-vs-terminal soft/hard decision to the resulting maximum bound.
            NetherErosionProjection policy = _erosionPolicy.ProjectBattle(
                input.CurrentErosion,
                baseDelta,
                input.ErosionModifiers!,
                input.SoftErosionLimit,
                isMandatoryBoss: true
            );
            if (policy.PauseReason is NetherPauseReason.UnknownEffect or NetherPauseReason.InvalidConfiguration)
            {
                pauseReason = policy.PauseReason;
                detail = policy.Detail;
                return false;
            }
            projected = policy.ProjectedErosion;
            return true;
        }
        catch (OverflowException)
        {
            pauseReason = NetherPauseReason.UnknownEffect;
            detail = "erosion-projection-overflow";
            return false;
        }
    }

    private static bool HasValidLimits(NetherFloorSafetyInput input) =>
        input.HardErosionLimit > 0
        && input.SoftErosionLimit > 0
        && input.SoftErosionLimit < input.HardErosionLimit
        && input.MinimumHpPermille is >= 0 and <= 1000;

    private static bool HasKnownHp(IReadOnlyList<int>? currentHpPermille, int minimumHpPermille)
    {
        if (currentHpPermille == null || currentHpPermille.Count == 0)
            return false;
        foreach (int hpPermille in currentHpPermille)
        {
            if (hpPermille is < 0 or > 1000)
                return false;
        }
        return minimumHpPermille is >= 0 and <= 1000;
    }

    private static bool HasHpBelowMinimum(IReadOnlyList<int> currentHpPermille, int minimumHpPermille)
    {
        foreach (int hpPermille in currentHpPermille)
        {
            if (hpPermille < minimumHpPermille)
                return true;
        }
        return false;
    }

    private static bool IsCombat(NetherFloorNodeType nodeType) =>
        nodeType is NetherFloorNodeType.Battle or NetherFloorNodeType.MiniBoss or NetherFloorNodeType.Boss;

    private static bool IsKnownKind(NetherFloorSafetyKind kind) =>
        kind is NetherFloorSafetyKind.Optional or NetherFloorSafetyKind.NecessaryTerminal;

    private static NetherFloorSafetyEvaluation Unknown(string detail) =>
        Pause(NetherPauseReason.UnknownMasterData, detail);

    private static NetherFloorSafetyEvaluation Pause(
        NetherPauseReason reason,
        string detail,
        int? projectedMinimum = null,
        int? projectedMaximum = null
    ) => new(
        IsSafe: false,
        ProjectedMinimumErosion: projectedMinimum,
        ProjectedMaximumErosion: projectedMaximum,
        PauseReason: reason,
        Detail: detail
    );
}
