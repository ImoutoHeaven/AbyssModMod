#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AbyssMod.Services;

public enum NetherWeaponType
{
    Unknown = 0,
    OneHandSword = 1,
    GreatSword = 2,
    Fists = 3,
    Bow = 4,
    Gun = 5,
    Staff = 6,
    Grimoire = 7,
    Pickel = 8,
}

[Flags]
public enum NetherWeaponTypeFilter
{
    Any = 0,
    OneHandSword = 1 << 0,
    GreatSword = 1 << 1,
    Fists = 1 << 2,
    Bow = 1 << 3,
    Gun = 1 << 4,
    Staff = 1 << 5,
    Grimoire = 1 << 6,
    Pickel = 1 << 7,
}

public readonly record struct NetherItemMasterInfo(
    long ItemType,
    int ContentRarity,
    NetherWeaponType WeaponType = NetherWeaponType.Unknown
);

public enum NetherTargetReason
{
    EquipmentStopCondition = 0,
    PreservedNetherItemId = 1,
}

public enum NetherPreserveMode
{
    AND = 0,
    OR = 1,
}

public readonly record struct NetherTargetDrop(
    BattleDropItem Drop,
    NetherItemMasterInfo Master,
    NetherTargetReason Reason = NetherTargetReason.EquipmentStopCondition,
    bool HasMaster = true,
    int EffectiveRarity = 0
);

public static class NetherPreserveItemIdParser
{
    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\r', '\n' };

    public static bool TryParse(
        string value,
        out HashSet<long> itemIds,
        out string error
    )
    {
        itemIds = new HashSet<long>();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        string[] tokens = value.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            if (!long.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long itemId
                )
                || itemId <= 0)
            {
                itemIds.Clear();
                error = $"invalid-preserve-item-id:{token}";
                return false;
            }

            itemIds.Add(itemId);
        }

        return true;
    }

    public static string Format(HashSet<long>? itemIds)
    {
        if (itemIds == null || itemIds.Count == 0)
            return "none";

        var sorted = new List<long>(itemIds);
        sorted.Sort();
        return string.Join(",", sorted);
    }
}

public sealed class NetherBattleDropProbeReport
{
    public IReadOnlyList<BattleDropItem> AllItems { get; }
    public IReadOnlyList<BattleDropItem> EnemyItems { get; }
    public string Error { get; }

    public int DropCount => AllItems.Count;
    public int EnemyDropCount => EnemyItems.Count;

    public NetherBattleDropProbeReport(
        IReadOnlyList<BattleDropItem> allItems,
        IReadOnlyList<BattleDropItem> enemyItems,
        string error = ""
    )
    {
        AllItems = allItems;
        EnemyItems = enemyItems;
        Error = error;
    }

    public string FormatAllItems() => FormatItems(AllItems);

    public string FormatEnemyItems() => FormatItems(EnemyItems);

    private static string FormatItems(IReadOnlyList<BattleDropItem> items)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                builder.Append("; ");

            BattleDropItem item = items[i];
            builder.Append("sid=").Append(item.Sid)
                .Append(" contentType=").Append(item.ContentType)
                .Append(" contentId=").Append(item.ContentId)
                .Append(" amount=").Append(item.Amount)
                .Append(" rarity=").Append(item.RarityLevel)
                .Append(" isRare=").Append(item.IsRare ? 1 : 0);
        }
        return builder.ToString();
    }
}

public readonly record struct NetherBypassTraceInput(
    IReadOnlyList<BattleDropItem> RootDrops,
    string Error
)
{
    public static NetherBypassTraceInput FromStageDetail(string? stageDetail)
    {
        BattleDropProbeReport report = BattleSessionDropProbe.Parse(stageDetail ?? string.Empty);
        return new NetherBypassTraceInput(report.Items, report.Error);
    }
}

