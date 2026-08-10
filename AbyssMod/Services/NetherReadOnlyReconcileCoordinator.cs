#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// The only capability exposed to reconciliation is a native read-only refresh.  It has no
/// Start/Continue/Update mutation member, making an accidental state-changing fallback
/// impossible at this production seam.
/// </summary>
internal interface INetherReadOnlyReconcileDriver
{
    NetherNativeActionResult BeginGetOnlyRefresh();

    NetherNativeActionResult PollGetOnlyRefresh();

    NetherReadOnlySnapshotResult TryCaptureAppliedSnapshot();
}

internal readonly record struct NetherReadOnlySnapshotResult(
    NetherSnapshot? Snapshot,
    string Detail
)
{
    public bool IsSuccess => Snapshot != null;

    public static NetherReadOnlySnapshotResult Success(NetherSnapshot snapshot) => new(snapshot, string.Empty);

    public static NetherReadOnlySnapshotResult Failure(string detail) => new(null, detail);
}

internal enum NetherReadOnlyReconcileStepKind
{
    Pending,
    Applied,
    BindingUnavailable,
    Faulted,
}

internal readonly record struct NetherReadOnlyReconcileStep(
    NetherReadOnlyReconcileStepKind Kind,
    NetherSnapshot? Snapshot,
    string Detail
)
{
    public static NetherReadOnlyReconcileStep Pending(string detail) => new(NetherReadOnlyReconcileStepKind.Pending, null, detail);

    public static NetherReadOnlyReconcileStep Applied(NetherSnapshot snapshot) => new(NetherReadOnlyReconcileStepKind.Applied, snapshot, string.Empty);

    public static NetherReadOnlyReconcileStep BindingUnavailable(string detail) => new(NetherReadOnlyReconcileStepKind.BindingUnavailable, null, detail);

    public static NetherReadOnlyReconcileStep Faulted(string detail) => new(NetherReadOnlyReconcileStepKind.Faulted, null, detail);
}

/// <summary>
/// Drives the real bridge's GET-only native refresh to its UniTask terminal state before
/// exposing a fresh authoritative snapshot to the controller.
/// </summary>
internal sealed class NetherReadOnlyReconcileCoordinator
{
    private readonly INetherReadOnlyReconcileDriver _driver;
    private bool _requestIssued;

    public NetherReadOnlyReconcileCoordinator(INetherReadOnlyReconcileDriver driver)
    {
        _driver = driver ?? throw new System.ArgumentNullException(nameof(driver));
    }

    public bool IsPending => _requestIssued;

    public NetherReadOnlyReconcileStep Pump()
    {
        if (!_requestIssued)
        {
            NetherNativeActionResult begin = _driver.BeginGetOnlyRefresh();
            if (begin.Kind is NetherNativeActionResultKind.Started or NetherNativeActionResultKind.Completed)
            {
                _requestIssued = true;
                return NetherReadOnlyReconcileStep.Pending(begin.Detail);
            }
            return ToTerminal(begin, "read-only-refresh-begin:");
        }

        NetherNativeActionResult poll = _driver.PollGetOnlyRefresh();
        if (poll.Kind == NetherNativeActionResultKind.Started)
            return NetherReadOnlyReconcileStep.Pending(poll.Detail);
        if (poll.Kind != NetherNativeActionResultKind.Completed)
        {
            _requestIssued = false;
            return ToTerminal(poll, "read-only-refresh-poll:");
        }

        _requestIssued = false;
        NetherReadOnlySnapshotResult captured = _driver.TryCaptureAppliedSnapshot();
        return captured.IsSuccess
            ? NetherReadOnlyReconcileStep.Applied(captured.Snapshot!)
            : NetherReadOnlyReconcileStep.BindingUnavailable("read-only-refresh-snapshot:" + captured.Detail);
    }

    public void Reset() => _requestIssued = false;

    private static NetherReadOnlyReconcileStep ToTerminal(NetherNativeActionResult native, string prefix) =>
        native.Kind == NetherNativeActionResultKind.BindingUnavailable
            ? NetherReadOnlyReconcileStep.BindingUnavailable(prefix + native.Detail)
            : NetherReadOnlyReconcileStep.Faulted(prefix + native.Detail);
}
