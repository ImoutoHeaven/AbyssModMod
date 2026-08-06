using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AbyssMod.Services;

public readonly record struct NetherItemMasterInfo(long ItemType, int ContentRarity);

public readonly record struct NetherTargetDrop(
    BattleDropItem Drop,
    NetherItemMasterInfo Master
);

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
    public bool ShouldRetry => Error.Length == 0 && Targets.Count == 0;

    public NetherBattleDropEvaluation(
        IReadOnlyList<NetherTargetDrop> targets,
        string error = ""
    )
    {
        Targets = targets;
        Error = error;
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
                .Append(" dropRarity=").Append(target.Drop.RarityLevel)
                .Append(" masterType=").Append(target.Master.ItemType)
                .Append(" masterRarity=").Append(target.Master.ContentRarity)
                .Append(" isRare=").Append(target.Drop.IsRare ? 1 : 0);
        }
        return builder.ToString();
    }
}

public static class NetherBattleAutoSLPolicy
{
    public const int NetherItemContentType = 31;
    public const int NetherEquipmentItemType = 91;
    public const int GoldRarityLevel = 3;

    public static NetherBattleDropEvaluation Evaluate(
        NetherBattleDropProbeReport report,
        IReadOnlyDictionary<long, NetherItemMasterInfo> masterItems
    )
    {
        if (report.Error.Length != 0)
            return Error(report.Error);
        if (masterItems == null || masterItems.Count == 0)
            return Error("missing-item-master");

        var targets = new List<NetherTargetDrop>();
        foreach (BattleDropItem item in report.EnemyItems)
        {
            if (!masterItems.TryGetValue(item.ContentId, out NetherItemMasterInfo master))
            {
                if (item.ContentType == NetherItemContentType)
                    return Error($"unresolved-nether-item-master:{item.ContentId}");
                continue;
            }

            if (master.ItemType != NetherEquipmentItemType
                || item.RarityLevel < GoldRarityLevel)
                continue;

            if (item.ContentType != NetherItemContentType)
                return Error($"content-type-mismatch:{item.ContentId}:{item.ContentType}");
            if (master.ContentRarity != item.RarityLevel)
                return Error(
                    $"rarity-mismatch:{item.ContentId}:{item.RarityLevel}:{master.ContentRarity}"
                );

            targets.Add(new NetherTargetDrop(item, master));
        }

        return new NetherBattleDropEvaluation(targets);
    }

    public static NetherBattleDropEvaluation Error(string error) =>
        new(Array.Empty<NetherTargetDrop>(), error);
}
