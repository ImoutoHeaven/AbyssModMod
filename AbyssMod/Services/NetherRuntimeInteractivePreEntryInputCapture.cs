#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace AbyssMod.Services;

/// <summary>
/// Reflection-facing request used by <see cref="NetherRuntimeBridge"/> to copy only exact,
/// non-localized Nether master/runtime fields into the pure interactive pre-entry evaluator.
/// The shape is deliberately object-based so its field-name contract can be characterized with
/// fixtures without making policy code depend on IL2CPP types.
/// </summary>
internal sealed record NetherRuntimeInteractivePreEntryCaptureRequest(
    object? FloorModel,
    IEnumerable? MapFloorRows,
    IEnumerable? EventRows,
    IEnumerable? EventPartRows,
    int? CurrentErosion,
    IReadOnlyList<int>? ActiveHpPermille,
    int? CurrentNetherGold,
    int? CurrentTreasureKeys,
    NetherAutoClimbSettings? Settings,
    bool CanCloseShop
);

/// <summary>
/// Captured source input and the immediate fail-closed pre-entry decision.  <c>IsCaptured</c>
/// proves only that exact field names could be read; a malformed/missing master row is still
/// represented by an unsafe decision rather than silently removed from the route.
/// </summary>
internal sealed record NetherRuntimeInteractivePreEntryCaptureResult
{
    public bool IsCaptured { get; init; }
    public NetherInteractiveFloorPreEntrySafetyInput? Input { get; init; }
    public NetherInteractiveFloorPreEntrySafetyResult Safety { get; init; } =
        NetherInteractiveFloorPreEntrySafetyResult.Pause(NetherPauseReason.UnknownMasterData, "uninitialized-interactive-input-capture");
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Per-runtime-generation capture returned by <c>NetherRuntimeBridge.TryCaptureInteractivePreEntryInputs</c>.
/// Any missing floor/model relation invalidates the whole set; callers must not consume a partial
/// dictionary as though omitted interactive floors were safe.
/// </summary>
internal sealed record NetherRuntimeInteractivePreEntryInputsResult
{
    public bool IsSuccess { get; init; }
    public IReadOnlyDictionary<long, NetherRuntimeInteractivePreEntryCaptureResult> ByFloorMasterId { get; init; } =
        new Dictionary<long, NetherRuntimeInteractivePreEntryCaptureResult>();
    public string Detail { get; init; } = string.Empty;

    public static NetherRuntimeInteractivePreEntryInputsResult Success(
        IReadOnlyDictionary<long, NetherRuntimeInteractivePreEntryCaptureResult> entries
    ) => new()
    {
        IsSuccess = true,
        ByFloorMasterId = entries,
    };

    public static NetherRuntimeInteractivePreEntryInputsResult Failure(string detail) => new()
    {
        IsSuccess = false,
        Detail = detail,
    };
}

/// <summary>
/// Strict copier for the runtime objects used by FloorSelection.  It reads the same raw numeric
/// names as the packaged models: <c>MNetherMapFloorId</c>/<c>ExtendId</c>, MNetherMapFloors,
/// MNetherFloorEvents, and MNetherFloorEventParts.  Duplicate and reference proof belongs to
/// <see cref="NetherInteractiveFloorPreEntrySafety"/>, which receives the complete copied rows.
/// </summary>
internal sealed class NetherRuntimeInteractivePreEntryInputCapture
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly NetherInteractiveFloorPreEntrySafety _safety = new();

