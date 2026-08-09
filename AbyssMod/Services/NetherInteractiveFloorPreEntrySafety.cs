#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Exact non-localized fields copied from one <c>MNetherFloorEvents</c> row.  The four part
/// fields are option references, not effect targets. The selected row is resolved with the same
/// ExtendId/id-or-first-floor rule as the game's NetherFloorMasterResolver.
/// </summary>
internal readonly record struct NetherFloorEventMasterRow(
    long EventId,
    long MapFloorMasterId,
    int Weight,
    long PartId1,
    long PartId2,
    long PartId3,
    long PartId4
)
{
    public bool HasRequiredFields { get; init; } = true;
    /// <summary>Raw <c>MNetherFloorEvents.type</c>, retained without guessing a localized semantic.</summary>
    public int Type { get; init; }
}

/// <summary>
/// Exact non-localized fields copied from one <c>MNetherFloorEventParts</c> row.  It deliberately
/// excludes select/effect text: locale text is not authoritative safety data.
/// </summary>
internal readonly record struct NetherFloorEventPartMasterRow(
    long PartId,
    int TargetType1,
    long SelectParameter1,
    int TargetType2,
    long SelectParameter2,
    int TargetType3,
    long SelectParameter3,
    int ContentType,
    long ContentId,
    long Amount
)
{
    public bool HasRequiredFields { get; init; } = true;
}

/// <summary>
/// Complete authoritative input for pre-entry proof of an interactive floor.  Nullable resource
/// values mean the runtime failed to read them; they are never substituted with zero.
/// </summary>
internal sealed record NetherInteractiveFloorPreEntrySafetyInput(
    NetherFloorNodeType FloorKind,
    long FloorMasterId,
    IReadOnlyList<NetherFloorMasterBoundsRow>? MapFloorRows,
    IReadOnlyList<NetherFloorEventMasterRow>? EventRows,
    IReadOnlyList<NetherFloorEventPartMasterRow>? EventPartRows,
    int? CurrentErosion,
    IReadOnlyList<int>? ActiveHpPermille,
    int? CurrentNetherGold,
    int? CurrentTreasureKeys,
    NetherAutoClimbSettings? Settings
)
{
    /// <summary>True only after a real native shop close callback has been bound.</summary>
    public bool CanCloseShop { get; init; }
    /// <summary>
    /// Exact live <c>NetherFloorModel.ExtendId</c>.  A positive value must identify the native
    /// resolver's event row; zero means the resolver's floor-master fallback is in effect.
    /// </summary>
    public long FloorExtendId { get; init; }
    /// <summary>Current authoritative portfolio used to prove that target_type=7 has a removable code.</summary>
    public IReadOnlyList<NetherCodeState> CurrentCodes { get; init; } = Array.Empty<NetherCodeState>();
    public int CodeCapacity { get; init; }
}

/// <summary>
/// The safe option proof is retained under the exact native-resolved event ID so the later popup
/// dispatcher cannot mistake an option from another master row for the selected floor.
/// </summary>
internal sealed record NetherInteractiveOptionProjection(
    int OptionNumber,
    int ErosionDelta,
    int HpDelta,
    IReadOnlyList<NetherEffect> ExpectedEffects
);

/// <summary>
/// Projection of the exact event row already represented by the server floor model. The route
/// planner consumes its erosion and HP outcome before clicking the floor.
/// </summary>
internal readonly record struct NetherInteractiveWorstCaseProjection(int ErosionDelta, int HpDelta);

