using AbyssMod.Services;
using HarmonyLib;
using Project.Api;
using Project.BattleResult;
using Project.Ingame.Disaster;
using Project.Ingame.Exploration;

namespace AbyssMod.Patches;

/// <summary>
/// Logs the exact stage_results object generated for battle settlement. This
/// is the client-side payload fragment that contains the authoritative drop SID list.
/// </summary>
[HarmonyPatch]
public static class BattleSettlementPayloadProbePatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ExplorationBattleEndRecord), nameof(ExplorationBattleEndRecord.CreateStageResults))]
    private static void ExplorationPostfix(ExplorationStageResults __result)
    {
        if (!Config.BattleSessionAutoSL.Value && !Config.BattleSessionProbe.Value)
            return;

        BattleSettlementPayloadTrace.LogExplorationStageResults(__result);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(DisasterBattleEndRecord), nameof(DisasterBattleEndRecord.CreateStageResults))]
    private static void DisasterPostfix(DisasterStageResults __result)
    {
        if (!Config.BattleSessionAutoSL.Value && !Config.BattleSessionProbe.Value)
            return;

        BattleSettlementPayloadTrace.LogDisasterStageResults(__result);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BattleResultUtility), nameof(BattleResultUtility.CreateBattleResultModel))]
    private static void ResultModelPrefix(IFinishQuestResponseEntity entity)
    {
        if (!Config.BattleSessionAutoSL.Value && !Config.BattleSessionProbe.Value)
            return;

        BattleSettlementPayloadTrace.LogFinishResponse(entity);
    }
}
