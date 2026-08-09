#nullable enable

namespace AbyssMod.Services;

/// <summary>Exact packaged interop contracts for the target_type=7 code-conversion sequence.</summary>
internal static class NetherCodeTransformNativeBinding
{
    private const string UnitTypeName = "UniRx.Unit";
    private const string Il2CppActionTypeName = "Il2CppSystem.Action";
    private const string Il2CppBoolActionTypeName = "Il2CppSystem.Action<System.Boolean>";
    private const string Il2CppCancellationTokenTypeName = "Il2CppSystem.Threading.CancellationToken";
    private const string UniTaskTypeName = "Cysharp.Threading.Tasks.UniTask";
    private const string ConfirmPopupTypeName =
        "Project.Nether.AbyssCodeChangePopup.AbyssCodeChangePopup";
    private const string CompletePopupTypeName =
        "Project.Nether.AbyssCodeChangeCompletePopup.AbyssCodeChangeCompletePopup";
    private const string NetherModelTypeName = "Project.Nether.FloorSelection.NetherModel";
    private const string EventResultTypeName = "Project.Nether.NetherEventResultModel";
    private const string BoolReactivePropertyTypeName = "UniRx.BoolReactiveProperty";

    public const string TransformTask =
        "Method_Internal_Static_UniTask_AbyssCodeListPopupController_NetherModel_NetherEventResultModel_Int64_BoolReactiveProperty_CancellationToken_0";
    public const string TransformTaskObfuscatedName =
        "<OpenChangeAbyssCodeListPopupByFloorEventAsync>g__HandleChangeAsync|11_1";

    public static NetherCodePopupInteropMethodBinding ConfirmCallbackBinding { get; } = new(
        "_SetupPopupEvent_b__6_1",
        "<SetupPopupEvent>b__6_1",
        new[] { UnitTypeName, Il2CppBoolActionTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding CompleteCloseCallbackBinding { get; } = new(
        "_SetupPopupEvent_b__7_0",
        "<SetupPopupEvent>b__7_0",
        new[] { UnitTypeName, CompletePopupTypeName, Il2CppActionTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding TransformTaskBinding(string listControllerTypeName) => new(
        TransformTask,
        TransformTaskObfuscatedName,
        new[]
        {
            listControllerTypeName,
            NetherModelTypeName,
            EventResultTypeName,
            "System.Int64",
            BoolReactivePropertyTypeName,
            Il2CppCancellationTokenTypeName,
        },
        UniTaskTypeName
    ) { IsStatic = true };

    public static NetherNativeMethodDescriptor TransformTaskDescriptor(string listControllerTypeName) => new(
        TransformTask,
        TransformTaskBinding(listControllerTypeName).ParameterTypeNames,
        UniTaskTypeName
    ) { IsStatic = true };
}
