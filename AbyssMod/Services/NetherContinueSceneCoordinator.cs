#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Narrow production seam for the destructive boundary after a one-ticket Sleep continuation.
/// The native Continue parent must finish, its FloorSelection owner must disappear, and a
/// strictly newer NetherTop runtime must register before one GET-only refresh is allowed.
/// Nothing in this component starts, resumes, or repeats a Nether action.
/// </summary>
internal interface INetherContinueSceneDriver : INetherReadOnlyReconcileDriver
{
    /// <summary>Polls only the already-started native Continue parent task.</summary>
    NetherNativeActionResult PollContinueParent();

    /// <summary>
    /// Exact lifecycle evidence for the FloorSelection owner that initiated Continue.  This is
    /// deliberately separate from an incidental absence of a UI controller.
    /// </summary>
    bool FloorOwnerTerminated { get; }

    /// <summary>
    /// Monotonically increasing registration generation for a Nether runtime.  Zero means no
    /// replacement runtime has registered yet.
    /// </summary>
    long CurrentRuntimeGeneration { get; }

    /// <summary>True only for the expected NetherTop/new-segment scene binding.</summary>
    bool IsExpectedNetherTopScene { get; }
}

/// <summary>Immutable server-owned postcondition required after a one-ticket continuation.</summary>
internal readonly record struct NetherContinueSceneContract(
    long ExpectedMapId,
    long ExpectedFloorId,
    int ExpectedSegmentFloorLevel,
    int TicketCost,
    NetherSessionStatus ExpectedStatus
);

internal enum NetherContinueSceneStepKind
{
    WaitForTeardown,
    WaitForRebind,
    Reconcile,
    Complete,
    Pause,
}

internal readonly record struct NetherContinueSceneStep(
    NetherContinueSceneStepKind Kind,
    NetherSnapshot? Snapshot,
    string Detail
)
{
    public static NetherContinueSceneStep WaitForTeardown(string detail) =>
        new(NetherContinueSceneStepKind.WaitForTeardown, null, detail);

    public static NetherContinueSceneStep WaitForRebind(string detail) =>
        new(NetherContinueSceneStepKind.WaitForRebind, null, detail);

    public static NetherContinueSceneStep Reconcile(string detail) =>
        new(NetherContinueSceneStepKind.Reconcile, null, detail);

    public static NetherContinueSceneStep Complete(NetherSnapshot snapshot, string detail) =>
        new(NetherContinueSceneStepKind.Complete, snapshot, detail);

    public static NetherContinueSceneStep Pause(string detail) =>
        new(NetherContinueSceneStepKind.Pause, null, detail);
}

/// <summary>
/// Owns the Continue post-parent scene handoff.  Its terminal step is cached, so polling it
/// after success/failure cannot issue a second GET or accidentally turn a stable observation
/// into another mutation.
/// </summary>
internal sealed class NetherContinueSceneCoordinator
{
    private enum Stage
    {
        Idle,
        AwaitingParent,
        AwaitingTeardown,
        AwaitingRebind,
        Reconciling,
        Terminal,
    }

    private readonly INetherContinueSceneDriver _driver;
    private readonly NetherReadOnlyReconcileCoordinator _reconcile;
    private readonly NetherNativeWaitGate _teardownWait;
    private readonly NetherNativeWaitGate _rebindWait;
    private Stage _stage;
    private NetherContinueSceneContract _contract;
    private NetherSnapshot? _before;
    private long _ownerGeneration;
    private bool _parentTerminalObserved;
    private NetherContinueSceneStep? _terminal;

