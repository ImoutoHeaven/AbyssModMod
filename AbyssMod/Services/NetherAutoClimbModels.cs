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
    AwaitingBattle,
    AwaitingF11,
    AwaitingBattleSettlement,
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
    Continue,
    FinishAtCheckpoint,
    AwaitNativeFlow,
    RestoreBattleSettings,
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
    AbyssCodeChanged = 7,
    Battle = 8,
    Item = 9,
    NetherGoldGain = 10,
    TreasureKeyGain = 11,
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
    string mapHash
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
}

internal sealed record NetherFloorNode(
    long FloorId,
    int FloorLevel,
    int FloorIndex,
    NetherFloorNodeType NodeType
)
{
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
    public int Rarity { get; init; }
    public int PartyCoverage { get; init; }
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

internal sealed record NetherSnapshot
{
    public NetherSessionStatus Status { get; init; }
    public long NetherId { get; init; }
    public long MapId { get; init; }
    public long CurrentFloorId { get; init; }
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
        MapHash
    );
}

internal readonly record struct NetherPlannedAction(NetherActionKind Kind)
{
    public long FloorId { get; init; }
    public int FloorLevel { get; init; }
    public int FloorIndex { get; init; }
    public int OptionNumber { get; init; }
    public long ContentId { get; init; }
    public long CodeId { get; init; }
    public long ReplaceCodeId { get; init; }
    public int TicketCount { get; init; }
}