public static class NetherBattleDropProbe
{
    public static NetherBattleDropProbeReport Parse(string stageDetail)
    {
        BattleDropProbeReport allDrops = BattleSessionDropProbe.Parse(stageDetail);
        if (allDrops.Error.Length != 0)
            return Error(allDrops.Items, allDrops.Error);

        try
        {
            using JsonDocument document = JsonDocument.Parse(stageDetail);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("enemies", out JsonElement enemies)
                || enemies.ValueKind != JsonValueKind.Array)
                return Error(allDrops.Items, "missing-enemies");

            var dropBySid = new Dictionary<long, BattleDropItem>();
            foreach (BattleDropItem item in allDrops.Items)
            {
                if (!dropBySid.TryAdd(item.Sid, item))
                    return Error(allDrops.Items, $"duplicate-drop-sid:{item.Sid}");
            }

            var enemyDropSids = new List<long>();
            var seenEnemyDropSids = new HashSet<long>();
            foreach (JsonElement enemy in enemies.EnumerateArray())
            {
                if (enemy.ValueKind != JsonValueKind.Object)
                    return Error(allDrops.Items, "invalid-enemy");

                if (!enemy.TryGetProperty("drops", out JsonElement drops)
                    || drops.ValueKind == JsonValueKind.Null)
                    continue;

                if (drops.ValueKind != JsonValueKind.Array)
                    return Error(allDrops.Items, "invalid-enemy-drops");

                foreach (JsonElement sidElement in drops.EnumerateArray())
                {
                    if (sidElement.ValueKind != JsonValueKind.Number
                        || !sidElement.TryGetInt64(out long sid))
                        return Error(allDrops.Items, "invalid-enemy-drop-sid");

                    if (seenEnemyDropSids.Add(sid))
                        enemyDropSids.Add(sid);
                }
            }

            var enemyItems = new List<BattleDropItem>(enemyDropSids.Count);
            foreach (long sid in enemyDropSids)
            {
                if (!dropBySid.TryGetValue(sid, out BattleDropItem item))
                    return Error(allDrops.Items, $"unresolved-enemy-drop-sid:{sid}");
                enemyItems.Add(item);
            }

            return new NetherBattleDropProbeReport(allDrops.Items, enemyItems);
        }
        catch (JsonException)
        {
            return Error(allDrops.Items, "parse-error");
        }
    }

    private static NetherBattleDropProbeReport Error(
        IReadOnlyList<BattleDropItem> allItems,
        string error
    ) => new(allItems, Array.Empty<BattleDropItem>(), error);
}

public sealed class NetherBattleDropEvaluation
{
    public IReadOnlyList<NetherTargetDrop> Targets { get; }
    public string Error { get; }
    public bool StopConditionMatched { get; }
    public int EquipmentTargetCount { get; }
    public int PreservedItemTargetCount { get; }
    public bool ShouldRetry => Error.Length == 0 && !StopConditionMatched;

    public NetherBattleDropEvaluation(
        IReadOnlyList<NetherTargetDrop> targets,
        bool stopConditionMatched,
        string error = ""
    )
    {
        Targets = targets;
        StopConditionMatched = stopConditionMatched;
        Error = error;

        int equipmentTargetCount = 0;
        int preservedItemTargetCount = 0;
        foreach (NetherTargetDrop target in targets)
        {
            if (target.Reason == NetherTargetReason.PreservedNetherItemId)
                preservedItemTargetCount++;
            else if (target.Reason == NetherTargetReason.EquipmentStopCondition)
                equipmentTargetCount++;
        }

        EquipmentTargetCount = equipmentTargetCount;
        PreservedItemTargetCount = preservedItemTargetCount;
    }

    public string FormatTargets()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < Targets.Count; i++)
        {
            if (i > 0)
                builder.Append("; ");

            NetherTargetDrop target = Targets[i];
            builder.Append("sid=").Append(target.Drop.Sid)
                .Append(" contentId=").Append(target.Drop.ContentId)
                .Append(" reason=").Append(FormatReason(target.Reason))
                .Append(" dropRarity=").Append(target.Drop.RarityLevel)
                .Append(" effectiveRarity=").Append(target.EffectiveRarity)
                .Append(" masterType=").Append(target.HasMaster ? target.Master.ItemType.ToString() : "n/a")
                .Append(" masterRarity=").Append(target.HasMaster ? target.Master.ContentRarity.ToString() : "n/a")
                .Append(" isRare=").Append(target.Drop.IsRare ? 1 : 0);
        }
        return builder.ToString();
    }

    private static string FormatReason(NetherTargetReason reason) => reason switch
    {
        NetherTargetReason.EquipmentStopCondition => "equipment-stop",
        NetherTargetReason.PreservedNetherItemId => "preserve-item-id",
        _ => $"unknown({(int)reason})",
    };
}

