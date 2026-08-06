using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Ingame.Disaster;
using Project.Ingame.BattleSceneTransitionStrategy;
using Project.Ingame.Exploration;
using AbyssMod.Services;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class BattleSessionAutoSLPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(NetherBattleSceneTransitionStrategy), "TransitionTask")]
    private static void NetherResumeTransitionPrefix()
    {
        if (!Config.BattleSessionAutoSL.Value)
        {
            NetherResumeAutoSLGate.Disarm();
            return;
        }

        NetherResumeAutoSLGate.Arm();
        Logger.Info("[F11][NetherAutoSL] interruption resume armed");
    }

    [HarmonyPostfix]
    [HarmonyPatch(
        typeof(ExplorationQuestPreserveAPIService),
        "Project_Ingame_Exploration_IExplorationQuestAPIService_StartQuestAsync"
    )]
    private static void NetherPostfix(
        ExplorationQuestPreserveAPIService __instance,
        CancellationToken ct,
        ref UniTask<BattleSessionStatusResponseEntity> __result
    )
    {
        if (__instance?._apiService == null)
            return;

        NetherAPIService netherApiService = __instance._apiService.TryCast<NetherAPIService>();
        if (netherApiService == null)
            return;

        if (!NetherResumeAutoSLGate.TryConsume())
            return;

        if (!Config.BattleSessionAutoSL.Value)
        {
            Logger.Info("[F11][NetherAutoSL] resume gate consumed while disabled");
            return;
        }

        Logger.Info("[F11][NetherAutoSL] preserve response intercepted; auto-SL started");

        __result = BattleSessionAutoSL.RunNether(
            __instance,
            netherApiService,
            __result,
            ct
        );
    }

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
