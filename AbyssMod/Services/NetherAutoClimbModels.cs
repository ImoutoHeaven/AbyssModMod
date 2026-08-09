#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

internal enum NetherSessionStatus
{
    Unknown = 0,
    NotPlayed = 1,
    Play = 2,
    Wait = 3,
    Battle = 5,
    Sleep = 6,
    Lose = 7,
    Clear = 8,
}

internal enum NetherFloorNodeType
{
    Unknown = 0,
    Battle = 1,
    Boss = 2,
    MiniBoss = 3,
    Event = 4,
    Recovery = 5,
    Shop = 6,
    Treasure = 7,
    Default = 8,
}

internal enum NetherAutoClimbPhase
{
    Disabled,
    Reconciling,
    Stable,
    ExecutingNativeAction,
    AwaitingBattleSceneHandoff,
    AwaitingContinueSceneHandoff,
    AwaitingBattle,
    AwaitingF11,
    AwaitingBattleSettlement,
    AwaitingBattleResultContinuation,
    AwaitingSceneChange,
    Paused,
    Completed,
}

internal enum NetherActionKind
{
    None,
    Reconcile,
    SelectFloor,
    SelectEventOption,
    LeaveShop,
    BuyShopItem,
    SelectCode,
    ReloadCode,
    /// <summary>
    /// Exact Abyss-code offer cancel flow.  This is terminal for the owned CodeOffer only
    /// after the generated HandleCancelSequenceAsync UniTask has completed; it never means a
    /// visual popup close.
    /// </summary>
    KeepCode,
    Continue,
    FinishAtCheckpoint,
    SelectReturnItems,
    AwaitNativeFlow,
    BattleSettlement,
    RestoreBattleSettings,
    /// <summary>Native floor-event Abyss-code conversion: remove one current code and receive one server-selected code.</summary>
    TransformCode,
}

internal enum NetherPauseReason
{
    None,
    NotInNether,
    NotPlayed,
    UnknownStatus,
    AmbiguousServerOutcome,
    BindingUnavailable,
    InvalidGraph,
    NoSafeRoute,
    UnknownFloor,
    UnsafeErosion,
    ErosionDrift,
    UnsafeHp,
    UnknownEffect,
    UnknownMasterData,
    UnsupportedPopup,
    InvalidConfiguration,
    BattleSettingsLeaseFault,
    BattleLifecycleFault,
    BattleLifecycleCanceled,
    BattleSettlementUnchanged,
    BattleSettlementWrongTarget,
    /// <summary>The authoritative post-battle snapshot cannot prove the immutable safety projection.</summary>
    BattleProjectionUnknown,
    /// <summary>The authoritative post-battle state drifted outside the immutable battle projection.</summary>
    BattleProjectionDrift,
    BattleSceneLost,
    ContinueLifecycleFault,
    ContinueLifecycleCanceled,
    ContinueTeardownTimeout,
    ContinueRebindTimeout,
    ContinueRebindWrongScene,
    ContinueSettlementWrongTarget,
    ResultLifecycleFault,
    ResultLifecycleCanceled,
    TargetReachedOutsideCheckpoint,
    Lose,
    UserDisabled,
}

internal enum NetherActionOutcome
{
    Applied,
    NotApplied,
    Ambiguous,
}

/// <summary>
/// The bridge never treats an unavailable native binding as a failed action: a failed
/// binding means that no request was made and the coordinator must pause safely.
/// </summary>
internal enum NetherNativeActionResultKind
{
    Started,
    Completed,
    Rejected,
    UnknownOutcome,
    BindingUnavailable,
}

internal readonly record struct NetherNativeActionResult(
    NetherNativeActionResultKind Kind,
    string Detail
)
{
    public static NetherNativeActionResult Started(string detail) => new(NetherNativeActionResultKind.Started, detail);

    public static NetherNativeActionResult Completed(string detail) => new(NetherNativeActionResultKind.Completed, detail);

    public static NetherNativeActionResult Rejected(string detail) => new(NetherNativeActionResultKind.Rejected, detail);

    public static NetherNativeActionResult UnknownOutcome(string detail) => new(NetherNativeActionResultKind.UnknownOutcome, detail);

    public static NetherNativeActionResult BindingUnavailable(string detail) => new(NetherNativeActionResultKind.BindingUnavailable, detail);
}

