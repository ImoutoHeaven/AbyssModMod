#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Owns Result-scene evidence independently from FloorSelection.  Floor teardown is expected
/// between native Finish and Result construction; it is therefore an observation, never a
/// reason to discard the Result UniTask or its bounded registration wait.
/// </summary>
internal enum NetherResultSceneStepKind
{
    Pending,
    Succeeded,
    Faulted,
    Canceled,
    BindingUnavailable,
}

internal readonly record struct NetherResultSceneStep(NetherResultSceneStepKind Kind, string Detail)
{
    public static NetherResultSceneStep Pending(string detail) => new(NetherResultSceneStepKind.Pending, detail);

    public static NetherResultSceneStep Succeeded(string detail) => new(NetherResultSceneStepKind.Succeeded, detail);

    public static NetherResultSceneStep Faulted(string detail) => new(NetherResultSceneStepKind.Faulted, detail);

    public static NetherResultSceneStep Canceled(string detail) => new(NetherResultSceneStepKind.Canceled, detail);

    public static NetherResultSceneStep BindingUnavailable(string detail) => new(NetherResultSceneStepKind.BindingUnavailable, detail);
}

internal sealed class NetherResultSceneCoordinator
{
    private readonly NetherNativeWaitGate _registrationWait;
    private object? _resultTask;

    public NetherResultSceneCoordinator(int maximumMissingPolls = 600)
    {
        _registrationWait = new NetherNativeWaitGate(maximumMissingPolls);
    }

    public bool HasResultEvidence => _resultTask != null;

    public bool IsResultObserved { get; private set; }

    public bool FloorSelectionTerminated { get; private set; }

    public void ObserveResultTask(object? resultTask)
    {
        IsResultObserved = true;
        if (resultTask == null)
            return;
        _resultTask = resultTask;
        _registrationWait.ObserveRegistration();
    }

    public void ObserveFloorSelectionTerminated() => FloorSelectionTerminated = true;

    public NetherResultSceneStep Pump(Func<object, NetherNativeActionResult> pollTask)
    {
        if (pollTask == null)
            throw new ArgumentNullException(nameof(pollTask));

        if (_resultTask == null)
        {
            NetherNativeActionResult wait = _registrationWait.AwaitRegistration("result");
            return wait.Kind == NetherNativeActionResultKind.Started
                ? NetherResultSceneStep.Pending(wait.Detail)
                : NetherResultSceneStep.BindingUnavailable(wait.Detail);
        }

        NetherNativeActionResult result = pollTask(_resultTask);
        return result.Kind switch
        {
            NetherNativeActionResultKind.Started => NetherResultSceneStep.Pending(result.Detail),
            NetherNativeActionResultKind.Completed => Complete(result.Detail),
            NetherNativeActionResultKind.BindingUnavailable => NetherResultSceneStep.BindingUnavailable(result.Detail),
            NetherNativeActionResultKind.UnknownOutcome when result.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0
                => NetherResultSceneStep.Canceled(result.Detail),
            _ => NetherResultSceneStep.Faulted(result.Detail),
        };
    }

    public void Reset()
    {
        _resultTask = null;
        IsResultObserved = false;
        FloorSelectionTerminated = false;
        _registrationWait.Clear();
    }

    private NetherResultSceneStep Complete(string detail)
    {
        _resultTask = null;
        _registrationWait.Clear();
        return NetherResultSceneStep.Succeeded(detail);
    }
}
