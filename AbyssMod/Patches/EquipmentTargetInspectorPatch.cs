using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AbyssMod.Services;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Project.Common;
using Project.Outgame;
using UnityEngine;

namespace AbyssMod.Patches;

internal static class QuestPreviewEquipmentCallbackPatchShared
{
    internal static IEnumerable<MethodBase> TargetMethods(int actionParameterIndex)
    {
        Assembly projectAssembly = typeof(EquipmentDropPopup).Assembly;
        foreach (
            QuestPreviewBindingDescriptor binding in QuestPreviewBindingCatalog.Bindings.Where(
                binding => binding.ActionParameterIndex == actionParameterIndex
            )
        )
        {
            Type controllerType = projectAssembly.GetType(binding.TypeName, false);
            if (controllerType == null)
            {
                Logger.Warn(
                    $"[F6][EquipmentTarget][Binding] outcome=missing-type type={binding.TypeName}"
                );
                continue;
            }

            MethodInfo selected = ResolveInitializer(controllerType, binding);
            if (selected == null)
            {
                Logger.Warn(
                    $"[F6][EquipmentTarget][Binding] outcome=missing-method type={binding.TypeName} "
                        + $"preferred={binding.MethodName} callbackIndex={binding.ActionParameterIndex} "
                        + "parameter=Il2CppSystem.Action<Project.Common.IContentModel>"
                );
                continue;
            }

            Logger.Info(
                $"[F6][EquipmentTarget][Binding] outcome=bound type={binding.TypeName} "
                    + $"method={selected.Name}"
            );
            yield return selected;
        }
    }

    internal static void Wrap(
        ref Il2CppSystem.Action<IContentModel> callback,
        MethodBase originalMethod
    )
    {
        if (callback == null)
            return;

        Il2CppSystem.Action<IContentModel> original = callback;
        string source = originalMethod?.DeclaringType?.FullName + "." + originalMethod?.Name;
        try
        {
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<IContentModel>>(
                new System.Action<IContentModel>(
                    contentModel => InvokeOriginalWithIntent(original, contentModel, source)
                )
            );
        }
        catch (Exception ex)
        {
            Logger.Warn(
                $"[F6][EquipmentTarget][Binding] outcome=callback-wrap-failed source={source} "
                    + $"error={ex.GetType().Name}:{ex.Message}"
            );
            callback = original;
        }
    }

    private static void InvokeOriginalWithIntent(
        Il2CppSystem.Action<IContentModel> original,
        IContentModel contentModel,
        string source
    )
    {
        try
        {
            int contentType = (int)contentModel.ContentType;
            long contentId = contentModel.ContentId;
            bool recorded = PreviewEquipmentTargetInspector.Shared.RecordQuestPreviewIntent(
                contentType,
                contentId,
                Time.realtimeSinceStartup
            );
            if (recorded)
            {
                var target = new NormalExactDropTarget(contentType, contentId);
                Logger.Info(
                    $"[F6][EquipmentTarget][Diag] event=quest-preview-intent "
                        + $"source={source} token={target.Token}"
                );
            }
        }
        catch (Exception ex)
        {
            PreviewEquipmentTargetInspector.Shared.Clear();
            Logger.Warn(
                $"[F6][EquipmentTarget][Diag] event=quest-preview-intent outcome=error "
                    + $"source={source} error={ex.GetType().Name}:{ex.Message}"
            );
        }

        // Preserve the game's exact callback. The previous implementation patched that
        // private IL2CPP callback itself; Harmony's generated original called
        // il2cpp_runtime_invoke and re-entered the native detour until stack overflow.
        original.Invoke(contentModel);
    }

    private static MethodInfo ResolveInitializer(
        Type popupType,
        QuestPreviewBindingDescriptor binding
    )
    {
        MethodInfo[] candidates = popupType
            .GetMethods(
                BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly
            )
            .Where(method => method.Name.Equals(binding.MethodName, StringComparison.Ordinal))
            .Where(method => method.ReturnType == typeof(void))
            .Where(
                method => HasContentCallbackAt(method, binding.ActionParameterIndex)
            )
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool HasContentCallbackAt(MethodInfo method, int index)
    {
        ParameterInfo[] parameters = method.GetParameters();
        return index >= 0
            && index < parameters.Length
            && parameters[index].ParameterType
                == typeof(Il2CppSystem.Action<IContentModel>);
    }
}

[HarmonyPatch]
public static class QuestPreviewEquipmentCallback0Patch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        QuestPreviewEquipmentCallbackPatchShared.TargetMethods(0);

    [HarmonyPrefix]
    private static void Prefix(
        ref Il2CppSystem.Action<IContentModel> __0,
        MethodBase __originalMethod
    ) => QuestPreviewEquipmentCallbackPatchShared.Wrap(ref __0, __originalMethod);
}

