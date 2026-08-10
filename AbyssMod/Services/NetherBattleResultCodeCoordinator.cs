#nullable enable

using System;

namespace AbyssMod.Services;

internal enum NetherBattleResultCodeNativeStepKind
{
    Pending,
    ReloadReady,
    Completed,
    BindingUnavailable,
    Faulted,
}

internal readonly record struct NetherBattleResultCodeNativeStep(
    NetherBattleResultCodeNativeStepKind Kind,
    string Detail
)
{
    public static NetherBattleResultCodeNativeStep Pending(string detail) =>
        new(NetherBattleResultCodeNativeStepKind.Pending, detail ?? string.Empty);

    public static NetherBattleResultCodeNativeStep ReloadReady(string detail) =>
        new(NetherBattleResultCodeNativeStepKind.ReloadReady, detail ?? string.Empty);

    public static NetherBattleResultCodeNativeStep Completed(string detail) =>
        new(NetherBattleResultCodeNativeStepKind.Completed, detail ?? string.Empty);

    public static NetherBattleResultCodeNativeStep BindingUnavailable(string detail) =>
        new(NetherBattleResultCodeNativeStepKind.BindingUnavailable, detail ?? string.Empty);

    public static NetherBattleResultCodeNativeStep Faulted(string detail) =>
        new(NetherBattleResultCodeNativeStepKind.Faulted, detail ?? string.Empty);
}

internal interface INetherBattleResultCodeDriver
{
    NetherRuntimeSnapshotResult TryCaptureBattleResultCodeSnapshot();

    NetherRuntimeCodeCandidatesResult TryGetCodeCandidates();

    NetherRuntimePopupResult TryGetBattleResultCodePopup();

    NetherNativeActionResult InvokeBattleResultCode(
        NetherRuntimePopupContext popup,
        NetherPlannedAction action
    );

    NetherBattleResultCodeNativeStep PollBattleResultCodeNative();
}

internal enum NetherBattleResultCodeStepKind
{
    AwaitingPopup,
    AwaitingNative,
    ReloadReady,
    Completed,
    CanceledBeforeInvoke,
    BindingUnavailable,
    Faulted,
}

internal readonly record struct NetherBattleResultCodeStep(
    NetherBattleResultCodeStepKind Kind,
    string Detail,
    NetherCombatLane? LockedLane = null,
    NetherPlannedAction? Action = null
);

/// <summary>
/// Orders the code offer created inside the native battle-result in-animation ahead of the
/// result page's Next callback.  It owns no endpoint: all mutations go through the exact
/// result-owned popup adapter and each native child is polled to terminal before Next is
/// allowed.  A reroll returns to policy with the same popup's incremented decision epoch.
/// </summary>
internal sealed class NetherBattleResultCodeCoordinator
{
    private readonly NetherCodePolicy _policy = new();
    private readonly NetherNativeWaitGate _popupWait;
    private readonly NetherActionKind _expectedOwnerAction;
    private bool _nativeInFlight;
    private bool _completed;
    private bool _cancelAfterInFlight;
    private NetherCombatLane? _lockedLane;

    public NetherBattleResultCodeCoordinator(
        int maximumPopupPolls = 600,
        NetherActionKind expectedOwnerAction = NetherActionKind.BattleSettlement
    )
    {
        if (expectedOwnerAction is not (
                NetherActionKind.BattleSettlement or NetherActionKind.RecoveredCodeOffer
            ))
        {
            throw new ArgumentOutOfRangeException(nameof(expectedOwnerAction));
        }
        _popupWait = new NetherNativeWaitGate(maximumPopupPolls);
        _expectedOwnerAction = expectedOwnerAction;
    }

    public bool IsNativeInFlight => _nativeInFlight;

    public NetherCombatLane? LockedLane => _lockedLane;

