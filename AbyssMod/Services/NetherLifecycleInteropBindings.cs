#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AbyssMod.Services;

internal readonly record struct NetherInteropPatchBinding(
    string TypeName,
    NetherNativeMethodDescriptor Method,
    BindingFlags Flags
);

/// <summary>
/// Exact lifecycle contracts for the BepInEx IL2CPP interop assembly shipped with the game.
/// Type lookup deliberately calls Assembly.GetType on already-loaded assemblies; it never
/// enumerates every type in every Unity assembly, which avoids Harmony's global-scan loader
/// warnings and makes a missing binding attributable to one exact type/signature.
/// </summary>
internal static class NetherLifecycleInteropBindings
{
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const string UniTaskTypeName = "Cysharp.Threading.Tasks.UniTask";
    private const string UnitTypeName = "UniRx.Unit";
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IReadOnlyList<NetherInteropPatchBinding> All { get; } = new[]
    {
        Instance(
            "Project.Nether.FloorSelection.SubScene",
            "OnInitializeAsync",
            new[]
            {
                "Project.ISubService",
                "Project.SubSceneParamBase",
                "Il2CppSystem.Threading.CancellationToken",
            },
            UniTaskTypeName
        ),
        Instance(
            "Project.Nether.FloorSelection.SubScene",
            "OnRefreshAsync",
            new[]
            {
                "Project.ISubService",
                "Project.SubSceneParamBase",
                "Il2CppSystem.Threading.CancellationToken",
            },
            UniTaskTypeName
        ),
        Instance(
            "Project.Nether.FloorSelection.SubScene",
            "OnEntered",
            Array.Empty<string>(),
            "System.Void"
        ),
        Instance(
            "Project.Nether.FloorSelection.SubViewController",
            "HandleStartEventByStatusAsync",
            new[] { "System.Boolean" },
            UniTaskTypeName
        ),
        Instance(
            "Project.Nether.FloorSelection.SubViewController",
            "Project_ISubService_Terminate",
            Array.Empty<string>(),
            "System.Void"
        ),
        Instance(
            "Project.Ingame.BottomRightView",
            "ApplyUserSettings",
            new[] { "Project.Ingame.IIngameUserSettings" },
            "System.Void"
        ),
        Instance("Project.Ingame.BottomRightView", "OnDestroy", Array.Empty<string>(), "System.Void"),
        Instance("Project.PopupBase", "Close", Array.Empty<string>(), "System.Void"),
        Instance("Project.PopupBase", "ImmediatelyClose", Array.Empty<string>(), "System.Void"),
        Instance("Absf.MonoBehaviourWithUniTask", "OnDestroy", Array.Empty<string>(), "System.Void"),
        Popup(
            "Project.Nether.NetherEventPopup.NetherEventPopupController",
            "Project.Nether.NetherEventPopup.NetherEventPopup"
        ),
        Popup(
            "Project.Nether.NetherRecoverPopup.NetherRecoverPopupController",
            "Project.Nether.NetherRecoverPopup.NetherRecoverPopup"
        ),
        Popup(
            "Project.Nether.NetherTreasurePopup.NetherTreasurePopupController",
            "Project.Nether.NetherTreasurePopup.NetherTreasurePopup"
        ),
        Popup(
            "Project.Nether.NetherShopPopup.NetherShopPopupController",
            "Project.Nether.NetherShopPopup.NetherShopPopup"
        ),
        Popup(
            "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopupController",
            "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopup"
        ),
        Popup(
            "Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopupController",
            "Project.Nether.NetherAbyssCodeListPopup.AbyssCodeListPopup"
        ),
        Popup(
            "Project.Nether.AbyssCodeChangePopup.AbyssCodeChangePopupController",
            "Project.Nether.AbyssCodeChangePopup.AbyssCodeChangePopup"
        ),
        Popup(
            "Project.Nether.AbyssCodeChangeCompletePopup.AbyssCodeChangeCompletePopupController",
            "Project.Nether.AbyssCodeChangeCompletePopup.AbyssCodeChangeCompletePopup"
        ),
        Popup(
            "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnItemSelectionPopupController",
            "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnItemSelectionPopup"
        ),
        Popup(
            "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopupController",
            "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopup"
        ),
        Popup(
            "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopupController",
            "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopup"
        ),
        Popup(
            "Project.Nether.NetherContentAcquiredPopup.NetherContentAcquiredPopupController",
            "Project.Nether.NetherContentAcquiredPopup.NetherContentAcquiredPopup"
        ),
        Popup(
            "Project.Nether.NetherFloorEventHintBox.NetherFloorEventHintBoxPopupController",
            "Project.Nether.NetherFloorEventHintBox.NetherFloorEventHintBoxPopup"
        ),
        Instance(
            "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnableItemScrollViewController",
            "InitializeView",
            Array.Empty<string>(),
            "System.Void"
        ),
        Instance(
            "Project.Nether.NetherReturnItemSelectionPopup.NetherReturnableItemScrollViewController",
            "OnThumbnailClicked",
            new[] { "System.Int32" },
            "System.Void"
        ),
    };