[HarmonyPatch]
public static class QuestPreviewEquipmentCallback1Patch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        QuestPreviewEquipmentCallbackPatchShared.TargetMethods(1);

    [HarmonyPrefix]
    private static void Prefix(
        ref Il2CppSystem.Action<IContentModel> __1,
        MethodBase __originalMethod
    ) => QuestPreviewEquipmentCallbackPatchShared.Wrap(ref __1, __originalMethod);
}

[HarmonyPatch]
public static class QuestPreviewEquipmentCallback2Patch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        QuestPreviewEquipmentCallbackPatchShared.TargetMethods(2);

    [HarmonyPrefix]
    private static void Prefix(
        ref Il2CppSystem.Action<IContentModel> __2,
        MethodBase __originalMethod
    ) => QuestPreviewEquipmentCallbackPatchShared.Wrap(ref __2, __originalMethod);
}

[HarmonyPatch]
public static class QuestPreviewEquipmentCallback3Patch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        QuestPreviewEquipmentCallbackPatchShared.TargetMethods(3);

    [HarmonyPrefix]
    private static void Prefix(
        ref Il2CppSystem.Action<IContentModel> __3,
        MethodBase __originalMethod
    ) => QuestPreviewEquipmentCallbackPatchShared.Wrap(ref __3, __originalMethod);
}

[HarmonyPatch(typeof(EquipmentDropPopup), nameof(EquipmentDropPopup.Setup))]
public static class QuestPreviewEquipmentDropPopupPatch
{
    private const int IgnoredPopupLogLimit = 8;
    private static int _ignoredPopupLogs;

    [HarmonyPostfix]
    private static void Postfix(
        EquipmentDropPopup __instance,
        EquipmentDropPopupModel __0
    )
    {
        try
        {
            PreviewEquipmentTargetSnapshot snapshot = CreateSnapshot(__instance, __0);
            if (snapshot == null)
            {
                PreviewEquipmentTargetInspector.Shared.Clear();
                LogIgnored("invalid-popup-model", "none");
                return;
            }

            bool registered = PreviewEquipmentTargetInspector.Shared.TryRegisterPopup(
                __instance,
                snapshot,
                Time.realtimeSinceStartup,
                IsActivePopup
            );
            if (registered)
            {
                Logger.Info(
                    $"[F6][EquipmentTarget][Diag] event=popup-correlated outcome=registered "
                        + snapshot.LogFields
                );
            }
            else
            {
                LogIgnored("no-matching-quest-preview-intent", snapshot.Token);
            }
        }
        catch (Exception ex)
        {
            PreviewEquipmentTargetInspector.Shared.Clear();
            Logger.Warn(
                $"[F6][EquipmentTarget][Diag] event=popup-correlated outcome=error "
                    + $"error={ex.GetType().Name}:{ex.Message}"
            );
        }
    }

    private static PreviewEquipmentTargetSnapshot CreateSnapshot(
        EquipmentDropPopup popup,
        EquipmentDropPopupModel model
    )
    {
        if (popup == null || model == null)
            return null;

        int contentType = (int)model.ContentType;
        long contentId = model.Id;
        if (!NormalExactDropTarget.TryFormatTypeName(contentType, out _)
            || contentId <= 0)
            return null;

        var rarities = new List<int>();
        var modelRarities = model.Rarities;
        if (modelRarities != null)
        {
            for (int i = 0; i < modelRarities.Length; i++)
                rarities.Add((int)modelRarities[i]);
        }

        long groupNo = model.ThumbnailModel?.GroupNo ?? 0;
        string visibleName = popup._nameText?.text;
        string name = string.IsNullOrWhiteSpace(visibleName) ? model.Name : visibleName;
        NormalEquipmentMasterIndex familyMaster = null;
        string familyError = string.Empty;
        if (!NormalEquipmentMasterCatalog.TryGet(out familyMaster, out familyError))
            familyMaster = null;
        return new PreviewEquipmentTargetSnapshot(
            contentType,
            contentId,
            name,
            groupNo,
            model.Rank,
            rarities,
            familyMaster,
            familyError
        );
    }

    private static bool IsActivePopup(object handle)
    {
        if (handle is not EquipmentDropPopup popup || popup == null)
            return false;
        GameObject gameObject = popup.gameObject;
        return gameObject != null && gameObject.activeInHierarchy;
    }

    private static void LogIgnored(string reason, string token)
    {
        if (_ignoredPopupLogs >= IgnoredPopupLogLimit)
            return;
        _ignoredPopupLogs++;
        Logger.Info(
            $"[F6][EquipmentTarget][Diag] event=popup-correlated outcome=ignored "
                + $"reason={reason} token={token} bounded={_ignoredPopupLogs}/{IgnoredPopupLogLimit}"
        );
    }
}