public static class NetherBattleAutoSLPolicy
{
    public const int NetherItemContentType = 31;
    public const int NetherInventoryItemType = 90;
    public const int NetherEquipmentItemType = 91;
    public const int GoldRarityLevel = 3;
    public const int RedRarityLevel = 4;

    public static NetherBattleDropEvaluation Evaluate(
        NetherBattleDropProbeReport report,
        IReadOnlyDictionary<long, NetherItemMasterInfo>? masterItems,
        NetherSlTarget target,
        bool equipmentOnly = true,
        NetherPreserveMode preserveMode = NetherPreserveMode.AND,
        HashSet<long>? preservedItemIds = null,
        NetherWeaponTypeFilter weaponTypes = NetherWeaponTypeFilter.Any
    )
    {
        if (report.Error.Length != 0)
            return Error(report.Error);
        if (target == NetherSlTarget.Off || !Enum.IsDefined(typeof(NetherSlTarget), target))
            return Error($"invalid-nether-sl-target:{(int)target}");
        if (!IsValidWeaponTypeFilter(weaponTypes))
            return Error($"invalid-nether-weapon-types:{(int)weaponTypes}");
        if (weaponTypes != NetherWeaponTypeFilter.Any && !equipmentOnly)
            return Error("nether-weapon-types-require-equipment-only");
        bool hasPreserveRules = preservedItemIds != null && preservedItemIds.Count > 0;
        if (hasPreserveRules && !Enum.IsDefined(typeof(NetherPreserveMode), preserveMode))
            return Error($"unsupported-preserve-mode:{(int)preserveMode}");
        if ((equipmentOnly || hasPreserveRules)
            && (masterItems == null || masterItems.Count == 0))
            return Error("missing-item-master");

        if (hasPreserveRules)
        {
            foreach (long itemId in preservedItemIds!)
            {
                if (!masterItems!.TryGetValue(itemId, out NetherItemMasterInfo preserveMaster))
                    return Error($"unknown-preserve-item-id:{itemId}");
                if (preserveMaster.ItemType != NetherInventoryItemType)
                    return Error(
                        $"preserve-item-type-mismatch:{itemId}:{preserveMaster.ItemType}"
                    );
            }
        }

        var targets = new List<NetherTargetDrop>();
        bool equipmentStopMatched = false;
        bool preservedItemMatched = false;
        foreach (BattleDropItem item in report.EnemyItems)
        {
            NetherItemMasterInfo master = default;
            bool hasMaster = masterItems != null
                && masterItems.TryGetValue(item.ContentId, out master);

            if (hasPreserveRules && preservedItemIds!.Contains(item.ContentId))
            {
                if (!hasMaster)
                    return Error($"unresolved-preserve-item-master:{item.ContentId}");
                if (item.ContentType != NetherItemContentType)
                    return Error(
                        $"preserve-content-type-mismatch:{item.ContentId}:{item.ContentType}"
                    );

                targets.Add(
                    new NetherTargetDrop(
                        item,
                        master,
                        NetherTargetReason.PreservedNetherItemId,
                        true,
                        item.RarityLevel
                    )
                );
                preservedItemMatched = true;
                continue;
            }

            if (equipmentOnly && !hasMaster)
            {
                if (item.ContentType == NetherItemContentType)
                    return Error($"unresolved-nether-item-master:{item.ContentId}");
                continue;
            }

            if (equipmentOnly)
            {
                if (master.ItemType != NetherEquipmentItemType)
                    continue;

                if (item.ContentType != NetherItemContentType)
                    return Error($"content-type-mismatch:{item.ContentId}:{item.ContentType}");
                if (weaponTypes != NetherWeaponTypeFilter.Any)
                {
                    NetherWeaponTypeFilter itemWeaponType = ToWeaponTypeFilter(master.WeaponType);
                    if (itemWeaponType == NetherWeaponTypeFilter.Any)
                        return Error($"unresolved-nether-equipment-type:{item.ContentId}");
                    if ((weaponTypes & itemWeaponType) == NetherWeaponTypeFilter.Any)
                        continue;
                }
            }

            if (item.RarityLevel < (int)NetherSlTarget.NoEffect
                || item.RarityLevel > (int)NetherSlTarget.UniqueWeapon)
            {
                return Error($"invalid-nether-rarity:{item.ContentId}:{item.RarityLevel}");
            }

            if (equipmentOnly
                && item.RarityLevel >= GoldRarityLevel
                && item.RarityLevel <= RedRarityLevel
                && master.ContentRarity != item.RarityLevel)
            {
                return Error(
                    $"rarity-mismatch:{item.ContentId}:{item.RarityLevel}:{master.ContentRarity}"
                );
            }

            int effectiveRarity = item.RarityLevel;
            if (equipmentOnly
                && item.RarityLevel == (int)NetherSlTarget.NoEffect
                && (master.ContentRarity == (int)NetherSlTarget.Silver
                    || master.ContentRarity == (int)NetherSlTarget.Purple))
            {
                effectiveRarity = master.ContentRarity;
            }
            if ((target == NetherSlTarget.UniqueWeapon && item.RarityLevel != (int)NetherSlTarget.UniqueWeapon)
                || (target != NetherSlTarget.UniqueWeapon && effectiveRarity < (int)target))
                continue;

            targets.Add(
                new NetherTargetDrop(
                    item,
                    master,
                    NetherTargetReason.EquipmentStopCondition,
                    hasMaster,
                    effectiveRarity
                )
            );
            equipmentStopMatched = true;
        }

        bool stopConditionMatched = !hasPreserveRules
            ? equipmentStopMatched
            : preserveMode switch
            {
                NetherPreserveMode.AND => equipmentStopMatched && preservedItemMatched,
                NetherPreserveMode.OR => equipmentStopMatched || preservedItemMatched,
                _ => false,
            };

        return new NetherBattleDropEvaluation(targets, stopConditionMatched);
    }

