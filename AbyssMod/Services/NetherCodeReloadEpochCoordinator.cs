#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AbyssMod.Services;

/// <summary>
/// Identity of the one live CodeOffer which owns a RerollAsync request.  Reroll does not
/// construct a second popup: its controller rebuilds the same popup's model after the server
/// response.  A generation/sequence tuple therefore remains immutable while only this
/// coordinator's decision epoch may advance.
/// </summary>
internal readonly record struct NetherCodeReloadEpochOwner(
    NetherActionKind OwnerAction,
    long Generation,
    long Sequence
);

internal readonly record struct NetherCodeReloadEpochRefresh(
    NetherCodeReloadEpochOwner Owner,
    int ReloadCount,
    NetherRuntimeCodeCandidatesResult Candidates
);

internal enum NetherCodeReloadEpochStage
{
    Idle,
    AwaitingRerollTask,
    AwaitingRefresh,
    Ready,
    Faulted,
}

/// <summary>
/// Makes the exact native <c>RerollAsync</c> child a bounded intermediate step.  It advances a
/// same-popup decision epoch only after the returned UniTask has ended and a fresh model proves
/// both a changed offer and exactly one consumed reload.  It never invokes a reload itself, so
/// fault/off/owner loss can only pause the already-started action rather than replay it.
/// </summary>
internal sealed class NetherCodeReloadEpochCoordinator
{
    private readonly int _maximumPendingPumps;
    private NetherCodeReloadEpochOwner? _owner;
    private int _beforeReloadCount;
    private string _beforeFingerprint = string.Empty;
    private string _faultDetail = string.Empty;
    private int _pendingPumps;