/// <summary>
/// A versioned native signature, represented without a reflection dependency so its
/// fail-closed matching rules can be characterized in the pure test project.
/// </summary>
internal sealed record NetherNativeMethodDescriptor(
    string Name,
    IReadOnlyList<string> ParameterTypeNames,
    string ReturnTypeName
)
{
    public int Arity => ParameterTypeNames.Count;

    /// <summary>
    /// Optional because existing non-reflection policy descriptors intentionally describe only
    /// a callable shape.  Exact generated callbacks may additionally require their proven
    /// static/instance ownership, which prevents an adjacent compiler-generated method from
    /// satisfying a superficially identical signature.
    /// </summary>
    public bool? IsStatic { get; init; }
}

internal readonly record struct NetherNativeBindingSelection(
    NetherNativeActionResultKind ResultKind,
    NetherNativeMethodDescriptor? Method,
    string Detail
);

internal static class NetherNativeMethodBindingSelector
{
    public static NetherNativeBindingSelection Select(
        NetherNativeMethodDescriptor expected,
        IEnumerable<NetherNativeMethodDescriptor> candidates
    )
    {
        if (expected is null)
            throw new ArgumentNullException(nameof(expected));
        if (candidates is null)
            throw new ArgumentNullException(nameof(candidates));

        List<NetherNativeMethodDescriptor> exact = new();
        foreach (NetherNativeMethodDescriptor candidate in candidates)
        {
            if (candidate is null || !Matches(expected, candidate))
                continue;
            exact.Add(candidate);
        }

        return exact.Count switch
        {
            1 => new NetherNativeBindingSelection(
                NetherNativeActionResultKind.Started,
                exact[0],
                "exact-signature"
            ),
            0 => new NetherNativeBindingSelection(
                NetherNativeActionResultKind.BindingUnavailable,
                null,
                "no-exact-signature"
            ),
            _ => new NetherNativeBindingSelection(
                NetherNativeActionResultKind.BindingUnavailable,
                null,
                "ambiguous-exact-signature"
            ),
        };
    }

    private static bool Matches(NetherNativeMethodDescriptor expected, NetherNativeMethodDescriptor candidate)
    {
        if (
            !string.Equals(expected.Name, candidate.Name, StringComparison.Ordinal)
            || expected.Arity != candidate.Arity
            || !string.Equals(expected.ReturnTypeName, candidate.ReturnTypeName, StringComparison.Ordinal)
            || (expected.IsStatic.HasValue && candidate.IsStatic != expected.IsStatic)
        )
            return false;

        for (int index = 0; index < expected.Arity; index++)
        {
            if (
                !string.Equals(
                    expected.ParameterTypeNames[index],
                    candidate.ParameterTypeNames[index],
                    StringComparison.Ordinal
                )
            )
                return false;
        }

        return true;
    }
}

internal enum NetherCombatLane
{
    Auto,
    Rush,
    Impact,
}

internal enum NetherTreasureMode
{
    Off,
    KeyOnly,
}

internal enum NetherShopMode
{
    Off,
    EquipmentBags,
}

internal enum NetherEffectKind
{
    Unknown = 0,
    Heal = 1,
    Damage = 2,
    Erosion = 3,
    ErosionHeal = 4,
    NetherGoldUsed = 5,
    TreasureKeyUsed = 6,
    /// <summary>Native target_type=7: open the code-conversion list; its parameter is not a code ID.</summary>
    AbyssCodeTransform = 7,
    Battle = 8,
    Item = 9,
    NetherGoldGain = 10,
    TreasureKeyGain = 11,
    /// <summary>Native content_type=160: open the server-provided Abyss-code offer flow.</summary>
    AbyssCodeOffer = 12,
}

internal enum NetherCodeEffectKind
{
    Unknown = 0,
    Safe = 1,
    Risk = 2,
    Rush = 3,
    Impact = 4,
    ErosionAdditionUp = 5,
    ErosionAdditionDown = 6,
    ErosionRateUp = 7,
    ErosionRateDown = 8,
    ResearchOnly = 9,
    /// <summary>
    /// A category-confirmed ordinary tactic code.  The packaged category proves it is
    /// selectable, but does not prove a Rush/Impact, party-coverage, or research semantic.
    /// It therefore participates as a deterministic general candidate rather than being
    /// guessed into either combat lane.
    /// </summary>
    General = 10,
}

