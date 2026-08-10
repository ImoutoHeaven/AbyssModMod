#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

// The test project compiles the unmodified production Controller source but deliberately does
// not reference IL2CPP game assemblies.  This mirrors only its already-public bridge contract;
// production continues to provide the concrete reflection bridge from NetherRuntimeBridge.cs.
internal readonly record struct NetherRuntimeSnapshotResult(NetherSnapshot? Snapshot, string Detail)
{
    public bool IsSuccess => Snapshot != null && Detail.Length == 0;

    public static NetherRuntimeSnapshotResult Success(NetherSnapshot snapshot) => new(snapshot, string.Empty);

    public static NetherRuntimeSnapshotResult Failure(string detail) => new(null, detail);
}

internal readonly record struct NetherRuntimeCodeCandidatesResult(
    IReadOnlyList<NetherCodeCandidate> Candidates,
    bool IsMasterComplete,
    string Detail
)
{
    public bool IsSuccess => Detail.Length == 0;

    public static NetherRuntimeCodeCandidatesResult Failure(string detail) => new(
        Array.Empty<NetherCodeCandidate>(),
        false,
        detail
    );
}

internal interface INetherRuntimeBridge : INetherRuntimeParentDriver, INetherReadOnlyReconcileDriver,
    INetherBattleIngressDriver, INetherBattleSettlementDriver, INetherBattleProjectionSnapshotDriver,
    INetherContinueSceneDriver, INetherBattleResultCodeDriver, INetherRecoveredCodeOfferDriver
{
    bool HasRegisteredFloorSelection { get; }
    bool HasObservedNetherBattleResult { get; }
    bool IsBattleActive { get; }
    bool IsResultObserved { get; }
    NetherRuntimeSnapshotResult TryCaptureSnapshot();
    NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors);
    NetherRuntimeInteractivePreEntryInputsResult TryCaptureInteractivePreEntryInputs(
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings
    );
    NetherRuntimePopupResult TryGetActivePopup();
    bool BeginFloorParent(NetherPlannedAction action, long generation);
    NetherNativeActionResult InvokeOwnedPopup(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    );
    void TerminateFloorParent();
    NetherNativeActionResult Reconcile();
    NetherNativeActionResult Invoke(NetherPlannedAction action);
    NetherNativeActionResult PollNativeFlow();
    bool TryBeginContinueSceneHandoff(out long ownerGeneration);
    NetherCheckpointReturnPreflightDecision PreflightContinueReturn(NetherPlannedAction action);
    NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items);
    bool TryConsumeResultSuccess();
    NetherNativeActionResult PollResultFlow();
    NetherBattleResultContinuationStep PollBattleResultContinuation(bool allowInvoke);
    void ClearRegistrations();
}

internal sealed class NetherRuntimeBridge
{
    public static INetherRuntimeBridge Instance { get; } = new UnavailableBridge();

    private sealed class UnavailableBridge : INetherRuntimeBridge
    {
        private static NetherNativeActionResult Unavailable(string operation) =>
            NetherNativeActionResult.BindingUnavailable("test-unconfigured-" + operation);