    private const NetherWeaponTypeFilter AllWeaponTypes =
        NetherWeaponTypeFilter.OneHandSword
        | NetherWeaponTypeFilter.GreatSword
        | NetherWeaponTypeFilter.Fists
        | NetherWeaponTypeFilter.Bow
        | NetherWeaponTypeFilter.Gun
        | NetherWeaponTypeFilter.Staff
        | NetherWeaponTypeFilter.Grimoire
        | NetherWeaponTypeFilter.Pickel;

    private static bool IsValidWeaponTypeFilter(NetherWeaponTypeFilter weaponTypes) =>
        (weaponTypes & ~AllWeaponTypes) == NetherWeaponTypeFilter.Any;

    private static NetherWeaponTypeFilter ToWeaponTypeFilter(NetherWeaponType weaponType) =>
        weaponType switch
        {
            NetherWeaponType.OneHandSword => NetherWeaponTypeFilter.OneHandSword,
            NetherWeaponType.GreatSword => NetherWeaponTypeFilter.GreatSword,
            NetherWeaponType.Fists => NetherWeaponTypeFilter.Fists,
            NetherWeaponType.Bow => NetherWeaponTypeFilter.Bow,
            NetherWeaponType.Gun => NetherWeaponTypeFilter.Gun,
            NetherWeaponType.Staff => NetherWeaponTypeFilter.Staff,
            NetherWeaponType.Grimoire => NetherWeaponTypeFilter.Grimoire,
            NetherWeaponType.Pickel => NetherWeaponTypeFilter.Pickel,
            _ => NetherWeaponTypeFilter.Any,
        };

    public static NetherBattleDropEvaluation Error(string error) =>
        new(Array.Empty<NetherTargetDrop>(), false, error);
}
