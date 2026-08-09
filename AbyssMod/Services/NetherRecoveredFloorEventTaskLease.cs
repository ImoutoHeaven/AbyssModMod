#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Correlates an exact floor-event sequence observed before F12 enable with the one popup it
/// subsequently creates.  A task can be claimed once only, and only by the same live
/// FloorSelection controller generation and exact popup instance/sequence.
/// </summary>
internal sealed class NetherRecoveredFloorEventTaskLease
{
    private object? _controller;
    private object? _task;
    private object? _popup;
    private long _generation;
    private long _popupSequenceBaseline;
    private long _popupSequence;

    public bool ObserveSequence(
        object? controller,
        long generation,
        object? task,
        long popupSequenceBaseline
    )
    {
        if (controller == null || task == null || generation < 1 || popupSequenceBaseline < 0)
            return false;

        Reset();
        _controller = controller;
        _generation = generation;
        _task = task;
        _popupSequenceBaseline = popupSequenceBaseline;
        return true;
    }

    public bool BindPopup(object? popup, long sequence)
    {
        if (_task == null || popup == null || sequence <= _popupSequenceBaseline)
            return false;

        _popup = popup;
        _popupSequence = sequence;
        return true;
    }

    public bool TryClaim(
        object? controller,
        long generation,
        object? popup,
        long sequence,
        out object? task
    )
    {
        task = null;
        if (_task == null
            || controller == null
            || popup == null
            || !ReferenceEquals(_controller, controller)
            || generation != _generation
            || !ReferenceEquals(_popup, popup)
            || sequence != _popupSequence)
        {
            return false;
        }

        task = _task;
        Reset();
        return true;
    }

    public bool InvalidatePopup(object? popup)
    {
        if (popup == null || !ReferenceEquals(_popup, popup))
            return false;
        Reset();
        return true;
    }

    public void Reset()
    {
        _controller = null;
        _task = null;
        _popup = null;
        _generation = 0;
        _popupSequenceBaseline = 0;
        _popupSequence = 0;
    }
}
