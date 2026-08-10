#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Exact runtime seam for a code offer opened by
/// FloorSelection.HandleStartEventByStatusAsync before F12 owns a floor click.  The parent
/// task is the game's existing resume/start-status sequence; automation may drive only its
/// owned code child and a final GET-only datastore refresh.
/// </summary>
internal interface INetherRecoveredCodeOfferDriver
{
    bool HasRecoveredCodeOffer => false;

    NetherRuntimeSnapshotResult TryCaptureRecoveredCodeSnapshot() =>
        NetherRuntimeSnapshotResult.Failure("recovered-code-driver-unavailable");

    NetherRuntimeCodeCandidatesResult TryGetRecoveredCodeCandidates() =>
        NetherRuntimeCodeCandidatesResult.Failure("recovered-code-driver-unavailable");

    NetherRuntimePopupResult TryGetRecoveredCodePopup() =>
        NetherRuntimePopupResult.Failure("recovered-code-driver-unavailable");

    NetherNativeActionResult InvokeRecoveredCode(
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    ) => NetherNativeActionResult.BindingUnavailable("recovered-code-driver-unavailable");

    NetherBattleResultCodeNativeStep PollRecoveredCodeNative() =>
        NetherBattleResultCodeNativeStep.BindingUnavailable("recovered-code-driver-unavailable");

    NetherNativeActionResult PollRecoveredCodeParent() =>
        NetherNativeActionResult.BindingUnavailable("recovered-code-driver-unavailable");

    NetherNativeActionResult BeginRecoveredCodeRefresh() =>
        NetherNativeActionResult.BindingUnavailable("recovered-code-driver-unavailable");

    NetherNativeActionResult PollRecoveredCodeRefresh() =>
        NetherNativeActionResult.BindingUnavailable("recovered-code-driver-unavailable");

    NetherRuntimeSnapshotResult TryCaptureRecoveredCodeAppliedSnapshot() =>
        NetherRuntimeSnapshotResult.Failure("recovered-code-driver-unavailable");

    void CompleteRecoveredCodeOffer() { }
}

internal enum NetherRecoveredCodeOfferStepKind
{
    NoOffer,
    AwaitingPopup,
    AwaitingNative,
    ReloadReady,
    AwaitingParent,
    AwaitingRefresh,
    Completed,
    CanceledBeforeInvoke,
    CanceledAfterDrain,
    BindingUnavailable,
    Faulted,
}

internal readonly record struct NetherRecoveredCodeOfferStep(
    NetherRecoveredCodeOfferStepKind Kind,
    string Detail,
    NetherSnapshot? Snapshot = null,
    NetherCombatLane? LockedLane = null,
    NetherPlannedAction? Action = null
);

/// <summary>
/// Serializes one recovered foreground code offer as:
/// code child terminal → original HandleStartEventByStatusAsync terminal → one GET-only sync.
/// It deliberately does not enter the ordinary SelectFloor transaction state, because this
/// native parent predates F12 and has its own exact Harmony-captured UniTask.
/// </summary>
internal sealed class NetherRecoveredCodeOfferCoordinator
{
    private enum Stage
    {
        Idle,
        Code,
        Parent,
        Refresh,
    }

    private sealed class CodeDriverAdapter : INetherBattleResultCodeDriver
    {
        public INetherRecoveredCodeOfferDriver? Driver { get; set; }

        private INetherRecoveredCodeOfferDriver Current => Driver
            ?? throw new InvalidOperationException("missing-recovered-code-driver");

        public NetherRuntimeSnapshotResult TryCaptureBattleResultCodeSnapshot() =>
            Current.TryCaptureRecoveredCodeSnapshot();

        public NetherRuntimeCodeCandidatesResult TryGetCodeCandidates() =>
            Current.TryGetRecoveredCodeCandidates();

        public NetherRuntimePopupResult TryGetBattleResultCodePopup() =>
            Current.TryGetRecoveredCodePopup();

        public NetherNativeActionResult InvokeBattleResultCode(
            NetherRuntimePopupContext popup,
            NetherPlannedAction action
        ) => Current.InvokeRecoveredCode(popup, action);