    public NetherContinueSceneCoordinator(
        INetherContinueSceneDriver driver,
        int maximumMissingTicks = 600
    )
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _reconcile = new NetherReadOnlyReconcileCoordinator(driver);
        _teardownWait = new NetherNativeWaitGate(maximumMissingTicks);
        _rebindWait = new NetherNativeWaitGate(maximumMissingTicks);
    }

    public bool IsActive => _stage is not (Stage.Idle or Stage.Terminal);

    /// <summary>
    /// The native Continue parent has reached a terminal state and the coordinator is now
    /// waiting only for its expected scene transition/rebind.  This is the precise point at
    /// which the controller may enter its explicit handoff phase.
    /// </summary>
    public bool ParentTerminalObserved => _parentTerminalObserved;

    /// <summary>
    /// Captures the immutable pre-mutation evidence before the native Continue parent is
    /// invoked.  Invalid/missing target information cannot be deferred into a mutation.
    /// </summary>
    public bool Begin(
        NetherContinueSceneContract contract,
        NetherSnapshot before,
        long ownerGeneration
    )
    {
        if (IsActive || before == null || !IsValidContract(contract, before, ownerGeneration))
            return false;

        _contract = contract;
        _before = before;
        _ownerGeneration = ownerGeneration;
        _parentTerminalObserved = false;
        _terminal = null;
        _teardownWait.Clear();
        _rebindWait.Clear();
        _reconcile.Reset();
        _stage = Stage.AwaitingParent;
        return true;
    }

    public NetherContinueSceneStep Pump()
    {
        if (_terminal is NetherContinueSceneStep terminal)
            return terminal;
        if (_before == null)
            return NetherContinueSceneStep.Pause("continue-scene-not-started");

        return _stage switch
        {
            Stage.AwaitingParent => PumpParent(),
            Stage.AwaitingTeardown => PumpTeardown(),
            Stage.AwaitingRebind => PumpRebind(),
            Stage.Reconciling => PumpReconcile(),
            _ => TerminalPause("continue-scene-invalid-stage:" + _stage),
        };
    }

    public void Reset()
    {
        _stage = Stage.Idle;
        _before = null;
        _ownerGeneration = 0;
        _parentTerminalObserved = false;
        _terminal = null;
        _teardownWait.Clear();
        _rebindWait.Clear();
        _reconcile.Reset();
    }

    private NetherContinueSceneStep PumpParent()
    {
        NetherNativeActionResult parent = _driver.PollContinueParent();
        if (parent.Kind == NetherNativeActionResultKind.Started)
            return NetherContinueSceneStep.WaitForTeardown("continue-parent-pending:" + parent.Detail);
        if (parent.Kind == NetherNativeActionResultKind.Completed)
        {
            _parentTerminalObserved = true;
            _stage = Stage.AwaitingTeardown;
            return NetherContinueSceneStep.WaitForTeardown("continue-parent-terminal:" + parent.Detail);
        }

        if (parent.Kind == NetherNativeActionResultKind.UnknownOutcome
            && parent.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return TerminalPause("continue-parent-canceled:" + parent.Detail);
        }

        return parent.Kind == NetherNativeActionResultKind.BindingUnavailable
            ? TerminalPause("continue-parent-binding:" + parent.Detail)
            : TerminalPause("continue-parent-fault:" + parent.Detail);
    }

    private NetherContinueSceneStep PumpTeardown()
    {
        if (_driver.FloorOwnerTerminated)
        {
            _teardownWait.ObserveRegistration();
            _stage = Stage.AwaitingRebind;
            return NetherContinueSceneStep.WaitForRebind("continue-floor-owner-terminated");
        }

        NetherNativeActionResult wait = _teardownWait.AwaitRegistration("continue-floor-teardown");
        return wait.Kind == NetherNativeActionResultKind.Started
            ? NetherContinueSceneStep.WaitForTeardown(wait.Detail)
            : TerminalPause("continue-floor-teardown-timeout:" + wait.Detail);
    }

    private NetherContinueSceneStep PumpRebind()
    {
        long generation = _driver.CurrentRuntimeGeneration;
        if (generation == 0)
        {
            NetherNativeActionResult wait = _rebindWait.AwaitRegistration("continue-runtime-rebind");
            return wait.Kind == NetherNativeActionResultKind.Started
                ? NetherContinueSceneStep.WaitForRebind(wait.Detail)
                : TerminalPause("continue-runtime-rebind-timeout:" + wait.Detail);
        }
        if (generation <= _ownerGeneration)
        {
            return TerminalPause(
                "continue-runtime-rebind-wrong-generation:owner=" + _ownerGeneration + ":observed=" + generation
            );
        }
        if (!_driver.IsExpectedNetherTopScene)
            return TerminalPause("continue-runtime-rebind-wrong-scene");

        _rebindWait.ObserveRegistration();
        _stage = Stage.Reconciling;
        return NetherContinueSceneStep.Reconcile("continue-runtime-rebound:" + generation);
    }

    private NetherContinueSceneStep PumpReconcile()
    {
        NetherReadOnlyReconcileStep refresh = _reconcile.Pump();
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.Pending)
            return NetherContinueSceneStep.Reconcile("continue-read-only-refresh-pending:" + refresh.Detail);
        if (refresh.Kind == NetherReadOnlyReconcileStepKind.BindingUnavailable)
            return TerminalPause("continue-read-only-refresh-binding:" + refresh.Detail);
        if (refresh.Kind != NetherReadOnlyReconcileStepKind.Applied || refresh.Snapshot == null)
            return TerminalPause("continue-read-only-refresh-fault:" + refresh.Detail);

        return ValidateSettlement(refresh.Snapshot);
    }

    private NetherContinueSceneStep ValidateSettlement(NetherSnapshot after)
    {
        NetherSnapshot before = _before!;
        if (after.TicketCount != before.TicketCount - _contract.TicketCost)
            return TerminalPause("continue-settlement-wrong-ticket");
        if (after.MapId != _contract.ExpectedMapId)
            return TerminalPause("continue-settlement-wrong-map");
        if (after.CurrentFloorId != _contract.ExpectedFloorId)
            return TerminalPause("continue-settlement-wrong-floor");
        if (after.FloorLevel != _contract.ExpectedSegmentFloorLevel)
            return TerminalPause("continue-settlement-wrong-segment");
        if (after.Status != _contract.ExpectedStatus)
            return TerminalPause("continue-settlement-wrong-status");

        return TerminalComplete(after, "continue-settlement-exact");
    }

    private NetherContinueSceneStep TerminalComplete(NetherSnapshot snapshot, string detail)
    {
        _stage = Stage.Terminal;
        _before = null;
        _reconcile.Reset();
        _terminal = NetherContinueSceneStep.Complete(snapshot, detail);
        return _terminal.Value;
    }

    private NetherContinueSceneStep TerminalPause(string detail)
    {
        _stage = Stage.Terminal;
        _before = null;
        _reconcile.Reset();
        _terminal = NetherContinueSceneStep.Pause(detail);
        return _terminal.Value;
    }

    private static bool IsValidContract(
        NetherContinueSceneContract contract,
        NetherSnapshot before,
        long ownerGeneration
    ) => contract.ExpectedMapId > 0
        && contract.ExpectedFloorId > 0
        && contract.ExpectedSegmentFloorLevel > 0
        && contract.TicketCost == 1
        && contract.ExpectedStatus != NetherSessionStatus.Unknown
        && before.Status == NetherSessionStatus.Sleep
        && before.TicketCount >= contract.TicketCost
        && ownerGeneration > 0;
}
