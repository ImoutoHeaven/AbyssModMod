#nullable enable

using System;
using System.Collections.Generic;
using Absf;
using Project.Master;
using Project.Master.NoaMessagePack;

namespace AbyssMod.Services;

internal static class NetherFloorEncounterCatalog
{
    private static IReadOnlyDictionary<long, int>? _floorTypes;

    public static bool TryGetRawFloorType(long mNetherMapFloorId, out int rawFloorType, out string error)
    {
        rawFloorType = 0;
        if (mNetherMapFloorId <= 0)
        {
            error = "invalid-nether-map-floor-id";
            return false;
        }

        if (_floorTypes == null && !TryLoad(out error))
            return false;

        if (!_floorTypes!.TryGetValue(mNetherMapFloorId, out rawFloorType))
        {
            error = $"missing-nether-map-floor:{mNetherMapFloorId}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryLoad(out string error)
    {
        try
        {
            MasterDataStore? masterDataStore = Engine.Get<MasterDataStore>();
            MNetherMapFloors[]? rows = masterDataStore?.GetCache<MNetherMapFloors>();
            if (rows == null || rows.Length == 0)
            {
                error = "missing-m-nether-map-floors-cache";
                return false;
            }

            var loaded = new Dictionary<long, int>();
            foreach (MNetherMapFloors row in rows)
            {
                if (row != null)
                    loaded[row.id] = row.type;
            }
            if (loaded.Count == 0)
            {
                error = "empty-m-nether-map-floors-cache";
                return false;
            }

            _floorTypes = loaded;
            error = string.Empty;
            Logger.Info($"[F11][NetherAutoSL] floor catalog loaded, floors={loaded.Count}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"nether-floor-master-load-error:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }
}
