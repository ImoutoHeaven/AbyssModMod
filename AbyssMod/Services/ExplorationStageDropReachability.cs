using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AbyssMod.Services;

public sealed class ExplorationTreasureChest
{
    public long FloorSid { get; }
    public long ResourceSid { get; }
    public string AssetId { get; }
    public IReadOnlyList<long> DropSids { get; }

    internal ExplorationTreasureChest(
        long floorSid,
        long resourceSid,
        string assetId,
        IReadOnlyList<long> dropSids
    )
    {
        FloorSid = floorSid;
        ResourceSid = resourceSid;
        AssetId = assetId;
        DropSids = dropSids;
    }
}

public sealed class ExplorationStageDropAnalysis
{
    public IReadOnlySet<long> InactiveDropSids { get; }
    public IReadOnlyList<ExplorationTreasureChest> ActiveTreasureChests { get; }

    internal ExplorationStageDropAnalysis(
        IReadOnlySet<long> inactiveDropSids,
        IReadOnlyList<ExplorationTreasureChest> activeTreasureChests
    )
    {
        InactiveDropSids = inactiveDropSids;
        ActiveTreasureChests = activeTreasureChests;
    }

    public IReadOnlyList<ExplorationTreasureChest> FindActiveTargetChests(
        IReadOnlyCollection<long> targetSids
    )
    {
        if (targetSids == null || targetSids.Count == 0)
            return Array.Empty<ExplorationTreasureChest>();

        var targets = new HashSet<long>(targetSids);
        return ActiveTreasureChests
            .Where(chest => chest.DropSids.Any(targets.Contains))
            .ToArray();
    }
}

public sealed class ExplorationTreasureDropCompletion
{
    public IReadOnlyList<long> DropSids { get; }
    public IReadOnlyList<long> AddedDropSids { get; }
    public IReadOnlyList<long> CompletedResourceSids { get; }

    internal ExplorationTreasureDropCompletion(
        IReadOnlyList<long> dropSids,
        IReadOnlyList<long> addedDropSids,
        IReadOnlyList<long> completedResourceSids
    )
    {
        DropSids = dropSids;
        AddedDropSids = addedDropSids;
        CompletedResourceSids = completedResourceSids;
    }
}

public static class ExplorationTreasureDropCompleter
{
    public static ExplorationTreasureDropCompletion Complete(
        IReadOnlyList<long> existingDropSids,
        IReadOnlyList<long> passedFloorSids,
        IReadOnlyList<ExplorationTreasureChest> targetChests
    )
    {
        var completed = new List<long>(existingDropSids ?? Array.Empty<long>());
        var seen = new HashSet<long>(completed);
        var passedFloors = new HashSet<long>(passedFloorSids ?? Array.Empty<long>());
        var added = new List<long>();
        var completedResources = new List<long>();

        foreach (ExplorationTreasureChest chest in targetChests
            ?? Array.Empty<ExplorationTreasureChest>())
        {
            if (!passedFloors.Contains(chest.FloorSid))
                continue;

            int addedBefore = added.Count;
            foreach (long sid in chest.DropSids)
            {
                if (!seen.Add(sid))
                    continue;
                completed.Add(sid);
                added.Add(sid);
            }
            if (added.Count != addedBefore)
                completedResources.Add(chest.ResourceSid);
        }

        return new ExplorationTreasureDropCompletion(
            completed,
            added,
            completedResources
        );
    }
}

public static class ExplorationStageDropReachability
{
    private const int TreasureBoxRoomRole = 201;
    private const int SeamlessBattleRole = 304;
    private const string GoldBoxAssetId = "BoxGold";

    public static ExplorationStageDropAnalysis Parse(string stageDetail)
    {
        if (string.IsNullOrWhiteSpace(stageDetail))
            return Empty();

        try
        {
            using JsonDocument document = JsonDocument.Parse(stageDetail);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    public static ExplorationStageDropAnalysis Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("floor_parts", out JsonElement floorParts)
            || floorParts.ValueKind != JsonValueKind.Array)
            return Empty();

        var enemyDrops = ReadOwnedDrops(root, "enemies");
        var resources = ReadResources(root);
        var allReferenced = new HashSet<long>();
        foreach (IReadOnlyList<long> drops in enemyDrops.Values)
            allReferenced.UnionWith(drops);
        foreach (ResourceDrops resource in resources.Values)
            allReferenced.UnionWith(resource.DropSids);

        var activeGroups = new HashSet<int>();
        var activeReferenced = new HashSet<long>();
        var activeChests = new List<ExplorationTreasureChest>();
        foreach (JsonElement floorPart in floorParts.EnumerateArray())
        {
            if (floorPart.ValueKind != JsonValueKind.Object)
                continue;

            int forkGroup = ReadInt(floorPart, "fork_group_no");
            bool isActive = forkGroup == 0 || activeGroups.Contains(forkGroup);
            if (!isActive)
                continue;

            AddFloorEnemyDrops(floorPart, enemyDrops, activeReferenced);
            AddFloorResourceDrops(
                floorPart,
                resources,
                activeReferenced,
                activeChests
            );

            if (ReadInt(floorPart, "role") == SeamlessBattleRole
                && TryReadNestedInt(
                    floorPart,
                    "role_option",
                    "seamless_battle",
                    "fork_group_no",
                    out int activatedGroup
                )
                && activatedGroup != 0)
                activeGroups.Add(activatedGroup);
        }

        allReferenced.ExceptWith(activeReferenced);
        return new ExplorationStageDropAnalysis(allReferenced, activeChests);
    }