    public NetherCodeReloadEpochCoordinator(int maximumPendingPumps = 600)
    {
        if (maximumPendingPumps < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumPendingPumps));
        _maximumPendingPumps = maximumPendingPumps;
    }

    public NetherCodeReloadEpochStage Stage { get; private set; } = NetherCodeReloadEpochStage.Idle;

    /// <summary>Monotonic only for the current owner; ClearFloorParent resets it.</summary>
    public long DecisionEpoch { get; private set; }

    public bool IsActive => Stage is NetherCodeReloadEpochStage.AwaitingRerollTask
        or NetherCodeReloadEpochStage.AwaitingRefresh;

    public NetherCodeReloadEpochOwner? Owner => _owner;

    public bool Begin(
        NetherCodeReloadEpochOwner owner,
        int reloadCount,
        NetherRuntimeCodeCandidatesResult candidates
    )
    {
        if (owner.OwnerAction != NetherActionKind.SelectFloor
            || owner.Generation <= 0
            || owner.Sequence <= 0
            || reloadCount <= 0
            || !TryCreateFingerprint(candidates, out string fingerprint))
        {
            return false;
        }

        // A second exact reroll is possible only after a new decision has been reached for
        // this same live offer.  A stale/parallel owner may never steal the epoch.
        if (Stage == NetherCodeReloadEpochStage.Ready)
        {
            if (_owner is not NetherCodeReloadEpochOwner current || current != owner)
                return false;
        }
        else if (Stage != NetherCodeReloadEpochStage.Idle)
        {
            return false;
        }

        _owner = owner;
        _beforeReloadCount = reloadCount;
        _beforeFingerprint = fingerprint;
        _faultDetail = string.Empty;
        _pendingPumps = 0;
        Stage = NetherCodeReloadEpochStage.AwaitingRerollTask;
        return true;
    }

    /// <summary>
    /// Polls the already-invoked child then, on a distinct pump, reads the current native
    /// model.  Completion is intentionally reported only after that read; callers must yield a
    /// frame so the runtime-flow coordinator sees the incremented epoch before parent polling.
    /// </summary>
    public NetherNativeActionResult Pump(
        Func<NetherNativeActionResult> pollRerollTask,
        Func<NetherCodeReloadEpochRefresh> captureFreshOffer
    )
    {
        if (pollRerollTask == null)
            throw new ArgumentNullException(nameof(pollRerollTask));
        if (captureFreshOffer == null)
            throw new ArgumentNullException(nameof(captureFreshOffer));

        switch (Stage)
        {
            case NetherCodeReloadEpochStage.AwaitingRerollTask:
            {
                NetherNativeActionResult child = pollRerollTask();
                if (child.Kind == NetherNativeActionResultKind.Started)
                {
                    if (++_pendingPumps > _maximumPendingPumps)
                    {
                        return Fault(
                            "code-reload-timeout",
                            NetherNativeActionResult.BindingUnavailable("pending-pump-limit")
                        );
                    }
                    return NetherNativeActionResult.Started("code-reload-awaiting-reroll-task");
                }
                if (child.Kind != NetherNativeActionResultKind.Completed)
                    return Fault("code-reload-task", child);

                Stage = NetherCodeReloadEpochStage.AwaitingRefresh;
                return NetherNativeActionResult.Started("code-reload-task-complete-awaiting-fresh-offer");
            }
            case NetherCodeReloadEpochStage.AwaitingRefresh:
            {
                NetherCodeReloadEpochRefresh refresh = captureFreshOffer();
                if (_owner is not NetherCodeReloadEpochOwner owner || refresh.Owner != owner)
                    return Fault("code-reload-owner", NetherNativeActionResult.BindingUnavailable("stale-or-missing-code-offer"));
                if (refresh.ReloadCount != _beforeReloadCount - 1)
                    return Fault("code-reload-count", NetherNativeActionResult.UnknownOutcome("expected-exactly-one-consumed"));
                if (!TryCreateFingerprint(refresh.Candidates, out string fingerprint))
                    return Fault("code-reload-candidates", NetherNativeActionResult.BindingUnavailable("unknown-or-invalid-fresh-offer"));
                if (string.Equals(fingerprint, _beforeFingerprint, StringComparison.Ordinal))
                    return Fault("code-reload-candidates", NetherNativeActionResult.UnknownOutcome("unchanged-fresh-offer"));

                try
                {
                    DecisionEpoch = checked(DecisionEpoch + 1);
                }
                catch (OverflowException)
                {
                    return Fault("code-reload-epoch", NetherNativeActionResult.BindingUnavailable("epoch-overflow"));
                }
                Stage = NetherCodeReloadEpochStage.Ready;
                return NetherNativeActionResult.Completed("code-reload-fresh-offer-ready");
            }
            case NetherCodeReloadEpochStage.Ready:
                return NetherNativeActionResult.Completed("code-reload-offer-ready");
            case NetherCodeReloadEpochStage.Faulted:
                return NetherNativeActionResult.BindingUnavailable(
                    _faultDetail.Length == 0 ? "code-reload-faulted" : _faultDetail
                );
            default:
                return NetherNativeActionResult.BindingUnavailable("code-reload-not-started");
        }
    }

    public bool IsOwner(NetherCodeReloadEpochOwner owner) =>
        _owner is NetherCodeReloadEpochOwner current && current == owner;

    public long GetDecisionEpoch(NetherCodeReloadEpochOwner owner) =>
        IsOwner(owner) ? DecisionEpoch : 0;

    public void Reset()
    {
        _owner = null;
        _beforeReloadCount = 0;
        _beforeFingerprint = string.Empty;
        _faultDetail = string.Empty;
        _pendingPumps = 0;
        DecisionEpoch = 0;
        Stage = NetherCodeReloadEpochStage.Idle;
    }

    private NetherNativeActionResult Fault(string phase, NetherNativeActionResult result)
    {
        Stage = NetherCodeReloadEpochStage.Faulted;
        _faultDetail = phase + ":" + result.Kind + ":" + result.Detail;
        return NetherNativeActionResult.BindingUnavailable(_faultDetail);
    }

    private static bool TryCreateFingerprint(
        NetherRuntimeCodeCandidatesResult result,
        out string fingerprint
    )
    {
        fingerprint = string.Empty;
        if (!result.IsSuccess || !result.IsMasterComplete || result.Candidates == null || result.Candidates.Count == 0)
            return false;

        NetherCodeCandidate[] candidates = result.Candidates.OrderBy(candidate => candidate.CodeId).ToArray();
        if (candidates.Any(candidate => candidate == null
                || candidate.CodeId <= 0
                || !candidate.IsKnown)
            || candidates.GroupBy(candidate => candidate.CodeId).Any(group => group.Count() != 1))
        {
            return false;
        }

        var builder = new StringBuilder(candidates.Length * 32);
        foreach (NetherCodeCandidate candidate in candidates)
        {
            builder.Append(candidate.CodeId).Append(':')
                .Append((int)candidate.EffectKind).Append(':')
                .Append(candidate.Level).Append(':')
                .Append((int)candidate.Category).Append(':')
                .Append(candidate.Rarity).Append(':')
                .Append(candidate.PartyCoverageKnown ? '1' : '0').Append(':')
                .Append(candidate.PartyCoverage).Append(':')
                .Append(candidate.IsResearchOnlyKnown ? '1' : '0').Append(':')
                .Append(candidate.IsResearchOnly ? '1' : '0').Append(';');
        }
        fingerprint = builder.ToString();
        return true;
    }
}
