using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AbyssMod.Services;

public readonly record struct BattleDropItem(
    long Sid,
    int ContentType,
    long ContentId,
    int Amount,
    int RarityLevel,
    bool IsRare
);

public sealed class BattleDropProbeReport
{
    public IReadOnlyList<BattleDropItem> Items { get; }
    public int DropCount => Items.Count;
    public int RootDropCount { get; }
    public int ExcludedInactiveDropCount { get; }
    public int RareDropCount { get; }
    public string Error { get; }

    public BattleDropProbeReport(
        IReadOnlyList<BattleDropItem> items,
        int rareDropCount,
        string error = "",
        int rootDropCount = -1,
        int excludedInactiveDropCount = 0
    )
    {
        Items = items;
        RootDropCount = rootDropCount < 0 ? items.Count : rootDropCount;
        ExcludedInactiveDropCount = excludedInactiveDropCount;
        RareDropCount = rareDropCount;
        Error = error;
    }

    public string FormatItemList()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < Items.Count; i++)
        {
            if (i > 0)
                builder.Append("; ");

            BattleDropItem item = Items[i];
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

public static class BattleSessionDropProbe
{
    public static BattleDropProbeReport Parse(string stageDetail)
    {
        if (string.IsNullOrWhiteSpace(stageDetail))
            return Missing();

        try
        {
            using JsonDocument document = JsonDocument.Parse(stageDetail);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ParseError();

            if (!document.RootElement.TryGetProperty("drops", out JsonElement drops)
                || drops.ValueKind != JsonValueKind.Array)
                return Missing();

            var items = new List<BattleDropItem>();
            foreach (JsonElement drop in drops.EnumerateArray())
            {
                if (drop.ValueKind != JsonValueKind.Object
                    || !TryReadLong(drop, "sid", out long sid)
                    || !TryReadInt(drop, "content_type", out int contentType)
                    || !TryReadLong(drop, "content_id", out long contentId)
                    || !TryReadInt(drop, "amount", out int amount)
                    || !TryReadInt(drop, "rarity_level", out int rarityLevel)
                    || !TryReadRareFlag(drop, out bool isRare))
                    return ParseError();

                items.Add(
                    new BattleDropItem(
                        sid,
                        contentType,
                        contentId,
                        amount,
                        rarityLevel,
                        isRare
                    )
                );
            }

            int rootDropCount = items.Count;
            ExplorationStageDropAnalysis reachability =
                ExplorationStageDropReachability.Parse(document.RootElement);
            int excludedInactiveDropCount = 0;
            if (reachability.InactiveDropSids.Count != 0)
            {
                excludedInactiveDropCount = items.RemoveAll(item =>
                    reachability.InactiveDropSids.Contains(item.Sid));
            }

            int rareCount = 0;
            foreach (BattleDropItem item in items)
            {
                if (item.IsRare)
                    rareCount++;
            }

            return new BattleDropProbeReport(
                items,
                rareCount,
                rootDropCount: rootDropCount,
                excludedInactiveDropCount: excludedInactiveDropCount
            );
        }
        catch (JsonException)
        {
            return ParseError();
        }
    }

    private static BattleDropProbeReport Missing() =>
        new(Array.Empty<BattleDropItem>(), 0, "missing");

    private static BattleDropProbeReport ParseError() =>
        new(Array.Empty<BattleDropItem>(), 0, "parse-error");

    private static bool TryReadLong(JsonElement element, string name, out long number)
    {
        if (element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number))
            return true;

        number = 0;
        return false;
    }

    private static bool TryReadInt(JsonElement element, string name, out int number)
    {
        if (element.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number))
            return true;

        number = 0;
        return false;
    }

    private static bool TryReadRareFlag(JsonElement element, out bool isRare)
    {
        isRare = false;
        if (!element.TryGetProperty("is_rare_drop", out JsonElement value))
            return false;

        if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
        {
            isRare = value.GetBoolean();
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            if (number != 0 && number != 1)
                return false;
            isRare = number == 1;
            return true;
        }

        return false;
    }
}
