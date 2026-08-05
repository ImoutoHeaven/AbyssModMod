using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Ingame.Disaster;
using Project.Ingame.Exploration;
using AbyssMod.Services;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class BattleSessionAutoSLPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ResumedQuestAPIService),
        "Project_Ingame_Exploration_IExplorationQuestAPIService_StartQuestAsync"
    )]
    private static void ExplorationPostfix(
        ResumedQuestAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (!Config.BattleSessionAutoSL.Value || __instance?._apiService == null)
            return;

        __result = BattleSessionAutoSL.RunExploration(
            __instance,
            __result,
            ct
        );
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ResumedDisasterQuestAPIService),
        "Project_Ingame_Disaster_IDisasterQuestAPIService_StartQuestAsync"
    )]
    private static void DisasterPostfix(
        ResumedDisasterQuestAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (!Config.BattleSessionAutoSL.Value || __instance?._apiService == null)
            return;

        __result = BattleSessionAutoSL.RunDisaster(
            __instance,
            __result,
            ct
        );
    }
}
