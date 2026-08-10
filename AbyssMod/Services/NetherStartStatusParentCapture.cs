#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Correlates the generated HandleStartEventByStatusAsync state machine with the code popup
/// it owns. The popup may be registered while MoveNext is running, before the builder's
/// pollable UniTask is exposed by the Harmony postfix.
/// </summary>
internal sealed class NetherStartStatusParentCapture
{
    private object? _stateMachine;
    private object? _controller;
    private object? _parentTask;
    private bool _popupAttached;

    public bool ObserveStateMachineEnter(object stateMachine, object controller)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(controller);

        if (ReferenceEquals(_stateMachine, stateMachine))
            return ReferenceEquals(_controller, controller);
        if (_popupAttached)
            return false;

        _stateMachine = stateMachine;
        _controller = controller;
        _parentTask = null;
        _popupAttached = false;
        return true;
    }

    public bool TryAttachPopup(object controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (_stateMachine == null || !ReferenceEquals(_controller, controller))
            return false;
        _popupAttached = true;
        return true;
    }

    public bool ObserveStateMachineExit(object stateMachine, object parentTask)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(parentTask);
        if (!ReferenceEquals(_stateMachine, stateMachine))
            return false;
        _parentTask = parentTask;
        return true;
    }

    public bool IsReady(object? currentController) =>
        currentController != null
        && _popupAttached
        && _parentTask != null
        && ReferenceEquals(_controller, currentController);

    public bool TryGetParentTask(object? currentController, out object? parentTask)
    {
        parentTask = IsReady(currentController) ? _parentTask : null;
        return parentTask != null;
    }

    public bool TryGetObservedParentTask(object? currentController, out object? parentTask)
    {
        parentTask = HasCandidateFor(currentController) ? _parentTask : null;
        return parentTask != null;
    }

    public bool HasCandidateFor(object? currentController) =>
        currentController != null
        && _stateMachine != null
        && ReferenceEquals(_controller, currentController);

    public bool PopupAttached => _popupAttached;

    public void Clear()
    {
        _stateMachine = null;
        _controller = null;
        _parentTask = null;
        _popupAttached = false;
    }
}
