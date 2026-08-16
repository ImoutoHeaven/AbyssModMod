using AbyssMod.Services;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Tavern;

namespace AbyssMod.Patches;

[HarmonyPatch]
public static class TavernFirstCardAutoSLPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TavernApiService), nameof(TavernApiService.RequestExecWorkAsync))]
    private static void RequestExecWorkPostfix(
        long dailyCardId,
        bool useTicket,
        CancellationToken ct,
        ref UniTask<TavernExecWorkResponseEntity> __result
    )
    {
        if (TavernFirstCardAutoSL.IsReplayInvocation)
            return;
        if (!TavernFirstCardAutoSL.TryGetEnabledTarget(out var target, out string reason))
        {
            if (Config.BattleSessionAutoSL.Value && reason.StartsWith("invalid-"))
                Logger.Warn($"[F11][TavernAutoSL] bypassed: {reason}");
            return;
        }

        Logger.Info(
            $"[F11][TavernAutoSL] first exec/work response intercepted before card model, "
                + $"dailyCardId={dailyCardId}, useTicket={useTicket}, "
                + $"target={target.ToString().ToLowerInvariant()}"
        );
        __result = TavernFirstCardAutoSL.Run(
            dailyCardId,
            useTicket,
            __result,
            ct
        );
    }
}