    public NetherBattleResultCodeStep Pump(
        INetherBattleResultCodeDriver driver,
        NetherAutoClimbSettings settings,
        NetherCombatLane? lockedLane,
        bool allowInvoke
    )
    {
        if (driver == null)
            throw new ArgumentNullException(nameof(driver));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));

        if (_completed)
        {
            return new(
                NetherBattleResultCodeStepKind.Completed,
                "battle-result-code-completed",
                _lockedLane
            );
        }

        if (_nativeInFlight)
        {
            if (!allowInvoke)
                _cancelAfterInFlight = true;

            NetherBattleResultCodeNativeStep native = driver.PollBattleResultCodeNative();
            switch (native.Kind)
            {
                case NetherBattleResultCodeNativeStepKind.Pending:
                    return new(
                        NetherBattleResultCodeStepKind.AwaitingNative,
                        native.Detail,
                        _lockedLane
                    );
                case NetherBattleResultCodeNativeStepKind.ReloadReady:
                    _nativeInFlight = false;
                    if (_cancelAfterInFlight)
                    {
                        Reset();
                        return new(
                            NetherBattleResultCodeStepKind.CanceledBeforeInvoke,
                            "f12-disabled-after-result-code-reload"
                        );
                    }
                    return new(
                        NetherBattleResultCodeStepKind.ReloadReady,
                        native.Detail,
                        _lockedLane
                    );
                case NetherBattleResultCodeNativeStepKind.Completed:
                    _nativeInFlight = false;
                    if (_cancelAfterInFlight)
                    {
                        Reset();
                        return new(
                            NetherBattleResultCodeStepKind.CanceledBeforeInvoke,
                            "f12-disabled-after-result-code-terminal"
                        );
                    }
                    _completed = true;
                    return new(
                        NetherBattleResultCodeStepKind.Completed,
                        native.Detail,
                        _lockedLane
                    );
                case NetherBattleResultCodeNativeStepKind.BindingUnavailable:
                    return Terminate(NetherBattleResultCodeStepKind.BindingUnavailable, native.Detail);
                default:
                    return Terminate(NetherBattleResultCodeStepKind.Faulted, native.Detail);
            }
        }

        if (!allowInvoke)
        {
            Reset();
            return new(
                NetherBattleResultCodeStepKind.CanceledBeforeInvoke,
                "f12-disabled-before-result-code"
            );
        }

        NetherRuntimeCodeCandidatesResult candidates = driver.TryGetCodeCandidates();
        if (!candidates.IsSuccess)
        {
            return Terminate(
                NetherBattleResultCodeStepKind.BindingUnavailable,
                "battle-result-code-candidates:" + candidates.Detail
            );
        }
        if (candidates.Candidates.Count == 0)
        {
            _completed = true;
            _lockedLane = lockedLane;
            return new(
                NetherBattleResultCodeStepKind.Completed,
                "battle-result-no-code-offer",
                _lockedLane
            );
        }

        NetherRuntimePopupResult popupResult = driver.TryGetBattleResultCodePopup();
        if (!popupResult.IsSuccess)
        {
            NetherNativeActionResult wait = _popupWait.AwaitRegistration("battle-result-code-popup");
            return wait.Kind == NetherNativeActionResultKind.Started
                ? new(
                    NetherBattleResultCodeStepKind.AwaitingPopup,
                    popupResult.Detail + ":" + wait.Detail,
                    lockedLane
                )
                : Terminate(
                    NetherBattleResultCodeStepKind.BindingUnavailable,
                    popupResult.Detail + ":" + wait.Detail
                );
        }

        NetherRuntimePopupContext popup = popupResult.Popup!;
        if (popup.Kind != NetherRuntimePopupKind.CodeOffer
            || popup.OwnerAction != _expectedOwnerAction
            || popup.OwnerGeneration <= 0
            || popup.Sequence <= 0)
        {
            return Terminate(
                NetherBattleResultCodeStepKind.BindingUnavailable,
                "battle-result-code-popup-owner-mismatch:"
                    + popup.OwnerAction + ":" + popup.OwnerGeneration + ":" + popup.Sequence
            );
        }
        _popupWait.Clear();

        NetherRuntimeSnapshotResult snapshotResult = driver.TryCaptureBattleResultCodeSnapshot();
        if (!snapshotResult.IsSuccess)
        {
            return Terminate(
                NetherBattleResultCodeStepKind.BindingUnavailable,
                "battle-result-code-snapshot:" + snapshotResult.Detail
            );
        }
        NetherSnapshot snapshot = snapshotResult.Snapshot!;
        NetherCodeDecision decision = _policy.Decide(
            new NetherCodePortfolio
            {
                CurrentCodes = snapshot.Codes,
                Capacity = snapshot.CodeCapacity,
                ReloadCount = snapshot.CodeReloadCount,
                IsMasterComplete = candidates.IsMasterComplete,
                LockedLane = lockedLane ?? _lockedLane,
            },
            candidates.Candidates,
            settings
        );
        if (decision.Kind == NetherCodeDecisionKind.Pause)
        {
            return Terminate(
                NetherBattleResultCodeStepKind.BindingUnavailable,
                "battle-result-code-policy:" + decision.PauseReason + ":" + decision.Detail
            );
        }

        _lockedLane = decision.LockedLane;
        NetherPlannedAction action = decision.Kind switch
        {
            NetherCodeDecisionKind.Reload => new(NetherActionKind.ReloadCode),
            NetherCodeDecisionKind.Keep => new(NetherActionKind.KeepCode),
            _ => new NetherPlannedAction(NetherActionKind.SelectCode)
            {
                CodeId = decision.SelectedCodeId,
                ReplaceCodeId = decision.RemoveCodeId,
            },
        };
        NetherNativeActionResult invoked = driver.InvokeBattleResultCode(popup, action);
        if (invoked.Kind is not (
                NetherNativeActionResultKind.Started
                or NetherNativeActionResultKind.Completed
            ))
        {
            return Terminate(
                invoked.Kind == NetherNativeActionResultKind.BindingUnavailable
                    ? NetherBattleResultCodeStepKind.BindingUnavailable
                    : NetherBattleResultCodeStepKind.Faulted,
                invoked.Detail
            );
        }

        _nativeInFlight = true;
        return new(
            NetherBattleResultCodeStepKind.AwaitingNative,
            invoked.Detail,
            _lockedLane,
            action
        );
    }

    public void Reset()
    {
        _nativeInFlight = false;
        _completed = false;
        _cancelAfterInFlight = false;
        _lockedLane = null;
        _popupWait.Clear();
    }

    private NetherBattleResultCodeStep Terminate(
        NetherBattleResultCodeStepKind kind,
        string detail
    )
    {
        _nativeInFlight = false;
        _completed = false;
        _cancelAfterInFlight = false;
        _popupWait.Clear();
        return new(kind, detail ?? string.Empty, _lockedLane);
    }
}
