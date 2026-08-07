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

    public static NetherNativeMethodDescriptor ConfirmDescriptor(string controllerTypeName) => new(
        ConfirmCallback,
        new[] { "UniRx.Unit", controllerTypeName },
        "System.Void"
    );
}
