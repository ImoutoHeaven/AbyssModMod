#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// The fields which remain server-authoritative after FloorSelection has intentionally been
/// destroyed.  The bridge maps these values only after the packaged GET-only Nether datastore
/// sync has completed; this type contains no native callbacks or endpoint access.
/// </summary>
internal sealed record NetherAuthoritativeTransitionState
{
    public NetherSessionStatus Status { get; init; }
    public long NetherId { get; init; }
    public long MapId { get; init; }
    public long CurrentFloorId { get; init; }
    public int FloorLevel { get; init; }
    public int FloorIndex { get; init; }
    public int MaxFloorLevel { get; init; }
    public int ContinuanceFloorLevel { get; init; }
    public int ErosionPoint { get; init; }
    public int TicketCount { get; init; }
    public int SignalCount { get; init; }
    public int TreasureKeyCount { get; init; }
    public int NetherGold { get; init; }
    public int CodeReloadCount { get; init; }
    public int CodeCapacity { get; init; }
    public int LockReward { get; init; }
    public NetherContinuationTarget? ContinuationTarget { get; init; }
    public IReadOnlyList<NetherCodeState> Codes { get; init; } = Array.Empty<NetherCodeState>();
    public IReadOnlyList<NetherRewardItem> AcquiredItems { get; init; } = Array.Empty<NetherRewardItem>();
}

/// <summary>
/// Retains only the last fully validated FloorSelection graph/presentation snapshot.  During a
/// battle scene the graph no longer has a live controller, but the GET-only response still owns
/// session status, current floor coordinates, resources and code portfolio.  This cache joins
/// those two sources only when Nether/map identity and the exact current node coordinate agree.
/// </summary>
internal sealed class NetherTransitionSnapshotCache
{
    private readonly object _gate = new();
    private NetherSnapshot? _lastFullSnapshot;
    private IReadOnlyList<NetherCharacterState>? _battleResultCharacters;

