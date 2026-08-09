#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Exact generated callback descriptors for the one-ticket Continue branch.  Both values of
/// Continue popup <c>_canBoost</c> enter the packaged <c>b__8_2</c> chain; the true branch then
/// owns a fresh Boost popup whose count is set to one before its confirm callback runs.
/// </summary>
internal static class NetherCheckpointContinueNativeBinding
{
    private const string UnitTypeName = "UniRx.Unit";
    private const string ContinueControllerTypeName =
        "Project.Nether.NetherContinueConfirmPopup.NetherContinueConfirmPopupController";
    private const string BoostControllerTypeName =
        "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopupController";
    private const string BoostPopupTypeName =
        "Project.Nether.NetherBoostConfirmPopup.NetherBoostConfirmPopup";

    public static NetherNativeMethodDescriptor ContinueCallback { get; } = new(
        "<SetupPopupEvent>b__8_2",
        new[] { UnitTypeName, ContinueControllerTypeName },
        "System.Void"
    );

    public static NetherNativeMethodDescriptor BoostSetCount { get; } = new(
        "<SetupPopupEvent>b__7_2",
        new[] { "System.Int32", BoostControllerTypeName, BoostPopupTypeName },
        "System.Void"
    );

    public static NetherNativeMethodDescriptor BoostConfirm { get; } = new(
        "<SetupPopupEvent>b__7_1",
        new[] { UnitTypeName, BoostControllerTypeName, BoostPopupTypeName },
        "System.Void"
    );

    public static NetherCodePopupInteropMethodBinding ContinueCallbackInterop { get; } = new(
        "_SetupPopupEvent_b__8_2",
        "<SetupPopupEvent>b__8_2",
        new[] { UnitTypeName, ContinueControllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding FinishCallbackInterop { get; } = new(
        "_SetupPopupEvent_b__8_1",
        "<SetupPopupEvent>b__8_1",
        new[] { UnitTypeName, ContinueControllerTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding BoostSetCountInterop { get; } = new(
        "_SetupPopupEvent_b__7_2",
        "<SetupPopupEvent>b__7_2",
        new[] { "System.Int32", BoostControllerTypeName, BoostPopupTypeName },
        "System.Void"
    ) { IsStatic = false };

    public static NetherCodePopupInteropMethodBinding BoostConfirmInterop { get; } = new(
        "_SetupPopupEvent_b__7_1",
        "<SetupPopupEvent>b__7_1",
        new[] { UnitTypeName, BoostControllerTypeName, BoostPopupTypeName },
        "System.Void"
    ) { IsStatic = false };

    public const int ExactTicketCount = 1;

    public static bool SubmitContinue(NetherCheckpointNativeFlow flow, bool canBoost) =>
        flow != null && flow.SubmitContinue(canBoost);
}
