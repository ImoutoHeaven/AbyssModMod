#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// Gives every native modal an immutable owner/action/generation/sequence identity.  The
/// registry deliberately compares popup instances by reference; close notifications from an
/// old animation can therefore never clear a newly registered popup of the same UI type.
/// </summary>
internal readonly record struct NetherPopupOwnership(
    object Popup,
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence
);

internal sealed class NetherPopupOwnershipRegistry
{
    private NetherActionKind _ownerAction;
    private long _ownerGeneration;
    private long _nextGeneration;
    private long _nextSequence;
    private NetherPopupOwnership? _current;

    public long BeginOwner(NetherActionKind action, long? generation = null)
    {
        if (action == NetherActionKind.None)
            throw new ArgumentOutOfRangeException(nameof(action));
        _ownerAction = action;
        if (generation is long explicitGeneration)
        {
            if (explicitGeneration < 1)
                throw new ArgumentOutOfRangeException(nameof(generation));
            _ownerGeneration = explicitGeneration;
            _nextGeneration = Math.Max(_nextGeneration, explicitGeneration);
        }
        else
        {
            _ownerGeneration = checked(++_nextGeneration);
        }
        _current = null;
        return _ownerGeneration;
    }

    public NetherPopupOwnership Register(object popup, NetherActionKind action, long generation)
    {
        if (popup == null)
            throw new ArgumentNullException(nameof(popup));
        if (action == NetherActionKind.None || action != _ownerAction || generation != _ownerGeneration)
            return default;

        var ownership = new NetherPopupOwnership(popup, action, generation, checked(++_nextSequence));
        _current = ownership;
        return ownership;
    }

    public bool TryGetOwned(NetherActionKind action, long generation, out NetherPopupOwnership ownership)
    {
        if (_current is NetherPopupOwnership candidate
            && candidate.OwnerAction == action
            && candidate.Generation == generation)
        {
            ownership = candidate;
            return true;
        }
        ownership = default;
        return false;
    }

    public void Invalidate(object popup, long sequence)
    {
        if (popup == null || _current is not NetherPopupOwnership candidate)
            return;
        if (candidate.Sequence == sequence && ReferenceEquals(candidate.Popup, popup))
            _current = null;
    }

    public void InvalidateOwner(NetherActionKind action, long generation)
    {
        if (_current is NetherPopupOwnership candidate
            && candidate.OwnerAction == action
            && candidate.Generation == generation)
        {
            _current = null;
        }
    }

    public void Clear()
    {
        _ownerAction = NetherActionKind.None;
        _ownerGeneration = 0;
        _current = null;
    }
}
