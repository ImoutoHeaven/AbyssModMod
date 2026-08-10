#nullable enable

using System;
using System.Collections.Generic;

namespace AbyssMod.Services;

/// <summary>Distinct native registrations in the Sleep checkpoint sequence.</summary>
internal enum NetherCheckpointPopupKind
{
    Continue,
    Boost,
    Return,
    ReturnScroll,
}

/// <summary>
/// Immutable registration evidence.  Owner action/generation and a sequence newer than the
/// checkpoint start are required before a popup can ever be clicked.
/// </summary>
internal readonly record struct NetherCheckpointPopupObservation(
    NetherCheckpointPopupKind Kind,
    NetherActionKind OwnerAction,
    long OwnerGeneration,
    long Sequence,
    bool IsLive
);

/// <summary>Only observes the already-started checkpoint parent; it cannot issue a mutation.</summary>
internal interface INetherCheckpointPopupWaitDriver
{
    NetherNativeActionResult PollCheckpointParent();
}

internal enum NetherCheckpointPopupWaitResultKind
{
    Ready,
    Waiting,
    ParentCompletedEarly,
    ParentFaulted,
    ParentCanceled,
    BindingUnavailable,
    Stale,
}

internal readonly record struct NetherCheckpointPopupWaitResult(
    NetherCheckpointPopupWaitResultKind Kind,
    NetherCheckpointPopupObservation? Observation,
    string Detail
)
{
    public static NetherCheckpointPopupWaitResult Ready(NetherCheckpointPopupObservation observation) =>
        new(NetherCheckpointPopupWaitResultKind.Ready, observation, "checkpoint-popup-ready:" + observation.Kind);

    public static NetherCheckpointPopupWaitResult Waiting(string detail) =>
        new(NetherCheckpointPopupWaitResultKind.Waiting, null, detail);

    public static NetherCheckpointPopupWaitResult Terminal(
        NetherCheckpointPopupWaitResultKind kind,
        string detail
    ) => new(kind, null, detail);
}

/// <summary>
/// Bounded, owner-aware waits for the four native checkpoint registrations.  Every wait pumps
/// the original parent in parallel: a parent fault/cancel wins immediately, while a parent
/// completion without the currently required registration is named early-complete evidence and
/// cannot become an infinite <c>Started</c> loop.
/// </summary>
internal sealed class NetherCheckpointPopupWaitCoordinator
{
    private readonly INetherCheckpointPopupWaitDriver _driver;
    private readonly IReadOnlyDictionary<NetherCheckpointPopupKind, NetherNativeWaitGate> _gates;
    private NetherActionKind _ownerAction;
    private long _ownerGeneration;
    private long _minimumSequence;
    private bool _active;

