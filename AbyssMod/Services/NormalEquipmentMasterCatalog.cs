using System;
using System.Collections.Generic;
using Absf;
using Project.Master;
using Project.Master.NoaMessagePack;

namespace AbyssMod.Services;

internal static class NormalEquipmentMasterCatalog
{
    private static NormalEquipmentMasterIndex _index;

    public static bool TryGet(
        out NormalEquipmentMasterIndex index,
        out string error
    )
    {
        if (_index != null)
        {
            index = _index;
            error = string.Empty;
            return true;
        }

        try
        {
            MasterDataStore store = Engine.Get<MasterDataStore>();
            MWeapons[] weapons = store?.GetCache<MWeapons>();
            MArmors[] armors = store?.GetCache<MArmors>();
            MAccessories[] accessories = store?.GetCache<MAccessories>();
            if (weapons == null || weapons.Length == 0)
                return Missing(out index, out error, "missing-m-weapons-cache");
            if (armors == null || armors.Length == 0)
                return Missing(out index, out error, "missing-m-armors-cache");
            if (accessories == null || accessories.Length == 0)
                return Missing(out index, out error, "missing-m-accessories-cache");

            var rows = new List<NormalEquipmentMasterInfo>(
                weapons.Length + armors.Length + accessories.Length
            );
            for (int i = 0; i < weapons.Length; i++)
            {
                MWeapons row = weapons[i];
                if (row != null)
                    rows.Add(
                        new NormalEquipmentMasterInfo(
                            BattleSessionAutoSLPolicy.WeaponContentType,
                            row.id,
                            row.group_no,
                            row.rank,
                            row.rarity,
                            row.name ?? string.Empty
                        )
                    );
            }
            for (int i = 0; i < armors.Length; i++)
            {
                MArmors row = armors[i];
                if (row != null)
                    rows.Add(
                        new NormalEquipmentMasterInfo(
                            BattleSessionAutoSLPolicy.ArmorContentType,
                            row.id,
                            row.group_no,
                            row.rank,
                            row.rarity,
                            row.name ?? string.Empty
                        )
                    );
            }
            for (int i = 0; i < accessories.Length; i++)
            {
                MAccessories row = accessories[i];
                if (row != null)
                    rows.Add(
                        new NormalEquipmentMasterInfo(
                            BattleSessionAutoSLPolicy.AccessoryContentType,
                            row.id,
                            row.group_no,
                            row.rank,
                            row.rarity,
                            row.name ?? string.Empty
                        )
                    );
            }

            var loaded = new NormalEquipmentMasterIndex(rows);
            if (loaded.Count != rows.Count)
                return Missing(out index, out error, "duplicate-normal-equipment-master-rows");

            _index = loaded;
            index = _index;
            error = string.Empty;
            Logger.Info(
                $"[F11][BattleAutoSL] normal equipment master catalog loaded, "
                    + $"weapons={weapons.Length}, armors={armors.Length}, "
                    + $"accessories={accessories.Length}, total={loaded.Count}"
            );
            return true;
        }
        catch (Exception ex)
        {
            index = null;
            error = $"normal-equipment-master-load-error:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    private static bool Missing(
        out NormalEquipmentMasterIndex index,
        out string error,
        string detail
    )
    {
        index = null;
        error = detail;
        return false;
    }
}