        public NetherBattleResultCodeNativeStep PollBattleResultCodeNative() =>
            Current.PollRecoveredCodeNative();
    }

    private readonly CodeDriverAdapter _adapter = new();
    private readonly NetherBattleResultCodeCoordinator _codeFlow;
    private Stage _stage;
    private bool _mutationStarted;
    private bool _cancelAfterDrain;
    private NetherCombatLane? _lockedLane;
    private NetherAutoClimbSettings? _settings;

    public NetherRecoveredCodeOfferCoordinator(int maximumPopupPolls = 600)
    {
        _codeFlow = new NetherBattleResultCodeCoordinator(
            maximumPopupPolls,
            NetherActionKind.RecoveredCodeOffer
        );
    }

    public bool IsActive => _stage != Stage.Idle;

    public bool HasMutationInFlight => _mutationStarted;

    public NetherRecoveredCodeOfferStep Pump(
        INetherRecoveredCodeOfferDriver driver,
        NetherAutoClimbSettings settings,
        NetherCombatLane? lockedLane,
        bool allowInvoke
    )
    {
        if (driver == null)
            throw new ArgumentNullException(nameof(driver));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        if (_stage == Stage.Idle)
        {
            if (!driver.HasRecoveredCodeOffer)
                return new(NetherRecoveredCodeOfferStepKind.NoOffer, "no-recovered-code-owner");
            _adapter.Driver = driver;
            _lockedLane = lockedLane;
            _settings = settings with { };
            _stage = Stage.Code;
        }
        else if (!ReferenceEquals(_adapter.Driver, driver))
        {
            return Terminate(
                NetherRecoveredCodeOfferStepKind.BindingUnavailable,
                "recovered-code-driver-changed"
            );
        }

        if (!allowInvoke && _mutationStarted)
            _cancelAfterDrain = true;

        switch (_stage)
        {
            case Stage.Code:
                return PumpCode(driver, settings, allowInvoke);
            case Stage.Parent:
                return PumpParent(driver);
            case Stage.Refresh:
                return PumpRefresh(driver);
            default:
                return Terminate(
                    NetherRecoveredCodeOfferStepKind.Faulted,
                    "invalid-recovered-code-stage"
                );
        }
    }

    public void Reset()
    {
        _codeFlow.Reset();
        _adapter.Driver = null;
        _stage = Stage.Idle;
        _mutationStarted = false;
        _cancelAfterDrain = false;
        _lockedLane = null;
        _settings = null;
    }

    private NetherRecoveredCodeOfferStep PumpCode(
        INetherRecoveredCodeOfferDriver driver,
        NetherAutoClimbSettings settings,
        bool allowInvoke
    )
    {
        NetherBattleResultCodeStep code = _codeFlow.Pump(
            _adapter,
            _settings ?? settings,
            _lockedLane,
            allowInvoke
        );
        if (code.Action != null)
            _mutationStarted = true;
        _lockedLane = code.LockedLane ?? _lockedLane;

        switch (code.Kind)
        {
            case NetherBattleResultCodeStepKind.AwaitingPopup:
                return Step(NetherRecoveredCodeOfferStepKind.AwaitingPopup, code);
            case NetherBattleResultCodeStepKind.AwaitingNative:
                return Step(NetherRecoveredCodeOfferStepKind.AwaitingNative, code);
            case NetherBattleResultCodeStepKind.ReloadReady:
                return Step(NetherRecoveredCodeOfferStepKind.ReloadReady, code);
            case NetherBattleResultCodeStepKind.Completed:
                _stage = Stage.Parent;
                return Step(
                    NetherRecoveredCodeOfferStepKind.AwaitingParent,
                    code with { Detail = "recovered-code-child-terminal:" + code.Detail }
                );
            case NetherBattleResultCodeStepKind.CanceledBeforeInvoke:
                if (!_mutationStarted)
                {
                    Reset();
                    return new(
                        NetherRecoveredCodeOfferStepKind.CanceledBeforeInvoke,
                        code.Detail
                    );
                }
                _cancelAfterDrain = true;
                _stage = Stage.Parent;
                return Step(
                    NetherRecoveredCodeOfferStepKind.AwaitingParent,
                    code with { Detail = "recovered-code-cancel-draining-parent:" + code.Detail }
                );
            case NetherBattleResultCodeStepKind.BindingUnavailable:
                return Terminate(NetherRecoveredCodeOfferStepKind.BindingUnavailable, code.Detail);
            default:
                return Terminate(NetherRecoveredCodeOfferStepKind.Faulted, code.Detail);
        }
    }

