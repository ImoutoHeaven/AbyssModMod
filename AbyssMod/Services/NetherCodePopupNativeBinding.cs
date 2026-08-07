#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Version-characterized generated callbacks for the normal Abyss code offer popup.  ISIL for
/// the packaged client proves b__12_0 invokes _onCancel while b__12_2 invokes _onConfirm; keep
/// the identifiers here so tests can distinguish the two otherwise adjacent lambdas.
/// </summary>
internal static class NetherCodePopupNativeBinding
{
    public const string CancelCallback = "<SetupPopupEvent>b__12_0";
    public const string DetailCallback = "<SetupPopupEvent>b__12_3";
    public const string ConfirmCallback = "<SetupPopupEvent>b__12_2";
    public const string CancelSequence = "<OpenAbyssCodeSelectPopupIfNeededAsync>g__HandleCancelSequenceAsync|19_3";

    public static NetherNativeMethodDescriptor ConfirmDescriptor(string controllerTypeName) => new(
        ConfirmCallback,
        new[] { "UniRx.Unit", controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherNativeMethodDescriptor CancelDescriptor(string controllerTypeName) => new(
        CancelCallback,
        new[] { "UniRx.Unit", controllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    /// <summary>
    /// The cancel callback itself returns void because the packaged closure calls Forget.
    /// Harmony therefore observes this exact static generated UniTask factory, whose ISIL
    /// awaits RequestNetherFixCodeAsync(0, 0), rather than treating popup disappearance as a
    /// completed Keep action.
    /// </summary>
    public static NetherNativeMethodDescriptor CancelSequenceDescriptor(string controllerTypeName) => new(
        CancelSequence,
        new[] { controllerTypeName, "System.Threading.CancellationToken" },
        "Cysharp.Threading.Tasks.UniTask"
    ) { IsStatic = true };
}
