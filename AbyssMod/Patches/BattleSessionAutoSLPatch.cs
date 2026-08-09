using AbyssMod.Services;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Ingame.Disaster;
using Project.Ingame.Exploration;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class BattleSessionAutoSLPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ExplorationQuestPreserveAPIService),
        "Project_Ingame_Exploration_IExplorationQuestAPIService_StartQuestAsync"
    )]
    private static void ExplorationPreservePostfix(
        ExplorationQuestPreserveAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (__instance?._apiService == null)
            return;

        // StartDungeon awaits this wrapper immediately before InitModelsForDungeon.
        // Retries use the underlying service so they do not recursively re-enter this patch.
        NetherAPIService netherApiService = __instance._apiService.TryCast<NetherAPIService>();
        if (netherApiService != null)
        {
            if (Config.BattleSessionAutoSL.Value)
            {
                Logger.Info(
                    "[F11][NetherAutoSL] pre-model response intercepted; source=preserved"
                );
                __result = BattleSessionAutoSL.RunNether(
                    __instance._apiService,
                    netherApiService,
                    __result,
                    ct,
                    "preserved"
                );
            }

            // This outer task is the exact battle-scene request result (or the final F11
            // reroll task).  Capturing here keeps the F12 ingress transaction intact without
            // Harmony rewriting the crash-prone concrete NetherAPIService async method.
            NetherRuntimeBridge.ObserveBattleStartTask(__result);
            return;
        }

        if (!Config.BattleSessionAutoSL.Value)
            return;

        if (
            !BattleSessionAutoSLRoutingPolicy.ShouldInterceptExploration(
                __instance._apiService.TryCast<EncounterQuestAPIService>() != null
            )
        )
            return;

        Logger.Info(
            "[F11][BattleAutoSL] pre-model response intercepted; "
                + "mode=exploration, source=preserved"
        );
        __result = BattleSessionAutoSL.RunExploration(
            __instance._apiService,
            __result,
            ct,
            "preserved"
        );
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ResumedQuestAPIService),
        "Project_Ingame_Exploration_IExplorationQuestAPIService_StartQuestAsync"
    )]
    private static void ExplorationResumePostfix(
        ResumedQuestAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (!Config.BattleSessionAutoSL.Value || __instance?._apiService == null)
            return;

        if (
            !BattleSessionAutoSLRoutingPolicy.ShouldInterceptExploration(
                __instance._apiService.TryCast<EncounterQuestAPIService>() != null
            )
        )
            return;

        Logger.Info(
            "[F11][BattleAutoSL] pre-model response intercepted; "
                + "mode=exploration, source=resumed"
        );
        __result = BattleSessionAutoSL.RunExploration(
            __instance._apiService,
            __result,
            ct,
            "resumed"
        );
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(DisasterQuestPreserveAPIService),
        "Project_Ingame_Disaster_IDisasterQuestAPIService_StartQuestAsync"
    )]
    private static void DisasterPreservePostfix(
        DisasterQuestPreserveAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (!Config.BattleSessionAutoSL.Value || __instance?._apiService == null)
            return;

        Logger.Info(
            "[F11][BattleAutoSL] pre-model response intercepted; "
                + "mode=disaster, source=preserved"
        );
        __result = BattleSessionAutoSL.RunDisaster(
            __instance._apiService,
            __result,
            ct,
            "preserved"
        );
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ResumedDisasterQuestAPIService),
        "Project_Ingame_Disaster_IDisasterQuestAPIService_StartQuestAsync"
    )]
    private static void DisasterResumePostfix(
        ResumedDisasterQuestAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (!Config.BattleSessionAutoSL.Value || __instance?._apiService == null)
            return;

        Logger.Info(
            "[F11][BattleAutoSL] pre-model response intercepted; "
                + "mode=disaster, source=resumed"
        );
        __result = BattleSessionAutoSL.RunDisaster(
            __instance._apiService,
            __result,
            ct,
            "resumed"
        );
    }
}