    private NetherRecoveredCodeOfferStep PumpParent(INetherRecoveredCodeOfferDriver driver)
    {
        NetherNativeActionResult parent = driver.PollRecoveredCodeParent();
        if (parent.Kind == NetherNativeActionResultKind.Started)
        {
            return new(
                NetherRecoveredCodeOfferStepKind.AwaitingParent,
                parent.Detail,
                LockedLane: _lockedLane
            );
        }
        if (parent.Kind != NetherNativeActionResultKind.Completed)
        {
            return Terminate(
                parent.Kind == NetherNativeActionResultKind.BindingUnavailable
                    ? NetherRecoveredCodeOfferStepKind.BindingUnavailable
                    : NetherRecoveredCodeOfferStepKind.Faulted,
                "recovered-code-parent:" + parent.Detail
            );
        }

        NetherNativeActionResult refresh = driver.BeginRecoveredCodeRefresh();
        if (refresh.Kind is not (
                NetherNativeActionResultKind.Started
                or NetherNativeActionResultKind.Completed
            ))
        {
            return Terminate(
                refresh.Kind == NetherNativeActionResultKind.BindingUnavailable
                    ? NetherRecoveredCodeOfferStepKind.BindingUnavailable
                    : NetherRecoveredCodeOfferStepKind.Faulted,
                "recovered-code-refresh-start:" + refresh.Detail
            );
        }
        _stage = Stage.Refresh;
        return new(
            NetherRecoveredCodeOfferStepKind.AwaitingRefresh,
            refresh.Detail,
            LockedLane: _lockedLane
        );
    }

    private NetherRecoveredCodeOfferStep PumpRefresh(INetherRecoveredCodeOfferDriver driver)
    {
        NetherNativeActionResult refresh = driver.PollRecoveredCodeRefresh();
        if (refresh.Kind == NetherNativeActionResultKind.Started)
        {
            return new(
                NetherRecoveredCodeOfferStepKind.AwaitingRefresh,
                refresh.Detail,
                LockedLane: _lockedLane
            );
        }
        if (refresh.Kind != NetherNativeActionResultKind.Completed)
        {
            return Terminate(
                refresh.Kind == NetherNativeActionResultKind.BindingUnavailable
                    ? NetherRecoveredCodeOfferStepKind.BindingUnavailable
                    : NetherRecoveredCodeOfferStepKind.Faulted,
                "recovered-code-refresh:" + refresh.Detail
            );
        }

        NetherRuntimeSnapshotResult snapshot = driver.TryCaptureRecoveredCodeAppliedSnapshot();
        if (!snapshot.IsSuccess)
        {
            return Terminate(
                NetherRecoveredCodeOfferStepKind.BindingUnavailable,
                "recovered-code-applied-snapshot:" + snapshot.Detail
            );
        }

        bool canceled = _cancelAfterDrain;
        NetherCombatLane? lane = _lockedLane;
        driver.CompleteRecoveredCodeOffer();
        Reset();
        return new(
            canceled
                ? NetherRecoveredCodeOfferStepKind.CanceledAfterDrain
                : NetherRecoveredCodeOfferStepKind.Completed,
            canceled ? "recovered-code-drain-completed" : "recovered-code-completed",
            snapshot.Snapshot,
            lane
        );
    }

    private NetherRecoveredCodeOfferStep Step(
        NetherRecoveredCodeOfferStepKind kind,
        NetherBattleResultCodeStep code
    ) => new(kind, code.Detail, LockedLane: _lockedLane, Action: code.Action);

    private NetherRecoveredCodeOfferStep Terminate(
        NetherRecoveredCodeOfferStepKind kind,
        string detail
    )
    {
        NetherCombatLane? lane = _lockedLane;
        Reset();
        return new(kind, detail ?? string.Empty, LockedLane: lane);
    }
}
