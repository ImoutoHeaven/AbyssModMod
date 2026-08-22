using System;
using System.Collections.Generic;
using Absf;
using Project.ContentInfoProvider;
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

            NetherItemInfoProvider weaponTypeProvider = null;
            string weaponTypeProviderError = string.Empty;
            try
            {
                weaponTypeProvider = new NetherItemInfoProvider();
            }
            catch (Exception ex)
            {
                weaponTypeProviderError = $"{ex.GetType().Name}:{ex.Message}";
            }

            var loaded = new Dictionary<long, NetherItemMasterInfo>();
            int netherEquipmentCount = 0;
            int unresolvedWeaponTypeCount = 0;
            try
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    MItems row = rows[i];
                    if (row == null)
                        continue;

                    NetherWeaponType weaponType = NetherWeaponType.Unknown;
                    if (row.type == NetherBattleAutoSLPolicy.NetherEquipmentItemType)
                    {
                        netherEquipmentCount++;
                        if (weaponTypeProvider != null)
                        {
                            try
                            {
                                weaponType = ToWeaponType(
                                    weaponTypeProvider.GetEquipmentType(row.id)
                                );
                            }
                            catch (Exception ex)
                            {
                                if (weaponTypeProviderError.Length == 0)
                                    weaponTypeProviderError = $"{ex.GetType().Name}:{ex.Message}";
                            }
                        }

                        if (weaponType == NetherWeaponType.Unknown)
                            unresolvedWeaponTypeCount++;
                    }

                    loaded[row.id] = new NetherItemMasterInfo(row.type, row.rarity, weaponType);
                }
            }
            finally
            {
                try
                {
                    weaponTypeProvider?.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Warn(
                        $"[F11][NetherAutoSL] weapon-type provider dispose failed: "
                            + $"{ex.GetType().Name}:{ex.Message}"
                    );
                }
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
            if (weaponTypeProviderError.Length != 0)
            {
                Logger.Warn(
                    $"[F11][NetherAutoSL] weapon-type provider unavailable: "
                        + weaponTypeProviderError
                );
            }
            Logger.Info(
                $"[F11][NetherAutoSL] master catalog loaded, "
                    + $"items={loaded.Count}, netherEquipment={netherEquipmentCount}, "
                    + $"unresolvedWeaponTypes={unresolvedWeaponTypeCount}"
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

    private static NetherWeaponType ToWeaponType(EquipmentType equipmentType) =>
        equipmentType switch
        {
            EquipmentType.OneHandSword => NetherWeaponType.OneHandSword,
            EquipmentType.GreatSword => NetherWeaponType.GreatSword,
            EquipmentType.Fists => NetherWeaponType.Fists,
            EquipmentType.Bow => NetherWeaponType.Bow,
            EquipmentType.Gun => NetherWeaponType.Gun,
            EquipmentType.Staff => NetherWeaponType.Staff,
            EquipmentType.Grimoire => NetherWeaponType.Grimoire,
            EquipmentType.Pickel => NetherWeaponType.Pickel,
            _ => NetherWeaponType.Unknown,
        };
}
