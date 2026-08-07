#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>
/// Resolves the server-created offer list to the exact native detail index.  The Receive
/// callback confirms the UI's current selection; it must never be invoked against an implicit
/// first/default offer.
/// </summary>
internal static class NetherCodeOfferSelection
{
    public static bool TryResolveIndex(IReadOnlyList<long> offerIds, long selectedCodeId, out int index)
    {
        if (offerIds == null)
            throw new ArgumentNullException(nameof(offerIds));
        index = -1;
        if (selectedCodeId <= 0)
            return false;

        for (int candidate = 0; candidate < offerIds.Count; candidate++)
        {
            if (offerIds[candidate] != selectedCodeId)
                continue;
            if (index >= 0)
                return false;
            index = candidate;
        }
        return index >= 0;
    }
}
