#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace AbyssMod.Services;

/// <summary>
/// Exact non-localized fields copied from one <c>MNetherFloorEvents</c> row.  The four part
/// fields are option references, not effect targets; a positive-weight row is a server-possible
/// outcome and must therefore be safe before selecting the floor.
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
}

/// <summary>
/// The safe option proof is retained per possible event row so the later popup dispatcher cannot
/// mistake a safe option from one weighted row for a safe exit in another row.
/// </summary>
internal sealed record NetherInteractiveFloorPreEntrySafetyResult
{
    public bool IsSafe { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyDictionary<long, int> SafeOptionNumberByEventId { get; init; } =
        new Dictionary<long, int>();

    public static NetherInteractiveFloorPreEntrySafetyResult Safe(
        IReadOnlyDictionary<long, int>? safeOptions = null
    ) => new()
    {
        IsSafe = true,
        PauseReason = NetherPauseReason.None,
        SafeOptionNumberByEventId = safeOptions ?? new Dictionary<long, int>(),
    };

    public static NetherInteractiveFloorPreEntrySafetyResult Pause(NetherPauseReason reason, string detail) => new()
    {
        IsSafe = false,
        PauseReason = reason,
        Detail = detail,
    };
}

/// <summary>
/// Fail-closed proof that an interactive floor has at least one safe native exit for every
/// server-possible weighted event row.  It is intentionally a pure production component: the
/// bridge can later copy exact master fields into these rows without exposing a reflection or UI
/// object to route policy.
/// </summary>
internal sealed class NetherInteractiveFloorPreEntrySafety
{
    private const int HardErosionLimit = 100;
    private readonly NetherFloorMasterBoundsMapper _boundsMapper = new();
    private readonly NetherFloorSafetyEvaluator _floorSafetyEvaluator = new();
    private readonly NetherEventPolicy _eventPolicy = new();

    public NetherInteractiveFloorPreEntrySafetyResult Evaluate(NetherInteractiveFloorPreEntrySafetyInput? input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));
        if (!TryCreateSnapshot(input, out NetherSnapshot? snapshot, out NetherInteractiveFloorPreEntrySafetyResult? invalid))
            return invalid!;
        if (!TryEvaluateFloorBounds(input, snapshot!, out NetherInteractiveFloorPreEntrySafetyResult? boundsFailure))
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
        };
        return true;
    }

    private bool TryEvaluateFloorBounds(
        NetherInteractiveFloorPreEntrySafetyInput input,
        NetherSnapshot snapshot,
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

        NetherFloorSafetyEvaluation evaluation = _floorSafetyEvaluator.Evaluate(new NetherFloorSafetyInput(
            CurrentErosion: snapshot.ErosionPoint,
            FloorMinimumErosion: bounds.MinimumErosionPoint.Value,
            FloorMaximumErosion: bounds.MaximumErosionPoint.Value,
            KnownModifierDelta: 0,
            Kind: NetherFloorSafetyKind.Optional,
            NodeType: input.FloorKind,
            CurrentHpPermille: snapshot.Characters.SelectHpPermille(),
            MinimumHpPermille: input.Settings!.MinimumCharacterHpPermille,
            SoftErosionLimit: input.Settings.SoftErosionLimit,
            HardErosionLimit: HardErosionLimit,
            AllInputsKnown: true
        )
        {
            ErosionModifiers = Array.Empty<NetherErosionModifier>(),
        });
        if (evaluation.IsSafe)
            return true;

        failure = NetherInteractiveFloorPreEntrySafetyResult.Pause(
            evaluation.PauseReason,
            "interactive-floor-bounds-unsafe:" + evaluation.Detail
        );
        return false;
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
                out IReadOnlyList<NetherFloorEventMasterRow>? possibleRows,
                out string eventError
            ))
        {
            return Unknown(eventError);
        }
        if (!TryIndexEventParts(input.EventPartRows, out IReadOnlyDictionary<long, NetherFloorEventPartMasterRow>? parts, out string partError))
            return Unknown(partError);

        var safeOptions = new Dictionary<long, int>();
        foreach (NetherFloorEventMasterRow row in possibleRows!)
        {
            if (!TryBuildOptions(row, parts!, out IReadOnlyList<NetherEventOption>? options, out string optionError))
                return Unknown("event-row-" + row.EventId.ToString(CultureInfo.InvariantCulture) + ":" + optionError);
            if (!TrySelectSafeOption(
                    snapshot,
                    options!,
                    input.Settings!,
                    isRecovery,
                    out int optionNumber,
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
        }

        return NetherInteractiveFloorPreEntrySafetyResult.Safe(safeOptions);
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

        var seen = new HashSet<long>();
        var possible = new List<NetherFloorEventMasterRow>();
        NetherFloorEventMasterRow? exactExtendRow = null;
        foreach (NetherFloorEventMasterRow row in rows)
        {
            if (!row.HasRequiredFields || row.EventId <= 0 || row.MapFloorMasterId <= 0 || row.Weight < 0 || row.Type < 0)
            {
                error = "invalid-m-nether-floor-event";
                return false;
            }
            if (!seen.Add(row.EventId))
            {
                error = "duplicate-m-nether-floor-event:" + row.EventId.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            if (floorExtendId > 0 && row.EventId == floorExtendId)
                exactExtendRow = row;
            if (row.MapFloorMasterId == floorMasterId && row.Weight > 0)
                possible.Add(row);
        }
        if (floorExtendId > 0
            && (!exactExtendRow.HasValue
                || exactExtendRow.Value.MapFloorMasterId != floorMasterId
                || exactExtendRow.Value.Weight <= 0))
        {
            error = "missing-or-invalid-extend-m-nether-floor-event:" + floorExtendId.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        if (possible.Count == 0)
        {
            error = "missing-positive-m-nether-floor-event:" + floorMasterId.ToString(CultureInfo.InvariantCulture);
            return false;
        }

        possibleRows = possible;
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
            if (part.ContentId <= 0 || part.Amount is < 0 or > int.MaxValue)
            {
                error = "invalid-event-content";
                return false;
            }
            NetherEffect? content = part.ContentType switch
            {
                30 or 31 => new NetherEffect(NetherEffectKind.Item, checked((int)part.Amount))
                {
                    ContentId = part.ContentId,
                },
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

        if (mapped.Count is < 1 or > 3)
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
        if (kind == NetherEffectKind.AbyssCodeChanged)
        {
            if (parameter <= 0)
            {
                error = "missing-event-replacement-code";
                return false;
            }
            effects.Add(new NetherEffect(kind, 0) { ReplacementCodeId = parameter });
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
        out NetherPauseReason rejection,
        out string detail
    )
    {
        selectedOptionNumber = 0;
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
            return;
        rejection = decision.PauseReason;
        detail = decision.Detail;
    }

    private static NetherInteractiveFloorPreEntrySafetyResult EvaluateShopOff(
        NetherInteractiveFloorPreEntrySafetyInput input
    )
    {
        if (input.Settings!.ShopMode != NetherShopMode.Off)
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.NoSafeRoute,
                "interactive-shop-mode-not-off"
            );
        }
        if (!input.CanCloseShop)
        {
            return NetherInteractiveFloorPreEntrySafetyResult.Pause(
                NetherPauseReason.BindingUnavailable,
                "interactive-shop-close-binding-unavailable"
            );
        }
        return NetherInteractiveFloorPreEntrySafetyResult.Safe();
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
        return NetherInteractiveFloorPreEntrySafetyResult.Safe();
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
