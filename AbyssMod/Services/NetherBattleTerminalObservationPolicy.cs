#nullable enable

using System;

namespace AbyssMod.Services;

internal enum NetherBattleTerminalKind
{
    None = 0,
    Clear = 1,
    Close = 2,
}

/// <summary>
/// Classifies the result path that BattleResultUtility has already awaited before it creates
/// the result model. Retire uses SurrenderQuestAsync, so it is deliberately not reported as
/// either the clear or close task that the settlement coordinator knows how to reconcile.
/// </summary>
internal static class NetherBattleTerminalObservationPolicy
{
    internal const int NetherBattleQuestType = 110;

    internal static NetherBattleTerminalKind Classify(
        int battleQuestType,
        int battleResultType
    )
    {
        // IFinishQuestResponseEntity.QuestType is the authoritative response discriminator.
        // The live IL2CPP wrapper returned by startParam.GetType() is not guaranteed to retain
        // cpp2il's nested NetherParam source spelling, and therefore cannot gate settlement.
        if (battleQuestType != NetherBattleQuestType)
            return NetherBattleTerminalKind.None;

        return battleResultType switch
        {
            1 or 5 => NetherBattleTerminalKind.Clear,
            2 or 4 => NetherBattleTerminalKind.Close,
            _ => NetherBattleTerminalKind.None,
        };
    }
}