internal sealed record NetherInteractiveFloorPreEntrySafetyResult
{
    public bool IsSafe { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyDictionary<long, int> SafeOptionNumberByEventId { get; init; } =
        new Dictionary<long, int>();
    public IReadOnlyDictionary<long, NetherInteractiveOptionProjection> SafeOptionProjectionByEventId { get; init; } =
        new Dictionary<long, NetherInteractiveOptionProjection>();
    public NetherInteractiveWorstCaseProjection? WorstCaseProjection { get; init; }

    public static NetherInteractiveFloorPreEntrySafetyResult Safe(
        IReadOnlyDictionary<long, int>? safeOptions = null,
        IReadOnlyDictionary<long, NetherInteractiveOptionProjection>? projections = null,
        NetherInteractiveWorstCaseProjection? worstCase = null
    ) => new()
    {
        IsSafe = true,
        PauseReason = NetherPauseReason.None,
        SafeOptionNumberByEventId = safeOptions ?? new Dictionary<long, int>(),
        SafeOptionProjectionByEventId = projections ?? new Dictionary<long, NetherInteractiveOptionProjection>(),
        WorstCaseProjection = worstCase,
    };

    public static NetherInteractiveFloorPreEntrySafetyResult SafeNeutral() => new()
    {
        IsSafe = true,
        PauseReason = NetherPauseReason.None,
        WorstCaseProjection = new NetherInteractiveWorstCaseProjection(ErosionDelta: 0, HpDelta: 0),
    };

    public static NetherInteractiveFloorPreEntrySafetyResult Pause(NetherPauseReason reason, string detail) => new()
    {
        IsSafe = false,
        PauseReason = reason,
        Detail = detail,
    };
}

/// <summary>
/// Fail-closed proof that an interactive floor's native-resolved event row has a safe exit. It is
/// intentionally a pure production component: the
/// bridge can later copy exact master fields into these rows without exposing a reflection or UI
/// object to route policy.
/// </summary>
internal sealed class NetherInteractiveFloorPreEntrySafety
{
    private readonly NetherFloorMasterBoundsMapper _boundsMapper = new();
    private readonly NetherEventPolicy _eventPolicy = new();

    public NetherInteractiveFloorPreEntrySafetyResult Evaluate(NetherInteractiveFloorPreEntrySafetyInput? input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (!TryCreateSnapshot(input, out NetherSnapshot? snapshot, out NetherInteractiveFloorPreEntrySafetyResult? invalid))
            return invalid!;
        if (!TryValidateFloorMaster(input, out NetherInteractiveFloorPreEntrySafetyResult? boundsFailure))
            return boundsFailure!;

        return input.FloorKind switch
        {
            NetherFloorNodeType.Event => EvaluatePossibleEventRows(input, snapshot!, isRecovery: false),
            NetherFloorNodeType.Recovery => EvaluatePossibleEventRows(input, snapshot!, isRecovery: true),
            NetherFloorNodeType.Shop => EvaluateShopOff(input),
            NetherFloorNodeType.Treasure => EvaluateTreasureKeyOnly(input, snapshot!),
            _ => NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.UnknownFloor,
                "unsupported-interactive-floor-kind:" + ((int)input.FloorKind).ToString(CultureInfo.InvariantCulture)
            ),
        };
    }

    private static bool TryCreateSnapshot(
        NetherInteractiveFloorPreEntrySafetyInput input,
        out NetherSnapshot? snapshot,
        out NetherInteractiveFloorPreEntrySafetyResult? failure
    )
    {
        snapshot = null;
        failure = null;
        if (input.Settings == null)
        {
            failure = Unknown("missing-interactive-safety-settings");
            return false;
        }
        if (!input.CurrentErosion.HasValue
            || !input.CurrentNetherGold.HasValue
            || !input.CurrentTreasureKeys.HasValue
            || input.ActiveHpPermille == null)
        {
            failure = Unknown("missing-interactive-authoritative-resource");
            return false;
        }
        if (input.CurrentErosion.Value < 0
            || input.CurrentNetherGold.Value < 0
            || input.CurrentTreasureKeys.Value < 0
            || input.ActiveHpPermille.Count == 0)
        {
            failure = Unknown("invalid-interactive-authoritative-resource");
            return false;
        }

        var characters = new List<NetherCharacterState>(input.ActiveHpPermille.Count);
        for (int index = 0; index < input.ActiveHpPermille.Count; index++)
        {
            int hp = input.ActiveHpPermille[index];
            if (hp is < 0 or > 1000)
            {
                failure = Unknown("invalid-interactive-active-hp");
                return false;
            }
            characters.Add(new NetherCharacterState(index + 1L, hp));
        }

        snapshot = new NetherSnapshot
        {
            ErosionPoint = input.CurrentErosion.Value,
            NetherGold = input.CurrentNetherGold.Value,
            TreasureKeyCount = input.CurrentTreasureKeys.Value,
            Characters = characters,
            Codes = input.CurrentCodes ?? Array.Empty<NetherCodeState>(),
            CodeCapacity = input.CodeCapacity,
        };
        return true;
    }