    public NetherRuntimeInteractivePreEntryCaptureResult Capture(NetherRuntimeInteractivePreEntryCaptureRequest? request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (request.FloorModel == null
            || !TryReadInt64(request.FloorModel, "MNetherMapFloorId", out long floorMasterId)
            || !TryReadInt64(request.FloorModel, "ExtendId", out long extendId)
            || !TryReadInt32(request.FloorModel, "FloorType", out int rawFloorType))
        {
            return Failure("missing-runtime-floor-m-nether-map-floor-id-extend-id-or-type");
        }
        if (floorMasterId <= 0 || extendId < 0 || !TryMapInteractiveFloorKind(rawFloorType, out NetherFloorNodeType floorKind))
            return Failure("invalid-or-unsupported-runtime-interactive-floor");

        if (!TryMapFloorRows(request.MapFloorRows, out IReadOnlyList<NetherFloorMasterBoundsRow>? mapRows, out string mapError))
            return Failure(mapError);
        if (!TryMapEventRows(request.EventRows, out IReadOnlyList<NetherFloorEventMasterRow>? eventRows, out string eventError))
            return Failure(eventError);
        if (!TryMapEventPartRows(request.EventPartRows, out IReadOnlyList<NetherFloorEventPartMasterRow>? eventPartRows, out string partError))
            return Failure(partError);

        var input = new NetherInteractiveFloorPreEntrySafetyInput(
            FloorKind: floorKind,
            FloorMasterId: floorMasterId,
            MapFloorRows: mapRows,
            EventRows: eventRows,
            EventPartRows: eventPartRows,
            CurrentErosion: request.CurrentErosion,
            ActiveHpPermille: request.ActiveHpPermille,
            CurrentNetherGold: request.CurrentNetherGold,
            CurrentTreasureKeys: request.CurrentTreasureKeys,
            Settings: request.Settings
        )
        {
            FloorExtendId = extendId,
            CanCloseShop = request.CanCloseShop,
        };
        return new NetherRuntimeInteractivePreEntryCaptureResult
        {
            IsCaptured = true,
            Input = input,
            Safety = _safety.Evaluate(input),
        };
    }

    private static bool TryMapFloorRows(
        IEnumerable? source,
        out IReadOnlyList<NetherFloorMasterBoundsRow>? rows,
        out string error
    )
    {
        rows = null;
        error = string.Empty;
        if (source == null)
            return true;

        var mapped = new List<NetherFloorMasterBoundsRow>();
        foreach (object? raw in source)
        {
            if (raw == null)
            {
                mapped.Add(new NetherFloorMasterBoundsRow(0, 0, 0) { HasRequiredFields = false });
                continue;
            }
            if (!TryReadInt64(raw, "id", out long id)
                || !TryReadInt64(raw, "min_erosion_point", out long minimum)
                || !TryReadInt64(raw, "max_erosion_point", out long maximum))
            {
                error = "missing-m-nether-map-floor-raw-field";
                return false;
            }
            mapped.Add(new NetherFloorMasterBoundsRow(id, minimum, maximum));
        }
        rows = mapped;
        return true;
    }

    private static bool TryMapEventRows(
        IEnumerable? source,
        out IReadOnlyList<NetherFloorEventMasterRow>? rows,
        out string error
    )
    {
        rows = null;
        error = string.Empty;
        if (source == null)
            return true;

        var mapped = new List<NetherFloorEventMasterRow>();
        foreach (object? raw in source)
        {
            if (raw == null)
            {
                mapped.Add(new NetherFloorEventMasterRow(0, 0, 0, 0, 0, 0, 0) { HasRequiredFields = false });
                continue;
            }
            if (!TryReadInt64(raw, "id", out long id)
                || !TryReadInt64(raw, "m_nether_map_floor_id", out long mapFloorId)
                || !TryReadInt32(raw, "weight", out int weight)
                || !TryReadInt32(raw, "type", out int type)
                || !TryReadInt64(raw, "m_nether_floor_event_part_id_1", out long part1)
                || !TryReadInt64(raw, "m_nether_floor_event_part_id_2", out long part2)
                || !TryReadInt64(raw, "m_nether_floor_event_part_id_3", out long part3)
                || !TryReadInt64(raw, "m_nether_floor_event_part_id_4", out long part4))
            {
                error = "missing-m-nether-floor-event-raw-field";
                return false;
            }
            mapped.Add(new NetherFloorEventMasterRow(id, mapFloorId, weight, part1, part2, part3, part4)
            {
                Type = type,
            });
        }
        rows = mapped;
        return true;
    }