    public NetherCheckpointPopupWaitCoordinator(
        INetherCheckpointPopupWaitDriver driver,
        int maximumMissingPolls = 600
    )
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _gates = new Dictionary<NetherCheckpointPopupKind, NetherNativeWaitGate>
        {
            [NetherCheckpointPopupKind.Continue] = new(maximumMissingPolls),
            [NetherCheckpointPopupKind.Boost] = new(maximumMissingPolls),
            [NetherCheckpointPopupKind.Return] = new(maximumMissingPolls),
            [NetherCheckpointPopupKind.ReturnScroll] = new(maximumMissingPolls),
        };
    }

    public bool IsActive => _active;

    public bool Begin(NetherActionKind ownerAction, long ownerGeneration, long minimumSequence)
    {
        if (_active
            || ownerAction is not (NetherActionKind.Continue or NetherActionKind.FinishAtCheckpoint)
            || ownerGeneration < 1
            || minimumSequence < 0)
        {
            return false;
        }

        _ownerAction = ownerAction;
        _ownerGeneration = ownerGeneration;
        _minimumSequence = minimumSequence;
        _active = true;
        foreach (NetherNativeWaitGate gate in _gates.Values)
            gate.Clear();
        return true;
    }

    public NetherCheckpointPopupWaitResult WaitFor(
        NetherCheckpointPopupKind kind,
        NetherCheckpointPopupObservation? observation
    )
    {
        if (!_active)
        {
            return NetherCheckpointPopupWaitResult.Terminal(
                NetherCheckpointPopupWaitResultKind.BindingUnavailable,
                "checkpoint-popup-wait-not-active"
            );
        }
        if (_ownerAction == NetherActionKind.FinishAtCheckpoint
            && kind is NetherCheckpointPopupKind.Boost or NetherCheckpointPopupKind.Return or NetherCheckpointPopupKind.ReturnScroll)
        {
            return NetherCheckpointPopupWaitResult.Terminal(
                NetherCheckpointPopupWaitResultKind.BindingUnavailable,
                "finish-does-not-own-checkpoint-popup:" + kind
            );
        }

        // Pump first even when the popup has just appeared.  Fault/cancel is terminal evidence
        // and must never be outraced by a stale UI object.  A normal Completed parent is allowed
        // only if the exact current stage registration is present; without it, completion was
        // too early to prove the native sequence advanced correctly.
        NetherNativeActionResult parent = _driver.PollCheckpointParent();
        switch (parent.Kind)
        {
            case NetherNativeActionResultKind.BindingUnavailable:
                return NetherCheckpointPopupWaitResult.Terminal(
                    NetherCheckpointPopupWaitResultKind.BindingUnavailable,
                    "checkpoint-parent-binding:" + parent.Detail
                );
            case NetherNativeActionResultKind.UnknownOutcome:
                return parent.Detail.IndexOf("canceled", StringComparison.OrdinalIgnoreCase) >= 0
                    ? NetherCheckpointPopupWaitResult.Terminal(
                        NetherCheckpointPopupWaitResultKind.ParentCanceled,
                        "checkpoint-parent-canceled:" + parent.Detail
                    )
                    : NetherCheckpointPopupWaitResult.Terminal(
                        NetherCheckpointPopupWaitResultKind.ParentFaulted,
                        "checkpoint-parent-fault:" + parent.Detail
                    );
            case NetherNativeActionResultKind.Rejected:
                return NetherCheckpointPopupWaitResult.Terminal(
                    NetherCheckpointPopupWaitResultKind.ParentFaulted,
                    "checkpoint-parent-rejected:" + parent.Detail
                );
        }

        if (observation is NetherCheckpointPopupObservation candidate)
        {
            if (!Matches(kind, candidate))
            {
                return NetherCheckpointPopupWaitResult.Terminal(
                    NetherCheckpointPopupWaitResultKind.Stale,
                    "stale-checkpoint-popup:" + kind
                );
            }

            _gates[kind].ObserveRegistration();
            return NetherCheckpointPopupWaitResult.Ready(candidate);
        }

        if (parent.Kind == NetherNativeActionResultKind.Completed)
        {
            return NetherCheckpointPopupWaitResult.Terminal(
                NetherCheckpointPopupWaitResultKind.ParentCompletedEarly,
                "checkpoint-parent-completed-before-popup:" + kind
            );
        }
        if (parent.Kind != NetherNativeActionResultKind.Started)
        {
            return NetherCheckpointPopupWaitResult.Terminal(
                NetherCheckpointPopupWaitResultKind.ParentFaulted,
                "checkpoint-parent-unexpected:" + parent.Kind + ":" + parent.Detail
            );
        }

        NetherNativeActionResult wait = _gates[kind].AwaitRegistration(FlowName(kind));
        return wait.Kind == NetherNativeActionResultKind.Started
            ? NetherCheckpointPopupWaitResult.Waiting(wait.Detail)
            : NetherCheckpointPopupWaitResult.Terminal(
                NetherCheckpointPopupWaitResultKind.BindingUnavailable,
                wait.Detail
            );
    }

    public void Reset()
    {
        _active = false;
        _ownerAction = NetherActionKind.None;
        _ownerGeneration = 0;
        _minimumSequence = 0;
        foreach (NetherNativeWaitGate gate in _gates.Values)
            gate.Clear();
    }

    private bool Matches(NetherCheckpointPopupKind expectedKind, NetherCheckpointPopupObservation candidate) =>
        candidate.Kind == expectedKind
        && candidate.IsLive
        && candidate.OwnerAction == _ownerAction
        && candidate.OwnerGeneration == _ownerGeneration
        && candidate.Sequence > _minimumSequence;

    private static string FlowName(NetherCheckpointPopupKind kind) => kind switch
    {
        NetherCheckpointPopupKind.Continue => "checkpoint-continue-popup",
        NetherCheckpointPopupKind.Boost => "checkpoint-boost-popup",
        NetherCheckpointPopupKind.Return => "checkpoint-return-popup",
        NetherCheckpointPopupKind.ReturnScroll => "checkpoint-return-scroll",
        _ => "checkpoint-popup",
    };
}
