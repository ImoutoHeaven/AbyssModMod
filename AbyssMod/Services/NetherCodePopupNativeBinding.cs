#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Version-characterized generated callbacks for the normal Abyss code offer popup.  ISIL for
/// the packaged client proves b__12_0 invokes _onCancel while b__12_2 invokes _onConfirm; keep
/// the identifiers here so tests can distinguish the two otherwise adjacent lambdas.
/// </summary>
internal static class NetherCodePopupNativeBinding
{
    private const string UnitTypeName = "UniRx.Unit";
    private const string PopupTypeName =
        "Project.Nether.AbyssCodeSelectPopup.AbyssCodeSelectPopup";
    private const string PartyModelTypeName = "Project.Nether.NetherPartyModel";
    private const string Il2CppCancellationTokenTypeName = "Il2CppSystem.Threading.CancellationToken";
    private const string UniTaskTypeName = "Cysharp.Threading.Tasks.UniTask";

    // These are the exact managed member names in the current packaged BepInEx interop.  The
    // cpp2il source names are retained only as exact ObfuscatedNameAttribute contracts, never
    // as a fuzzy or arity-only fallback.
    public const string CancelCallback = "_SetupPopupEvent_b__12_0";
    public const string DetailCallback = "_SetupPopupEvent_b__12_3";
    public const string ConfirmCallback = "_SetupPopupEvent_b__12_2";
    public const string CancelCallbackObfuscatedName = "<SetupPopupEvent>b__12_0";
    public const string DetailCallbackObfuscatedName = "<SetupPopupEvent>b__12_3";
    public const string ConfirmCallbackObfuscatedName = "<SetupPopupEvent>b__12_2";
    public const string ConfirmSequenceObfuscatedName =
        "<OpenAbyssCodeSelectPopupIfNeededAsync>g__HandleConfirmSequenceAsync|19_2";
    public const string CancelSequenceObfuscatedName =
        "<OpenAbyssCodeSelectPopupIfNeededAsync>g__HandleCancelSequenceAsync|19_3";
    public const string ConfirmTask =
        "Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_Int64_NetherPartyModel_CancellationToken_0";
    public const string CancelTask =
        "Method_Internal_Static_UniTask_AbyssCodeSelectPopupController_CancellationToken_0";

    public static NetherNativeMethodDescriptor ConfirmDescriptor(string controllerTypeName) => new(
        ConfirmCallback,
        new[] { UnitTypeName, controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherNativeMethodDescriptor CancelDescriptor(string controllerTypeName) => new(
        CancelCallback,
        new[] { UnitTypeName, controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding ConfirmCallbackBinding(string controllerTypeName) => new(
        ConfirmCallback,
        ConfirmCallbackObfuscatedName,
        new[] { UnitTypeName, controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding CancelCallbackBinding(string controllerTypeName) => new(
        CancelCallback,
        CancelCallbackObfuscatedName,
        new[] { UnitTypeName, controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding DetailCallbackBinding(string controllerTypeName) => new(
        DetailCallback,
        DetailCallbackObfuscatedName,
        new[] { "System.Int32", controllerTypeName, PopupTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding ConfirmTaskBinding(string controllerTypeName) => new(
        ConfirmTask,
        ConfirmSequenceObfuscatedName,
        new[] { controllerTypeName, "System.Int64", PartyModelTypeName, Il2CppCancellationTokenTypeName },
        UniTaskTypeName
    ) { IsStatic = true };

    public static NetherCodePopupInteropMethodBinding CancelTaskBinding(string controllerTypeName) => new(
        CancelTask,
        CancelSequenceObfuscatedName,
        new[] { controllerTypeName, Il2CppCancellationTokenTypeName },
        UniTaskTypeName
    ) { IsStatic = true };

    /// <summary>
    /// The cancel callback itself returns void because the packaged closure calls Forget.
    /// Harmony therefore observes this exact static generated UniTask factory, whose ISIL
    /// awaits RequestNetherFixCodeAsync(0, 0), rather than treating popup disappearance as a
    /// completed Keep action.
    /// </summary>
    public static NetherNativeMethodDescriptor CancelSequenceDescriptor(string controllerTypeName) => new(
        CancelTask,
        new[] { controllerTypeName, Il2CppCancellationTokenTypeName },
        UniTaskTypeName
    ) { IsStatic = true };
}