    public static NetherCodePopupInteropMethodBinding ShopCloseCallback { get; } = new(
        "_SetupPopupEvent_b__16_0",
        "<SetupPopupEvent>b__16_0",
        new[] { UnitTypeName, Il2CppActionTypeName },
        "System.Void"
    ) { IsStatic = false };

    /// <summary>
    /// Exact callback behind the event-result hint box's "confirm to continue" tap.  Unlike
    /// ordinary simple popups this callback intentionally asks IPopupService to close the
    /// current popup; invoking only the SetupPopupEvent close argument bypasses that native
    /// service transition and can leave the visual overlay alive.
    /// </summary>
    public static NetherCodePopupInteropMethodBinding FloorEventHintDismissCallback { get; } = new(
        "_SetupPopupEvent_b__3_0",
        "<SetupPopupEvent>b__3_0",
        new[] { UnitTypeName, Il2CppActionTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static Type? ResolveType(IEnumerable<Assembly> assemblies, string typeName)
    {
        if (assemblies == null || string.IsNullOrWhiteSpace(typeName))
            return null;

        foreach (Assembly assembly in assemblies)
        {
            try
            {
                Type? type = assembly.GetType(typeName, throwOnError: false, ignoreCase: false);
                if (type != null)
                    return type;
            }
            catch (Exception)
            {
                // A malformed unrelated interop assembly must not turn exact lookup into a
                // global type enumeration or prevent later loaded assemblies from matching.
            }
        }

        return null;
    }

    public static bool TryResolve(
        IEnumerable<Assembly> assemblies,
        NetherInteropPatchBinding binding,
        out string error,
        out MethodInfo? method
    )
    {
        Type? type = ResolveType(assemblies, binding.TypeName);
        if (type == null)
        {
            method = null;
            error = "binding-unavailable:" + binding.TypeName + ":missing-type";
            return false;
        }

        return TryResolveExactMethod(type, binding.Method, binding.Flags, out error, out method);
    }

    public static bool TryResolveExactMethod(
        Type type,
        NetherNativeMethodDescriptor expected,
        BindingFlags flags,
        out string error,
        out MethodInfo? method
    )
    {
        method = null;
        MethodInfo[] candidates = type.GetMethods(flags)
            .Where(candidate => string.Equals(candidate.Name, expected.Name, StringComparison.Ordinal))
            .ToArray();
        NetherNativeMethodDescriptor[] descriptors = candidates.Select(Describe).ToArray();
        NetherNativeBindingSelection selection = NetherNativeMethodBindingSelector.Select(expected, descriptors);
        if (selection.ResultKind != NetherNativeActionResultKind.Started || selection.Method == null)
        {
            error = "binding-unavailable:"
                + (type.FullName ?? type.Name)
                + ":"
                + expected.Name
                + ":"
                + selection.Detail;
            return false;
        }

        int selectedIndex = Array.FindIndex(descriptors, descriptor => ReferenceEquals(descriptor, selection.Method));
        if (selectedIndex < 0)
        {
            error = "binding-unavailable:"
                + (type.FullName ?? type.Name)
                + ":"
                + expected.Name
                + ":selection-lost";
            return false;
        }

        method = candidates[selectedIndex];
        error = string.Empty;
        return true;
    }

    private static NetherInteropPatchBinding Popup(string controllerTypeName, string popupTypeName) =>
        Instance(
            controllerTypeName,
            "SetupPopupEvent",
            new[] { popupTypeName, Il2CppActionTypeName },
            "System.Void"
        );

    private static NetherInteropPatchBinding Instance(
        string typeName,
        string methodName,
        IReadOnlyList<string> parameterTypeNames,
        string returnTypeName
    ) => new(
        typeName,
        new NetherNativeMethodDescriptor(methodName, parameterTypeNames, returnTypeName),
        InstanceFlags
    );

    private static NetherNativeMethodDescriptor Describe(MethodInfo method) => new(
        method.Name,
        method.GetParameters().Select(parameter => TypeName(parameter.ParameterType)).ToArray(),
        TypeName(method.ReturnType)
    ) { IsStatic = method.IsStatic };

    private static string TypeName(Type type) => type.FullName ?? type.Name;
}
