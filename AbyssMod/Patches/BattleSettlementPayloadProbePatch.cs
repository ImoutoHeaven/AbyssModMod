using Absf;
using AbyssMod.Services;
using HarmonyLib;
using Project.Api;
using Project.BattleResult;
using Project.Ingame;
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
    private static void ResultModelPrefix(
        BattleResultType resultType,
        ISceneTransitionParam startParam,
        IFinishQuestResponseEntity entity
    )
    {
        string startParamType = startParam?.GetType().FullName ?? string.Empty;
        int battleQuestType = entity == null ? 0 : (int)entity.QuestType;
        NetherBattleTerminalKind terminal = NetherBattleTerminalObservationPolicy.Classify(
            battleQuestType,
            (int)resultType
        );
        switch (terminal)
        {
            case NetherBattleTerminalKind.Clear:
                NetherRuntimeBridge.ObserveBattleClear();
                break;
            case NetherBattleTerminalKind.Close:
                NetherRuntimeBridge.ObserveBattleClose();
                break;
        }

        // Log every authoritative Nether result, including an unrecognized result enum.  A
        // future game version must leave enough evidence to distinguish a new terminal value
        // from a missing Harmony callback without relying on the unstable startParam wrapper.
        if (battleQuestType == NetherBattleTerminalObservationPolicy.NetherBattleQuestType)
        {
            NetherAutoClimbController.LogDiagnostic(
                "runtime-lifecycle",
                new("action", "battle-result-terminal-observed"),
                new("source", "BattleResultUtility.CreateBattleResultModel"),
                new("questType", entity!.QuestType.ToString()),
                new("questTypeValue", battleQuestType.ToString()),
                new("resultType", resultType.ToString()),
                new("resultTypeValue", ((int)resultType).ToString()),
                new("terminal", terminal.ToString()),
                new("startParamType", startParamType),
                new("responseType", entity?.GetType().FullName ?? "none")
            );
        }

        if (Config.BattleSessionAutoSL.Value || Config.BattleSessionProbe.Value)
            BattleSettlementPayloadTrace.LogFinishResponse(entity);
    }
}