    private static bool TryMapEventPartRows(
        IEnumerable? source,
        out IReadOnlyList<NetherFloorEventPartMasterRow>? rows,
        out string error
    )
    {
        rows = null;
        error = string.Empty;
        if (source == null)
            return true;

        var mapped = new List<NetherFloorEventPartMasterRow>();
        foreach (object? raw in source)
        {
            if (raw == null)
            {
                mapped.Add(new NetherFloorEventPartMasterRow(0, 0, 0, 0, 0, 0, 0, 0, 0, 0) { HasRequiredFields = false });
                continue;
            }
            if (!TryReadInt64(raw, "id", out long id)
                || !TryReadInt32(raw, "target_type_1", out int targetType1)
                || !TryReadInt64(raw, "select_parameter_1", out long parameter1)
                || !TryReadInt32(raw, "target_type_2", out int targetType2)
                || !TryReadInt64(raw, "select_parameter_2", out long parameter2)
                || !TryReadInt32(raw, "target_type_3", out int targetType3)
                || !TryReadInt64(raw, "select_parameter_3", out long parameter3)
                || !TryReadInt32(raw, "content_type", out int contentType)
                || !TryReadInt64(raw, "content_id", out long contentId)
                || !TryReadInt64(raw, "amount", out long amount))
            {
                error = "missing-m-nether-floor-event-part-raw-field";
                return false;
            }
            mapped.Add(new NetherFloorEventPartMasterRow(
                id,
                targetType1,
                parameter1,
                targetType2,
                parameter2,
                targetType3,
                parameter3,
                contentType,
                contentId,
                amount
            ));
        }
        rows = mapped;
        return true;
    }

    private static bool TryMapInteractiveFloorKind(int rawFloorType, out NetherFloorNodeType kind)
    {
        kind = rawFloorType switch
        {
            (int)NetherFloorNodeType.Event => NetherFloorNodeType.Event,
            (int)NetherFloorNodeType.Recovery => NetherFloorNodeType.Recovery,
            (int)NetherFloorNodeType.Shop => NetherFloorNodeType.Shop,
            (int)NetherFloorNodeType.Treasure => NetherFloorNodeType.Treasure,
            _ => NetherFloorNodeType.Unknown,
        };
        return kind != NetherFloorNodeType.Unknown;
    }

    private static bool TryReadInt32(object instance, string name, out int value)
    {
        value = 0;
        if (!TryReadInt64(instance, name, out long raw) || raw is < int.MinValue or > int.MaxValue)
            return false;
        value = checked((int)raw);
        return true;
    }

    private static bool TryReadInt64(object instance, string name, out long value)
    {
        value = 0;
        Type type = instance.GetType();
        object? raw = type.GetProperty(name, InstanceFlags)?.GetValue(instance)
            ?? type.GetField(name, InstanceFlags)?.GetValue(instance);
        if (raw == null)
            return false;

        switch (raw)
        {
            case sbyte signedByte:
                value = signedByte;
                return true;
            case byte unsignedByte:
                value = unsignedByte;
                return true;
            case short signedShort:
                value = signedShort;
                return true;
            case ushort unsignedShort:
                value = unsignedShort;
                return true;
            case int signedInt:
                value = signedInt;
                return true;
            case uint unsignedInt:
                value = unsignedInt;
                return true;
            case long signedLong:
                value = signedLong;
                return true;
            case ulong unsignedLong when unsignedLong <= long.MaxValue:
                value = checked((long)unsignedLong);
                return true;
            default:
                if (!raw.GetType().IsEnum)
                    return false;
                try
                {
                    value = Convert.ToInt64(raw);
                    return true;
                }
                catch (OverflowException)
                {
                    return false;
                }
        }
    }

    private static NetherRuntimeInteractivePreEntryCaptureResult Failure(string detail) => new()
    {
        IsCaptured = false,
        Safety = NetherInteractiveFloorPreEntrySafetyResult.Pause(NetherPauseReason.UnknownMasterData, detail),
        Detail = detail,
    };
}
