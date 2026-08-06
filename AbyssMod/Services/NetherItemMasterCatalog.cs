using System;
using System.Collections.Generic;
using Absf;
using Project.Master;
using Project.Master.NoaMessagePack;

namespace AbyssMod.Services;

internal static class NetherItemMasterCatalog
{
    private static IReadOnlyDictionary<long, NetherItemMasterInfo> _items;

    public static bool TryGet(
        out IReadOnlyDictionary<long, NetherItemMasterInfo> items,
        out string error
    )
    {
        if (_items != null)
        {
            items = _items;
            error = string.Empty;
            return true;
        }

        try
        {
            MasterDataStore masterDataStore = Engine.Get<MasterDataStore>();
            var rows = masterDataStore?.GetCache<MItems>();
            if (rows == null || rows.Length == 0)
            {
                items = null;
                error = "missing-m-items-cache";
                return false;
            }

            var loaded = new Dictionary<long, NetherItemMasterInfo>();
            int netherEquipmentCount = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                MItems row = rows[i];
                if (row == null)
                    continue;

                loaded[row.id] = new NetherItemMasterInfo(row.type, row.rarity);
                if (row.type == NetherBattleAutoSLPolicy.NetherEquipmentItemType)
                    netherEquipmentCount++;
            }

            if (netherEquipmentCount == 0)
            {
                items = null;
                error = "missing-nether-equipment-master";
                return false;
            }

            _items = loaded;
            items = _items;
            error = string.Empty;
            Logger.Info(
                $"[F11][NetherAutoSL] master catalog loaded, "
                    + $"items={loaded.Count}, netherEquipment={netherEquipmentCount}"
            );
            return true;
        }
        catch (Exception ex)
        {
            items = null;
            error = $"master-load-error:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }
}