    public void ObserveFullSnapshot(NetherSnapshot snapshot)
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        lock (_gate)
        {
            _lastFullSnapshot = snapshot;
            // A rebuilt FloorSelection model supersedes the transient battle result payload.
            _battleResultCharacters = null;
        }
    }

    public void BeginBattle()
    {
        lock (_gate)
            _battleResultCharacters = null;
    }

    public bool ObserveBattleResultCharacters(IReadOnlyList<NetherCharacterState>? characters)
    {
        if (!TryValidateCharacters(characters, out NetherCharacterState[]? copied))
            return false;
        lock (_gate)
            _battleResultCharacters = copied;
        return true;
    }

    public NetherRuntimeSnapshotResult TryCompose(
        NetherAuthoritativeTransitionState state,
        bool requireFreshBattleCharacters
    )
    {
        if (state == null)
            throw new ArgumentNullException(nameof(state));

        NetherSnapshot? cached;
        IReadOnlyList<NetherCharacterState>? battleCharacters;
        lock (_gate)
        {
            cached = _lastFullSnapshot;
            battleCharacters = _battleResultCharacters;
        }

        if (cached == null)
            return NetherRuntimeSnapshotResult.Failure("missing-cached-floor-selection-snapshot");
        if (state.NetherId <= 0 || state.MapId <= 0
            || state.NetherId != cached.NetherId || state.MapId != cached.MapId)
        {
            return NetherRuntimeSnapshotResult.Failure(
                "cached-transition-owner-mismatch:cached=" + cached.NetherId + ":" + cached.MapId
                + ":fresh=" + state.NetherId + ":" + state.MapId
            );
        }
        if (state.FloorLevel < 0 || state.FloorIndex < 0
            || state.CurrentFloorId <= 0 && state.Status != NetherSessionStatus.Battle)
            return NetherRuntimeSnapshotResult.Failure("invalid-authoritative-current-floor");
        if (state.Codes == null || state.AcquiredItems == null)
            return NetherRuntimeSnapshotResult.Failure("missing-authoritative-transition-collections");

        bool coordinateFallback = state.Status == NetherSessionStatus.Battle
            && state.CurrentFloorId == 0;
        NetherFloorNode[] current = cached.Floors
            .Where(floor => floor != null
                && (coordinateFallback || floor.FloorId == state.CurrentFloorId)
                && floor.FloorLevel == state.FloorLevel
                && floor.ApiFloorIndex == state.FloorIndex)
            .ToArray();
        if (current.Length != 1 || current[0].NodeId <= 0)
        {
            if (coordinateFallback)
            {
                return NetherRuntimeSnapshotResult.Failure(
                    "authoritative-battle-coordinate-not-unique:level=" + state.FloorLevel
                    + ":api-index=" + state.FloorIndex
                    + ":matches=" + current.Length
                );
            }
            return NetherRuntimeSnapshotResult.Failure(
                "authoritative-current-node-not-unique:master=" + state.CurrentFloorId
                + ":level=" + state.FloorLevel
                + ":api-index=" + state.FloorIndex
                + ":matches=" + current.Length
            );
        }

        IReadOnlyList<NetherCharacterState> characters;
        if (requireFreshBattleCharacters)
        {
            if (battleCharacters == null)
                return NetherRuntimeSnapshotResult.Failure("missing-authoritative-battle-result-characters");
            characters = battleCharacters;
        }
        else
        {
            characters = battleCharacters ?? cached.Characters;
        }
        if (!TryValidateCharacters(characters, out NetherCharacterState[]? validatedCharacters))
            return NetherRuntimeSnapshotResult.Failure("invalid-transition-characters");

        NetherCodeState[] codes = state.Codes.ToArray();
        if (codes.Any(code => code == null || code.CodeId <= 0)
            || codes.GroupBy(code => code.CodeId).Any(group => group.Count() != 1))
        {
            return NetherRuntimeSnapshotResult.Failure("invalid-authoritative-transition-codes");
        }

        var snapshot = new NetherSnapshot
        {
            Status = state.Status,
            NetherId = state.NetherId,
            MapId = state.MapId,
            // The live Battle payload intentionally reports m_nether_map_floor_id=0.  Only
            // Battle may recover that master ID, and only from one exact cached
            // (floor_level, floor_index) coordinate.  Play/Wait/Sleep never receive this
            // fallback and therefore cannot silently drift to a different map node.
            CurrentFloorId = current[0].FloorId,
            CurrentNodeId = current[0].NodeId,
            FloorLevel = state.FloorLevel,
            FloorIndex = state.FloorIndex,
            MaxFloorLevel = state.MaxFloorLevel,
            ContinuanceFloorLevel = state.ContinuanceFloorLevel,
            MasterMaxFloorLevel = cached.MasterMaxFloorLevel,
            ErosionPoint = state.ErosionPoint,
            TicketCount = state.TicketCount,
            SignalCount = state.SignalCount,
            TreasureKeyCount = state.TreasureKeyCount,
            NetherGold = state.NetherGold,
            CodeReloadCount = state.CodeReloadCount,
            CodeCapacity = state.CodeCapacity,
            LockReward = state.LockReward,
            ContinuationTarget = state.ContinuationTarget,
            Characters = validatedCharacters!,
            Codes = codes,
            Floors = cached.Floors,
            AcquiredItems = state.AcquiredItems.ToArray(),
            CharacterHpHash = CreateCharacterHash(validatedCharacters!),
            CodeHash = CreateCodeHash(codes),
            MapHash = cached.MapHash,
        };
        return NetherRuntimeSnapshotResult.Success(snapshot);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _lastFullSnapshot = null;
            _battleResultCharacters = null;
        }
    }

    private static bool TryValidateCharacters(
        IReadOnlyList<NetherCharacterState>? characters,
        out NetherCharacterState[]? copied
    )
    {
        copied = null;
        if (characters == null || characters.Count == 0)
            return false;
        NetherCharacterState[] values = characters.ToArray();
        if (values.Any(character => character.CharacterId <= 0
                || character.HpPermille is < 0 or > 1000)
            || values.GroupBy(character => character.CharacterId).Any(group => group.Count() != 1))
        {
            return false;
        }
        copied = values;
        return true;
    }

    internal static string CreateCharacterHash(IEnumerable<NetherCharacterState> characters) =>
        string.Join(
            ";",
            characters.OrderBy(character => character.CharacterId).Select(character =>
                character.CharacterId.ToString(CultureInfo.InvariantCulture) + ":"
                + character.HpPermille.ToString(CultureInfo.InvariantCulture) + ":"
                + (character.IsActive ? "1" : "0")
            )
        );

    internal static string CreateCodeHash(IEnumerable<NetherCodeState> codes) => string.Join(
        ";",
        codes.OrderBy(code => code.CodeId).Select(code =>
            code.CodeId.ToString(CultureInfo.InvariantCulture) + ":"
            + code.Level.ToString(CultureInfo.InvariantCulture) + ":"
            + ((int)code.EffectKind).ToString(CultureInfo.InvariantCulture)
        )
    );
}