    private bool TryValidateFloorMaster(
        NetherInteractiveFloorPreEntrySafetyInput input,
        out NetherInteractiveFloorPreEntrySafetyResult? failure
    )
    {
        failure = null;
        NetherFloorMasterBounds bounds = _boundsMapper.Map(input.FloorMasterId, input.MapFloorRows);
        if (!bounds.IsKnown || !bounds.MinimumErosionPoint.HasValue || !bounds.MaximumErosionPoint.HasValue)
        {
            failure = Unknown("interactive-floor-bounds:" + bounds.Detail);
            return false;
        }

        // min/max_erosion_point belongs to MNetherMapFloors row generation eligibility.
        // The server has already materialized this exact floor; treating the range as an
        // action delta double-counts up to 100 erosion. Exact event effects are evaluated
        // below, while neutral Shop/Treasure exits remain zero-cost.
        return true;
    }

    private NetherInteractiveFloorPreEntrySafetyResult EvaluatePossibleEventRows(
        NetherInteractiveFloorPreEntrySafetyInput input,
        NetherSnapshot snapshot,
        bool isRecovery
    )
    {
        if (!TryIndexEventMasters(
                input.EventRows,
                input.FloorMasterId,
                input.FloorExtendId,
                out IReadOnlyList<NetherFloorEventMasterRow>? resolvedRows,
                out string eventError
            ))
        {
            return Unknown(eventError);
        }
        if (!TryIndexEventParts(input.EventPartRows, out IReadOnlyDictionary<long, NetherFloorEventPartMasterRow>? parts, out string partError))
            return Unknown(partError);

        var safeOptions = new Dictionary<long, int>();
        var projections = new Dictionary<long, NetherInteractiveOptionProjection>();
        int worstErosion = int.MinValue;
        int worstHp = int.MaxValue;
        foreach (NetherFloorEventMasterRow row in resolvedRows!)
        {
            if (!TryBuildOptions(row, parts!, out IReadOnlyList<NetherEventOption>? options, out string optionError))
                return Unknown("event-row-" + row.EventId.ToString(CultureInfo.InvariantCulture) + ":" + optionError);
            if (!TrySelectSafeOption(
                    snapshot,
                    options!,
                    input.Settings!,
                    isRecovery,
                    out int optionNumber,
                    out NetherInteractiveOptionProjection projection,
                    out NetherPauseReason rejection,
                    out string rejectionDetail
                ))
            {
                return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                    rejection,
                    "event-row-" + row.EventId.ToString(CultureInfo.InvariantCulture) + ":" + rejectionDetail
                );
            }
            safeOptions.Add(row.EventId, optionNumber);
            projections.Add(row.EventId, projection);
            worstErosion = Math.Max(worstErosion, projection.ErosionDelta);
            worstHp = Math.Min(worstHp, projection.HpDelta);
        }

        if (projections.Count != safeOptions.Count || worstErosion == int.MinValue || worstHp == int.MaxValue)
            return Unknown("missing-event-option-projection");

