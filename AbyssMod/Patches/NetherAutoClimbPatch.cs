#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using AbyssMod.Services;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AbyssMod.Patches;

/// <summary>
/// Lifecycle-only instrumentation for F12.  It never calls bridge actions from a Harmony
/// callback; the main-thread coordinator consumes these registrations on a later Update.
/// </summary>
[HarmonyPatch]
internal static class NetherAutoClimbPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => NetherRuntimeBridge.GetPatchTargets();

    [HarmonyPostfix]
    private static void Postfix(MethodBase __originalMethod, object __instance, object[] __args)
    {
        try
        {
            NetherRuntimeBridge.ObservePatchedCall(__originalMethod, __instance, __args);
        }
        catch (Exception ex)
        {
            Logger.Error("[F12][NetherClimb] native lifecycle observation failed: " + ex);
        }
    }
}

/// <summary>
/// Result construction itself is the native Result request flow.  Keeping this patch separate
/// lets Harmony pass the returned UniTask so F12 can wait for success rather than completing
/// merely because the Result scene was opened.
/// </summary>
[HarmonyPatch]
internal static class NetherAutoClimbResultPatch
{
    private static MethodBase? TargetMethod()
    {
        Type? type = AccessTools.TypeByName("Project.NetherTop.Result.SubViewController");
        Type? partyCharacterType = AccessTools.TypeByName(
            "Project.NetherTop.Result.NetherResultPartyCharacterModel"
        );
        if (type == null || partyCharacterType == null)
            return null;
        Type partyCharacterArrayType = typeof(Il2CppReferenceArray<>).MakeGenericType(partyCharacterType);

        foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!string.Equals(method.Name, "CreateNetherResultModelAsync", StringComparison.Ordinal))
                continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2
                || parameters[0].ParameterType != typeof(bool)
                || parameters[1].ParameterType != partyCharacterArrayType
                || method.ReturnType != typeof(UniTask))
            {
                continue;
            }
            return method;
        }
        return null;
    }

    [HarmonyPostfix]
    private static void Postfix(ref UniTask __result)
    {
        try
        {
            // Observation only: the coordinator polls this task later from Hotkey.Update.
            NetherRuntimeBridge.ObserveResult(__result);
        }
        catch (Exception ex)
        {
            Logger.Error("[F12][NetherClimb] native result observation failed: " + ex);
        }
    }
}