/// <summary>
/// Exact numeric values from Project.NetherCodeCategoryType in the packaged client.
/// </summary>
internal enum NetherCodeCategory
{
    Unknown = 0,
    Technique = 1,
    Strength = 2,
    ErosionResistance = 3,
    ErosionEnhancement = 4,
}

/// <summary>Exact Project.NetherCodeCategoryGroupType values, kept local to the policy seam.</summary>
internal enum NetherCodeCategoryGroup
{
    Unknown = -1,
    Tactics = 0,
    Erosion = 1,
}

internal enum NetherRewardRarity
{
    NoEffect = 0,
    Silver = 1,
    Purple = 2,
    Gold = 3,
    Red = 4,
    UniqueWeapon = 5,
}

internal readonly record struct NetherSnapshotFingerprint(
    NetherSessionStatus status,
    long netherId,
    long mapId,
    int floorLevel,
    int floorIndex,
    int erosionPoint,
    string characterHpHash,
    string codeHash,
    string mapHash,
    long currentFloorId = 0,
    int ticketCount = 0,
    int treasureKeyCount = 0,
    int netherGold = 0,
    int codeReloadCount = 0,
    int lockReward = 0,
    long currentNodeId = 0
)
{
    public NetherSessionStatus Status => status;
    public long NetherId => netherId;
    public long MapId => mapId;
    public int FloorLevel => floorLevel;
    public int FloorIndex => floorIndex;
    public int ErosionPoint => erosionPoint;
    public string CharacterHpHash => characterHpHash ?? string.Empty;
    public string CodeHash => codeHash ?? string.Empty;
    public string MapHash => mapHash ?? string.Empty;
    public long CurrentFloorId => currentFloorId;
    public int TicketCount => ticketCount;
    public int TreasureKeyCount => treasureKeyCount;
    public int NetherGold => netherGold;
    public int CodeReloadCount => codeReloadCount;
    public int LockReward => lockReward;
    public long CurrentNodeId => currentNodeId;
}

internal sealed record NetherFloorNode(
    long FloorId,
    int FloorLevel,
    int FloorIndex,
    NetherFloorNodeType NodeType
)
{
    /// <summary>
    /// Stable server-coordinate identity for this rendered node.  <see cref="FloorId"/> is the
    /// reusable MNetherMapFloors/master ID and is not globally unique in a Nether map.
    /// Tests and non-runtime callers retain the historical default for compact fixtures.
    /// </summary>
    public long NodeId { get; init; } = FloorId;
    /// <summary>Server/API floor_index (native FloorPosition), distinct from the per-level UI index.</summary>
    public int ApiFloorIndex { get; init; } = FloorIndex;
    public bool IsHidden { get; init; }
    public bool IsUnlocked { get; init; }
    public IReadOnlyList<long> PreviousFloorIds { get; init; } = Array.Empty<long>();
    public int RewardTier { get; init; }
    public int OptionalCombatCount { get; init; }
}

internal readonly record struct NetherCharacterState(
    long CharacterId,
    int HpPermille,
    bool IsActive = true
);

internal sealed record NetherCodeState(long CodeId, NetherCodeEffectKind EffectKind, int Level)
{
    public bool IsKnown { get; init; } = true;
    public NetherCodeCategory Category { get; init; }
    public int Rarity { get; init; }
    /// <summary>
    /// A numeric zero is not evidence that no party member benefits.  Runtime mapping keeps
    /// this false until an exact ability/party authority proves the value.
    /// </summary>
    public bool PartyCoverageKnown { get; init; }
    public int PartyCoverage { get; init; }
    /// <summary>False is a value only when <see cref="IsResearchOnlyKnown"/> is true.</summary>
    public bool IsResearchOnlyKnown { get; init; }
    public bool IsResearchOnly { get; init; }
}

internal sealed record NetherEffect(NetherEffectKind Kind, int Amount)
{
    public bool Known { get; init; } = true;
    public bool ContentKnown { get; init; } = true;
    public int RatePermille { get; init; } = 1000;
    public long ContentId { get; init; }
    public long ReplacementCodeId { get; init; }
    public bool IsOptionalBattle { get; init; }
}