    private static ExplorationStageDropAnalysis Empty() =>
        new(new HashSet<long>(), Array.Empty<ExplorationTreasureChest>());

    private static Dictionary<long, IReadOnlyList<long>> ReadOwnedDrops(
        JsonElement root,
        string collectionName
    )
    {
        var result = new Dictionary<long, IReadOnlyList<long>>();
        if (!root.TryGetProperty(collectionName, out JsonElement owners)
            || owners.ValueKind != JsonValueKind.Array)
            return result;

        foreach (JsonElement owner in owners.EnumerateArray())
        {
            if (owner.ValueKind != JsonValueKind.Object
                || !TryReadLong(owner, "sid", out long sid))
                continue;
            result[sid] = ReadLongArray(owner, "drops");
        }
        return result;
    }

    private static Dictionary<long, ResourceDrops> ReadResources(JsonElement root)
    {
        var result = new Dictionary<long, ResourceDrops>();
        if (!root.TryGetProperty("resources", out JsonElement resources)
            || resources.ValueKind != JsonValueKind.Array)
            return result;

        foreach (JsonElement resource in resources.EnumerateArray())
        {
            if (resource.ValueKind != JsonValueKind.Object
                || !TryReadLong(resource, "sid", out long sid))
                continue;
            string assetId = resource.TryGetProperty("asset_id", out JsonElement asset)
                && asset.ValueKind == JsonValueKind.String
                    ? asset.GetString() ?? string.Empty
                    : string.Empty;
            result[sid] = new ResourceDrops(assetId, ReadLongArray(resource, "drops"));
        }
        return result;
    }

    private static void AddFloorEnemyDrops(
        JsonElement floorPart,
        IReadOnlyDictionary<long, IReadOnlyList<long>> enemyDrops,
        HashSet<long> activeDrops
    )
    {
        if (!floorPart.TryGetProperty("enemy_groups", out JsonElement groups)
            || groups.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement group in groups.EnumerateArray())
        {
            foreach (long enemySid in ReadLongArray(group, "enemies"))
            {
                if (enemyDrops.TryGetValue(enemySid, out var drops))
                    activeDrops.UnionWith(drops);
            }
        }
    }

    private static void AddFloorResourceDrops(
        JsonElement floorPart,
        IReadOnlyDictionary<long, ResourceDrops> resources,
        HashSet<long> activeDrops,
        List<ExplorationTreasureChest> activeChests
    )
    {
        long floorSid = TryReadLong(floorPart, "sid", out long sid) ? sid : 0;
        bool isTreasureRoom = ReadInt(floorPart, "role") == TreasureBoxRoomRole;
        foreach (long resourceSid in ReadLongArray(floorPart, "resources"))
        {
            if (!resources.TryGetValue(resourceSid, out var resource))
                continue;
            activeDrops.UnionWith(resource.DropSids);
            if (isTreasureRoom
                && resource.AssetId.Equals(GoldBoxAssetId, StringComparison.Ordinal))
            {
                activeChests.Add(
                    new ExplorationTreasureChest(
                        floorSid,
                        resourceSid,
                        resource.AssetId,
                        resource.DropSids
                    )
                );
            }
        }
    }

    private static IReadOnlyList<long> ReadLongArray(JsonElement parent, string name)
    {
        var values = new List<long>();
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
                values.Add(number);
        }
        return values;
    }

    private static int ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int number)
            ? number
            : 0;

    private static bool TryReadLong(JsonElement parent, string name, out long number)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out number))
            return true;
        number = 0;
        return false;
    }

    private static bool TryReadNestedInt(
        JsonElement parent,
        string first,
        string second,
        string name,
        out int number
    )
    {
        number = 0;
        return parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(first, out JsonElement firstValue)
            && firstValue.ValueKind == JsonValueKind.Object
            && firstValue.TryGetProperty(second, out JsonElement secondValue)
            && secondValue.ValueKind == JsonValueKind.Object
            && secondValue.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out number);
    }

    private sealed class ResourceDrops
    {
        public string AssetId { get; }
        public IReadOnlyList<long> DropSids { get; }

        public ResourceDrops(string assetId, IReadOnlyList<long> dropSids)
        {
            AssetId = assetId;
            DropSids = dropSids;
        }
    }
}
