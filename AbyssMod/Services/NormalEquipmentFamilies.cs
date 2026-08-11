using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

public readonly record struct NormalEquipmentMasterInfo(
    int ContentType,
    long ContentId,
    long GroupNo,
    int Rank,
    int Rarity,
    string Name
);

public sealed class NormalEquipmentMasterIndex
{
    private readonly IReadOnlyDictionary<(int ContentType, long ContentId), NormalEquipmentMasterInfo> _items;

    public int Count => _items.Count;

    public NormalEquipmentMasterIndex(IEnumerable<NormalEquipmentMasterInfo> items)
    {
        if (items == null)
            throw new ArgumentNullException(nameof(items));

        var loaded = new Dictionary<(int ContentType, long ContentId), NormalEquipmentMasterInfo>();
        foreach (NormalEquipmentMasterInfo item in items)
        {
            Validate(item);
            var key = (item.ContentType, item.ContentId);
            if (loaded.TryGetValue(key, out NormalEquipmentMasterInfo existing))
            {
                if (!existing.Equals(item))
                    throw new ArgumentException(
                        $"duplicate-normal-equipment-master:{item.ContentType}:{item.ContentId}",
                        nameof(items)
                    );
                continue;
            }
            loaded.Add(key, item);
        }

        _items = loaded;
    }

    public bool TryGet(
        int contentType,
        long contentId,
        out NormalEquipmentMasterInfo item
    ) => _items.TryGetValue((contentType, contentId), out item);

    public bool IsSameFamilyAtOrAbove(
        NormalEquipmentMasterInfo anchor,
        NormalEquipmentMasterInfo candidate
    ) => candidate.ContentType == anchor.ContentType
        && candidate.GroupNo == anchor.GroupNo
        && candidate.Rarity == anchor.Rarity
        && candidate.Rank >= anchor.Rank;

    public IReadOnlyList<NormalEquipmentMasterInfo> FindFamilyAtOrAbove(
        NormalEquipmentMasterInfo anchor
    ) => _items.Values
        .Where(candidate => IsSameFamilyAtOrAbove(anchor, candidate))
        .OrderBy(candidate => candidate.Rank)
        .ThenBy(candidate => candidate.ContentId)
        .ToArray();

    private static void Validate(NormalEquipmentMasterInfo item)
    {
        if (!NormalExactDropTarget.TryFormatTypeName(item.ContentType, out _)
            || item.ContentId <= 0
            || item.GroupNo <= 0
            || item.Rank <= 0
            || item.Rarity <= 0)
        {
            throw new ArgumentException(
                $"invalid-normal-equipment-master:{item.ContentType}:{item.ContentId}:"
                    + $"{item.GroupNo}:{item.Rank}:{item.Rarity}",
                nameof(item)
            );
        }
    }
}