internal sealed record NetherRewardItem(long ItemId, int Amount)
{
    public bool HasMasterData { get; init; } = true;
    /// <summary>
    /// The acquisition datastore does not carry the server-return-popup rarity.  A false
    /// value is deliberately not ranked as <see cref="NetherRewardRarity.NoEffect"/>: the
    /// item must be remapped from the freshly created native return popup before use.
    /// </summary>
    public bool HasVerifiedDropRarity { get; init; } = true;
    public int ItemType { get; init; }
    public NetherRewardRarity DropRarity { get; init; }
    public int MasterRarity { get; init; }
}

internal sealed record NetherAutoClimbSettings
{
    public int MaxDepth { get; init; } = 130;
    public int SoftErosionLimit { get; init; } = 90;
    public int MinimumCharacterHpPermille { get; init; } = 300;
    public NetherCombatLane CombatLane { get; init; } = NetherCombatLane.Auto;
    public int CodeReloadReserve { get; init; } = 1;
    public NetherTreasureMode TreasureMode { get; init; } = NetherTreasureMode.KeyOnly;
    public NetherShopMode ShopMode { get; init; } = NetherShopMode.Off;
    public bool DetailedLogging { get; init; } = true;
}

/// <summary>
/// Exact, pre-mutation target for a Sleep continuation, derived from the current map-floor
/// master chain.  A missing target is intentionally represented as null on the snapshot so the
/// controller pauses before issuing Continue rather than inferring the next segment.
/// </summary>
internal sealed record NetherContinuationTarget(
    long MapId,
    long FloorId,
    int SegmentFloorLevel
);

internal sealed record NetherSnapshot
{
    public NetherSessionStatus Status { get; init; }
    public long NetherId { get; init; }
    public long MapId { get; init; }
    public long CurrentFloorId { get; init; }
    /// <summary>Coordinate identity of the current rendered node; FloorId remains the master ID.</summary>
    public long CurrentNodeId { get; init; }
    public int FloorLevel { get; init; }
    public int FloorIndex { get; init; }
    public int MaxFloorLevel { get; init; }
    public int ContinuanceFloorLevel { get; init; }
    public int MasterMaxFloorLevel { get; init; }
    public int ErosionPoint { get; init; }
    public int TicketCount { get; init; }
    public int SignalCount { get; init; }
    public int TreasureKeyCount { get; init; }
    public int NetherGold { get; init; }
    public int CodeReloadCount { get; init; }
    public int CodeCapacity { get; init; }
    public int LockReward { get; init; }
    public NetherContinuationTarget? ContinuationTarget { get; init; }
    public IReadOnlyList<NetherCharacterState> Characters { get; init; } = Array.Empty<NetherCharacterState>();
    public IReadOnlyList<NetherCodeState> Codes { get; init; } = Array.Empty<NetherCodeState>();
    public IReadOnlyList<NetherFloorNode> Floors { get; init; } = Array.Empty<NetherFloorNode>();
    public IReadOnlyList<NetherRewardItem> AcquiredItems { get; init; } = Array.Empty<NetherRewardItem>();
    public string CharacterHpHash { get; init; } = string.Empty;
    public string CodeHash { get; init; } = string.Empty;
    public string MapHash { get; init; } = string.Empty;

    public NetherSnapshotFingerprint Fingerprint => new(
        Status,
        NetherId,
        MapId,
        FloorLevel,
        FloorIndex,
        ErosionPoint,
        CharacterHpHash,
        CodeHash,
        MapHash,
        CurrentFloorId,
        TicketCount,
        TreasureKeyCount,
        NetherGold,
        CodeReloadCount,
        LockReward,
        CurrentNodeId
    );
}

internal sealed record NetherBattleSettlementContract(
    long EntryMapId,
    long EntryFloorId,
    NetherSessionStatus EntryStatus,
    long ExpectedMapId,
    long ExpectedFloorId,
    NetherSessionStatus ExpectedStatus,
    string ProjectionIdentity
)
{
    public NetherBattleProjectionPayload? EntryProjection { get; init; }
}

