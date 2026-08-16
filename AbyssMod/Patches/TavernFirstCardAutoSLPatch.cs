using AbyssMod.Services;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Project.Api;
using Il2CppVignetteList = Il2CppSystem.Collections.Generic.List<Project.Tavern.Top.VignetteData>;
using TavernGameViewController = Project.Tavern.Top.GameViewController;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class TavernFirstCardAutoSLPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(
        typeof(TavernGameViewController),
        TavernFirstCardHookPlan.InterceptionMethod
    )]
    private static bool CreateGameDataPrefix(
        TavernGameViewController __instance,
        Il2CppVignetteList vignetteIds,
        TavernExecWorkResponseEntity entity,
        long dailyId,
        ref UniTask __result
    )
    {
        if (TavernFirstCardAutoSL.IsNativeCreateGameDataInvocation)
            return true;
        if (!TavernFirstCardAutoSL.TryGetEnabledTarget(out var target, out string reason))
        {
            if (Config.BattleSessionAutoSL.Value && reason.StartsWith("invalid-"))
                Logger.Warn($"[F11][TavernAutoSL] bypassed: {reason}");
            return true;
        }
        if (entity?.tavern_daily_card == null)
        {
            Logger.Warn(
                "[F11][TavernAutoSL] pre-model CreateGameData bypassed: "
                    + "missing-tavern-daily-card"
            );
            return true;
        }

        Logger.Info(
            $"[F11][TavernAutoSL] pre-model CreateGameData intercepted, "
                + $"dailyCardId={dailyId}, "
                + $"useTicket={entity.tavern_daily_card.use_ticket != 0}, "
                + $"target={target.ToString().ToLowerInvariant()}"
        );
        __result = TavernFirstCardAutoSL.RunCreateGameData(
            __instance,
            vignetteIds,
            entity,
            dailyId
        );
        return false;
    }
}