        public bool HasRegisteredFloorSelection => false;
        public bool HasObservedNetherBattleResult => false;
        public bool IsBattleActive => false;
        public bool IsResultObserved => false;
        public bool IsF11Busy => false;
        public bool FloorOwnerTerminated => false;
        public long CurrentRuntimeGeneration => 0;
        public bool IsExpectedNetherTopScene => false;
        public NetherRuntimeSnapshotResult TryCaptureSnapshot() => NetherRuntimeSnapshotResult.Failure("test-unconfigured-snapshot");
        public NetherRuntimeRouteSafetyData TryCaptureRouteSafety(IReadOnlyList<NetherFloorNode> floors) => new();
        public NetherRuntimeInteractivePreEntryInputsResult TryCaptureInteractivePreEntryInputs(NetherSnapshot snapshot, NetherAutoClimbSettings settings) =>
            NetherRuntimeInteractivePreEntryInputsResult.Failure("test-unconfigured-interactive");
        public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates() => NetherRuntimeCodeCandidatesResult.Failure("test-unconfigured-codes");
        public NetherRuntimeSnapshotResult TryCaptureBattleResultCodeSnapshot() =>
            NetherRuntimeSnapshotResult.Failure("test-unconfigured-battle-result-code-snapshot");
        public NetherRuntimePopupResult TryGetBattleResultCodePopup() =>
            NetherRuntimePopupResult.Failure("test-unconfigured-battle-result-code-popup");
        public NetherNativeActionResult InvokeBattleResultCode(
            NetherRuntimePopupContext popup,
            NetherPlannedAction action
        ) => Unavailable("battle-result-code");
        public NetherBattleResultCodeNativeStep PollBattleResultCodeNative() =>
            NetherBattleResultCodeNativeStep.BindingUnavailable("test-unconfigured-battle-result-code-native");
        public NetherRuntimePopupResult TryGetActivePopup() => NetherRuntimePopupResult.Failure("test-unconfigured-popup");
        public NetherRuntimePopupResult TryGetOwnedPopup(NetherPlannedAction parent) => NetherRuntimePopupResult.Failure("missing-owned-floor-popup");
        public bool BeginFloorParent(NetherPlannedAction action, long generation) => false;
        public void TerminateFloorParent() { }
        public NetherNativeActionResult InvokeOwnedPopup(NetherPlannedAction parent, NetherRuntimePopupContext popup, NetherPlannedAction action) => Unavailable("owned-popup");
        public NetherNativeActionResult Reconcile() => Unavailable("reconcile");
        public NetherNativeActionResult Invoke(NetherPlannedAction action) => Unavailable("invoke");
        public NetherNativeActionResult PollNativeFlow() => Unavailable("native-flow");
        public NetherNativeActionResult PollFloorParent() => Unavailable("floor-parent");
        public bool TryBeginContinueSceneHandoff(out long ownerGeneration)
        {
            ownerGeneration = 0;
            return false;
        }
        public NetherCheckpointReturnPreflightDecision PreflightContinueReturn(NetherPlannedAction action) => new()
        {
            Kind = NetherCheckpointReturnPreflightKind.Pause,
            PauseReason = NetherPauseReason.BindingUnavailable,
            Detail = "test-unconfigured-preflight",
        };
        public NetherNativeActionResult SelectReturnItems(IReadOnlyList<NetherRewardItem> items) => Unavailable("return-items");
        public bool TryConsumeResultSuccess() => false;
        public NetherNativeActionResult PollResultFlow() => Unavailable("result");
        public NetherBattleResultContinuationStep PollBattleResultContinuation(bool allowInvoke) =>
            new(NetherBattleResultContinuationStepKind.BindingUnavailable, "test-unconfigured-battle-result");
        public void ClearRegistrations() { }
        public NetherNativeActionResult BeginGetOnlyRefresh() => Unavailable("get");
        public NetherNativeActionResult PollGetOnlyRefresh() => Unavailable("get-poll");
        public NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot() => NetherReadOnlySnapshotResult.Failure("test-unconfigured-applied-snapshot");
        public NetherNativeActionResult PollBattleLifecycle() => Unavailable("battle");
        public NetherNativeActionResult PollBattleStart() => Unavailable("battle-start");
        public void CancelBattleStartObservation() { }
        public bool TryConsumeBattleClear() => false;
        public bool TryConsumeBattleClose() => false;
        public NetherActiveCodeErosionProjection TryCaptureActiveCodeErosionProjection() =>
            NetherActiveCodeErosionProjectionMapper.Unknown("test-unconfigured-projection");
        public NetherNativeActionResult PollContinueParent() => Unavailable("continue-parent");
    }
}