/// <summary>
/// Immutable combat safety evidence captured immediately before the native floor click.  The
/// battle-settlement action keeps this payload rather than recomputing against a changed code
/// portfolio or erosion value after the server has accepted the node.
/// </summary>
internal sealed record NetherBattleProjectionPayload(
    long MapId,
    long FloorId,
    int PreBattleErosion,
    int FloorMinimumErosion,
    int FloorMaximumErosion,
    int ProjectedMinimumErosion,
    int ProjectedMaximumErosion,
    string CodeHash,
    string ProjectionIdentity
);

/// <summary>
/// One immutable, owned modal stage in a SelectFloor native parent chain.  A floor parent can
/// legitimately create more than one popup (for example Event -> Change Code -> Code Select),
/// so the final read-only reconcile must retain every stage rather than replacing the first
/// popup contract with the most recent one.
/// </summary>
internal sealed record NetherFloorPopupStage(
    NetherRuntimePopupKind PopupKind,
    NetherActionKind ActionKind,
    long OwnerGeneration,
    long Sequence,
    NetherSessionStatus ExpectedAfterStatus,
    int OptionNumber,
    IReadOnlyList<NetherEffect> ExpectedEffects,
    long ContentId,
    int ContentAmount,
    int GoldCost,
    long CodeId,
    long ReplaceCodeId,
    long DecisionEpoch = 0
);

internal readonly record struct NetherPlannedAction(NetherActionKind Kind)
{
    public long FloorId { get; init; }
    public int FloorLevel { get; init; }
    public int FloorIndex { get; init; }
    /// <summary>Exact server-owned status required before the selected floor action.</summary>
    public NetherSessionStatus ExpectedBeforeStatus { get; init; } = NetherSessionStatus.Unknown;
    /// <summary>Exact server-owned status required after the selected floor action.</summary>
    public NetherSessionStatus ExpectedAfterStatus { get; init; } = NetherSessionStatus.Unknown;
    public int OptionNumber { get; init; }
    /// <summary>Only fully mapped effects may be used to prove an event postcondition.</summary>
    public IReadOnlyList<NetherEffect> ExpectedEffects { get; init; } = Array.Empty<NetherEffect>();
    public long ContentId { get; init; }
    public int ContentAmount { get; init; }
    public int GoldCost { get; init; }
    public long CodeId { get; init; }
    public long ReplaceCodeId { get; init; }
    public int TicketCount { get; init; }
    public int TicketCost { get; init; }
    public long ExpectedMapId { get; init; }
    /// <summary>Exact post-Continue floor ID, not the source floor-selection ID.</summary>
    public long ExpectedFloorId { get; init; }
    public int ExpectedSegmentFloorLevel { get; init; }
    /// <summary>
    /// Once a SelectFloor parent has opened an owned popup, these two fields retain the
    /// immutable child contract used for the single parent reconciliation.  They prevent a
    /// visual popup close from being treated as an untyped successful floor selection.
    /// </summary>
    public NetherRuntimePopupKind OwnedPopupKind { get; init; }
    public NetherActionKind OwnedPopupActionKind { get; init; }
    /// <summary>
    /// Ordered immutable proof for every owned modal dispatched by this one SelectFloor
    /// parent.  Legacy scalar fields above mirror the final stage for compact audit output;
    /// reconciliation uses this collection whenever it is populated.
    /// </summary>
    public IReadOnlyList<NetherFloorPopupStage> OwnedPopupStages { get; init; }
        = Array.Empty<NetherFloorPopupStage>();
    public NetherBattleSettlementContract? BattleSettlement { get; init; }
    /// <summary>Set only for a safety-approved combat floor before its native selection parent begins.</summary>
    public NetherBattleProjectionPayload? BattleProjection { get; init; }
    /// <summary>
    /// A checkpoint continuation carries only its lock count and explicit user preserve IDs.
    /// The live datastore preflight is captured before starting the native Continue parent and
    /// the fresh native return popup must match this contract before it can be confirmed.
    /// </summary>
    public int ReturnLockReward { get; init; }
    public IReadOnlyList<long> ReturnPreserveItemIds { get; init; } = Array.Empty<long>();
    public int ReturnPreflightSelectionLimit { get; init; }
    public string ReturnExpectedPristineHash { get; init; } = string.Empty;
    public IReadOnlyList<NetherCheckpointReturnPreflightItem> ReturnPreflightWholeEntrySelection { get; init; }
        = Array.Empty<NetherCheckpointReturnPreflightItem>();
}