        return NetherInteractiveFloorPreEntrySafetyResult.Safe(
            safeOptions,
            projections,
            new NetherInteractiveWorstCaseProjection(worstErosion, worstHp)
        );
    }

    private static bool TryIndexEventMasters(
        IReadOnlyList<NetherFloorEventMasterRow>? rows,
        long floorMasterId,
        long floorExtendId,
        out IReadOnlyList<NetherFloorEventMasterRow>? possibleRows,
        out string error
    )
    {
        possibleRows = null;
        if (floorMasterId <= 0 || floorExtendId < 0)
        {
            error = "invalid-interactive-floor-master-id";
            return false;
        }
        if (rows == null)
        {
            error = "missing-m-nether-floor-events";
            return false;
        }

        NetherFloorEventMasterRow? resolved = null;
        foreach (NetherFloorEventMasterRow row in rows)
        {
            bool matches = floorExtendId > 0
                ? row.EventId == floorExtendId
                : row.MapFloorMasterId == floorMasterId;
            if (matches)
            {
                resolved = row;
                break;
            }
        }
        if (!resolved.HasValue)
        {
            error = floorExtendId > 0
                ? "missing-extend-m-nether-floor-event:" + floorExtendId.ToString(CultureInfo.InvariantCulture)
                : "missing-floor-m-nether-floor-event:" + floorMasterId.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        NetherFloorEventMasterRow selected = resolved.Value;
        if (!selected.HasRequiredFields
            || selected.EventId <= 0
            || selected.MapFloorMasterId <= 0
            || selected.Weight < 0
            || selected.Type < 0)
        {
            error = "invalid-resolved-m-nether-floor-event:" + selected.EventId.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        // Native NetherFloorMasterResolver uses First(id == ExtendId) when ExtendId is
        // positive, otherwise First(m_nether_map_floor_id == floorMasterId). Weight and the
        // selected row's map-floor field are generation metadata, not extra resolver gates.
        possibleRows = new[] { selected };
        error = string.Empty;
        return true;
    }

    private static bool TryIndexEventParts(
        IReadOnlyList<NetherFloorEventPartMasterRow>? rows,
        out IReadOnlyDictionary<long, NetherFloorEventPartMasterRow>? indexed,
        out string error
    )
    {
        indexed = null;
        if (rows == null)
        {
            error = "missing-m-nether-floor-event-parts";
            return false;
        }

        var parts = new Dictionary<long, NetherFloorEventPartMasterRow>();
        foreach (NetherFloorEventPartMasterRow row in rows)
        {
            if (!row.HasRequiredFields || row.PartId <= 0)
            {
                error = "invalid-m-nether-floor-event-part";
                return false;
            }
            if (!parts.TryAdd(row.PartId, row))
            {
                error = "duplicate-m-nether-floor-event-part:" + row.PartId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
        }

        indexed = parts;
        error = string.Empty;
        return true;
    }

    private static bool TryBuildOptions(
        NetherFloorEventMasterRow row,
        IReadOnlyDictionary<long, NetherFloorEventPartMasterRow> parts,
        out IReadOnlyList<NetherEventOption>? options,
        out string error
    )
    {
        options = null;
        long[] ids = [row.PartId1, row.PartId2, row.PartId3, row.PartId4];
        bool foundEmptyPart = false;
        var seen = new HashSet<long>();
        var mapped = new List<NetherEventOption>();
        for (int index = 0; index < ids.Length; index++)
        {
            long id = ids[index];
            if (id < 0)
            {
                error = "invalid-event-part-reference";
                return false;
            }
            if (id == 0)
            {
                foundEmptyPart = true;
                continue;
            }
            if (foundEmptyPart)
            {
                error = "noncontiguous-event-part-reference";
                return false;
            }
            if (!seen.Add(id))
            {
                error = "duplicate-event-part-reference:" + id.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!parts.TryGetValue(id, out NetherFloorEventPartMasterRow part))
            {
                error = "missing-m-nether-floor-event-part:" + id.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (!TryMapPart(part, out IReadOnlyList<NetherEffect>? effects, out string partError))
            {
                error = "event-part-" + id.ToString(CultureInfo.InvariantCulture) + ":" + partError;
                return false;
            }
            mapped.Add(new NetherEventOption(index + 1, effects!));
        }
        if (mapped.Count == 0)
        {
            error = "empty-event-part-references";
            return false;
        }

        options = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryMapPart(
        NetherFloorEventPartMasterRow part,
        out IReadOnlyList<NetherEffect>? effects,
        out string error
    )
    {
        effects = null;
        var mapped = new List<NetherEffect>();
        if (!TryMapTarget(part.TargetType1, part.SelectParameter1, mapped, out error)
            || !TryMapTarget(part.TargetType2, part.SelectParameter2, mapped, out error)
            || !TryMapTarget(part.TargetType3, part.SelectParameter3, mapped, out error))
        {
            return false;
        }

        if (part.ContentType != 0)
        {
            if (part.Amount is < 0 or > int.MaxValue)
            {
                error = "invalid-event-content";
                return false;
            }
            NetherEffect? content = part.ContentType switch
            {
                30 or 31 when part.ContentId > 0 => new NetherEffect(NetherEffectKind.Item, checked((int)part.Amount))
                {
                    ContentId = part.ContentId,
                },
                160 when part.ContentId == 0 => new NetherEffect(NetherEffectKind.AbyssCodeOffer, checked((int)part.Amount)),
                165 => new NetherEffect(NetherEffectKind.NetherGoldGain, checked((int)part.Amount))
                {
                    ContentId = part.ContentId,
                },
                166 => new NetherEffect(NetherEffectKind.TreasureKeyGain, checked((int)part.Amount))
                {
                    ContentId = part.ContentId,
                },
                _ => null,
            };
            if (content == null)
            {
                error = "unsupported-event-content-type:" + part.ContentType.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            mapped.Add(content);
        }

        if (mapped.Count is < 1 or > 4)
        {
            error = "invalid-event-effect-count:" + mapped.Count.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        effects = mapped;
        error = string.Empty;
        return true;
    }

    private static bool TryMapTarget(
        int rawType,
        long parameter,
        ICollection<NetherEffect> effects,
        out string error
    )
    {
        error = string.Empty;
        if (rawType == 0)
            return true;
        if (parameter < 0 || parameter > int.MaxValue || rawType is < 1 or > 8)
        {
            error = "unsupported-event-target-type-or-parameter:" + rawType.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        NetherEffectKind kind = (NetherEffectKind)rawType;
        if (kind == NetherEffectKind.AbyssCodeTransform)
        {
            // Native CreateModelByEventStarted treats target_type=7 as a boolean flow flag.
            // select_parameter is not the replacement/new code ID (zero is a valid master value).
            effects.Add(new NetherEffect(kind, 0));
            return true;
        }
        if (kind == NetherEffectKind.Battle)
        {
            effects.Add(new NetherEffect(kind, checked((int)parameter)) { IsOptionalBattle = true });
            return true;
        }

        effects.Add(new NetherEffect(kind, checked((int)parameter)));
        return true;
    }

    private bool TrySelectSafeOption(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings,
        bool isRecovery,
        out int selectedOptionNumber,
        out NetherInteractiveOptionProjection selectedProjection,
        out NetherPauseReason rejection,
        out string detail
    )
    {
        selectedOptionNumber = 0;
        selectedProjection = default!;
        rejection = NetherPauseReason.NoSafeRoute;
        detail = "no-safe-event-option";
        var safeOptions = new List<NetherEventOption>();
        foreach (NetherEventOption option in options)
        {
            NetherEventDecision decision = isRecovery
                ? _eventPolicy.DecideRecovery(snapshot, [option], settings)
                : _eventPolicy.DecideEvent(snapshot, [option], settings);
            if (decision.Kind != NetherEventDecisionKind.Select)
            {
                CaptureMoreSpecificRejection(decision, ref rejection, ref detail);
                continue;
            }
            if (decision.StartsBattleAfterSelection)
            {
                // A floor selected as interactive cannot prove a later battle's route/lease
                // safety.  It is not an exit unless a non-battle option from this same row exists.
                continue;
            }
            if (!HasSafeHpFloor(snapshot, decision.HpDelta, settings.MinimumCharacterHpPermille))
            {
                rejection = NetherPauseReason.UnsafeHp;
                detail = "event-option-hp-below-minimum";
                continue;
            }
            safeOptions.Add(option);
        }

        if (safeOptions.Count == 0)
            return false;

        NetherEventDecision selected = isRecovery
            ? _eventPolicy.DecideRecovery(snapshot, safeOptions, settings)
            : _eventPolicy.DecideEvent(snapshot, safeOptions, settings);
        if (selected.Kind != NetherEventDecisionKind.Select || selected.StartsBattleAfterSelection)
        {
            rejection = selected.Kind == NetherEventDecisionKind.Pause ? selected.PauseReason : NetherPauseReason.NoSafeRoute;
            detail = selected.Detail.Length == 0 ? "safe-option-selection-unavailable" : selected.Detail;
            return false;
        }
        selectedOptionNumber = selected.OptionNumber;
        try
        {
            selectedProjection = new NetherInteractiveOptionProjection(
                selected.OptionNumber,
                checked(selected.ProjectedErosion - snapshot.ErosionPoint),
                selected.HpDelta,
                selected.ExpectedEffects.ToArray()
            );
        }
        catch (OverflowException)
        {
            rejection = NetherPauseReason.UnknownEffect;
            detail = "event-option-projection-overflow";
            return false;
        }
        return true;
    }

    private static bool HasSafeHpFloor(NetherSnapshot snapshot, int hpDelta, int minimumHpPermille)
    {
        if (hpDelta >= 0)
            return true;
        try
        {
            foreach (NetherCharacterState character in snapshot.Characters)
            {
                if (character.IsActive && checked(character.HpPermille + hpDelta) < minimumHpPermille)
                    return false;
            }
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void CaptureMoreSpecificRejection(
        NetherEventDecision decision,
        ref NetherPauseReason rejection,
        ref string detail
    )
    {
        if (rejection != NetherPauseReason.NoSafeRoute)
            return;
        if (decision.PauseReason == NetherPauseReason.NoSafeRoute)
        {
            if (!string.IsNullOrEmpty(decision.Detail))
                detail = decision.Detail;
            return;
        }
        rejection = decision.PauseReason;
        detail = decision.Detail;
    }

    private static NetherInteractiveFloorPreEntrySafetyResult EvaluateShopOff(
        NetherInteractiveFloorPreEntrySafetyInput input
    )
    {
        if (!input.CanCloseShop)
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.BindingUnavailable,
                "interactive-shop-close-binding-unavailable"
            );
        }
        // ShopOff is the default and uses this proved close exit.  EquipmentBags may also
        // enter only because the same exact close exists; the later popup policy still has to
        // prove a particular purchase's content, amount and Gold cost before it mutates.  Do
        // not reject an otherwise safe route solely because the user enabled an optional buy.
        if (input.Settings!.ShopMode is not (NetherShopMode.Off or NetherShopMode.EquipmentBags))
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.InvalidConfiguration,
                "interactive-shop-mode-invalid"
            );
        }
        return NetherInteractiveFloorPreEntrySafetyResult.SafeNeutral();
    }

    private static NetherInteractiveFloorPreEntrySafetyResult EvaluateTreasureKeyOnly(
        NetherInteractiveFloorPreEntrySafetyInput input,
        NetherSnapshot snapshot
    )
    {
        if (input.Settings!.TreasureMode != NetherTreasureMode.KeyOnly)
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.NoSafeRoute,
                "interactive-treasure-mode-not-key-only"
            );
        }
        if (snapshot.TreasureKeyCount < 1)
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.NoSafeRoute,
                "interactive-treasure-key-unavailable"
            );
        }
        return NetherInteractiveFloorPreEntrySafetyResult.SafeNeutral();
    }

    private static NetherInteractiveFloorPreEntrySafetyResult Unknown(string detail) =>
        NetherInteractiveFloorPreEntrySafetyResult.Pause(NetherPauseReason.UnknownMasterData, detail);
}

internal static class NetherInteractiveFloorPreEntrySafetyCharacterExtensions
{
    public static IReadOnlyList<int> SelectHpPermille(this IReadOnlyList<NetherCharacterState> characters)
    {
        var values = new int[characters.Count];
        for (int index = 0; index < characters.Count; index++)
            values[index] = characters[index].HpPermille;
        return values;
    }
}
