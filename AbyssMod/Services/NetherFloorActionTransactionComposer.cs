#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

/// <summary>
/// Folds owned modal actions into their original SelectFloor parent.  Native Nether flows can
/// have multiple modal children under one parent (notably Event -> Code Offer), therefore a
/// composition is append-only and the one eventual GET validates every child together.
/// </summary>
internal static class NetherFloorActionTransactionComposer
{
    private readonly record struct CodeFlowRequirements(bool Transform, bool Offer, bool Invalid);

    /// <summary>
    /// Compatibility overload for isolated policy tests and single-stage callers.  Runtime
    /// code must pass both the immutable native owner and current settlement copy below.
    /// </summary>
    public static bool TryCompose(
        NetherPlannedAction parent,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        out NetherPlannedAction composed
    ) => TryCompose(parent, parent, popup, child, out composed);

    /// <summary>
    /// Appends exactly one owned popup stage.  <paramref name="ownerParent"/> remains the
    /// native task owner; <paramref name="settlement"/> is the current immutable evidence
    /// copy.  The two must identify the same floor and pre-status, but only settlement can
    /// carry prior stages.
    /// </summary>
    public static bool TryCompose(
        NetherPlannedAction ownerParent,
        NetherPlannedAction settlement,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        out NetherPlannedAction composed
    )
    {
        composed = default;
        if (!IsValidParent(ownerParent)
            || !HasSameParentIdentity(ownerParent, settlement)
            || popup == null)
        {
            return false;
        }

        // CodeOffer is an owned intermediate of its original SelectFloor parent.  A
        // code-changing Event may itself finish by transitioning the parent into Battle;
        // the nested code choice is still local Play work, but its retained transaction must
        // inherit that root terminal rather than conflict with it.
        NetherSessionStatus terminal = ResolveTerminal(settlement.ExpectedAfterStatus, popup.Kind, child);
        if (!TryGetStages(settlement, out List<NetherFloorPopupStage> stages)
            || terminal == NetherSessionStatus.Unknown
            || (!CanUseTerminal(settlement.ExpectedAfterStatus, terminal)
                && !CanPromoteProvisionalInteractiveParent(settlement, stages, popup, child, terminal)))
        {
            return false;
        }

        if (!TryCreateStage(popup, child, terminal, out NetherFloorPopupStage? stage)
            || stage == null
            || !CanAppend(ownerParent, stages, stage))
        {
            return false;
        }

        stages.Add(stage);
        IReadOnlyList<NetherEffect> retainedEffects = stages
            .Where(value => value.ActionKind == NetherActionKind.SelectEventOption)
            .SelectMany(value => value.ExpectedEffects)
            .ToArray();

        composed = settlement with
        {
            ExpectedAfterStatus = terminal,
            OwnedPopupKind = stage.PopupKind,
            OwnedPopupActionKind = stage.ActionKind,
            OwnedPopupStages = stages.ToArray(),
            // These are retained for existing telemetry and legacy tests.  The ordered stage
            // sequence above is authoritative for reconciliation.
            OptionNumber = stage.OptionNumber,
            ExpectedEffects = retainedEffects,
            ContentId = stage.ContentId,
            ContentAmount = stage.ContentAmount,
            GoldCost = stage.GoldCost,
            CodeId = stage.CodeId,
            ReplaceCodeId = stage.ReplaceCodeId,
        };
        return true;
    }

    /// <summary>
    /// A native SelectFloor parent is not allowed to reach reconciliation while a stage it
    /// itself made necessary is absent.  In particular the Event result's code-change effect
    /// is only settled after a matching CodeOffer selection; issuing GET earlier would turn a
    /// visually closed but unfinished chain into a misleading server result.
    /// </summary>
    public static bool IsCompleteForParentTerminal(NetherPlannedAction settlement)
    {
        if (!IsValidParent(settlement)
            || settlement.ExpectedAfterStatus == NetherSessionStatus.Unknown)
        {
            return false;
        }

        IReadOnlyList<NetherFloorPopupStage> stages = settlement.OwnedPopupStages
            ?? Array.Empty<NetherFloorPopupStage>();
        if (stages.Count == 0)
            return settlement.OwnedPopupKind == NetherRuntimePopupKind.None;

        NetherFloorPopupStage? previous = null;
        foreach (NetherFloorPopupStage stage in stages)
        {
            if (!IsWellFormedStage(stage)
                || stage.ExpectedAfterStatus != settlement.ExpectedAfterStatus)
            {
                return false;
            }
            if (previous != null
                && (previous.OwnerGeneration <= 0
                    || previous.Sequence <= 0
                    || stage.OwnerGeneration != previous.OwnerGeneration
                    || !HasMonotonicStageIdentity(previous, stage)))
            {
                return false;
            }
            previous = stage;
        }

        // RerollAsync is an intermediate mutation of the same live CodeOffer, never a parent
        // settlement.  The owner may reach its terminal only after the bridge has advanced the
        // popup epoch and the controller has appended one exact terminal choice: Receive or
        // the separately observed generated Keep/cancel sequence.  A visual popup close is
        // not represented as either terminal action.
        if (!HasTerminalSelectionAfterEveryReload(stages))
            return false;

        return HasRequiredCodeFlowStages(stages);
    }

    private static bool HasTerminalSelectionAfterEveryReload(
        IReadOnlyList<NetherFloorPopupStage> stages
    )
    {
        int firstCodeStage = -1;
        int firstReload = -1;
        for (int index = 0; index < stages.Count; index++)
        {
            if (stages[index].PopupKind != NetherRuntimePopupKind.CodeOffer)
                continue;
            if (firstCodeStage < 0)
                firstCodeStage = index;
            if (stages[index].ActionKind == NetherActionKind.ReloadCode && firstReload < 0)
            {
                firstReload = index;
            }
        }
        if (firstCodeStage < 0)
            return true;

        // Every CodeOffer chain has exactly one final terminal choice.  A direct Keep at the
        // reload reserve is valid; a reroll run may use strictly increasing epochs before that
        // one Select/Keep.  No later stage may reopen an already-terminal offer.
        int terminalIndex = stages.Count - 1;
        NetherFloorPopupStage terminal = stages[terminalIndex];
        if (terminal.PopupKind != NetherRuntimePopupKind.CodeOffer
            || terminal.ActionKind is not (NetherActionKind.SelectCode or NetherActionKind.KeepCode)
            || stages.Count(stage => stage.PopupKind == NetherRuntimePopupKind.CodeOffer
                && stage.ActionKind is NetherActionKind.SelectCode or NetherActionKind.KeepCode) != 1)
        {
            return false;
        }

        // With no reload, the one direct code terminal is complete as long as it is the final
        // stage; code-change correctness is checked separately below and requires Select.
        if (firstReload < 0)
            return firstCodeStage == terminalIndex;

        NetherFloorPopupStage? previousReload = null;
        for (int index = firstReload; index < terminalIndex; index++)
        {
            NetherFloorPopupStage reload = stages[index];
            if (reload.PopupKind != NetherRuntimePopupKind.CodeOffer
                || reload.ActionKind != NetherActionKind.ReloadCode
                || reload.OwnerGeneration != terminal.OwnerGeneration
                || reload.Sequence != terminal.Sequence)
            {
                return false;
            }
            if (previousReload != null && reload.DecisionEpoch <= previousReload.DecisionEpoch)
                return false;
            previousReload = reload;
        }

        return previousReload != null && terminal.DecisionEpoch > previousReload.DecisionEpoch;
    }

    private static bool IsValidParent(NetherPlannedAction parent) =>
        parent.Kind == NetherActionKind.SelectFloor
        && parent.FloorId > 0
        && parent.FloorLevel >= 1
        && parent.FloorIndex >= 0
        && parent.ExpectedBeforeStatus == NetherSessionStatus.Play;

    private static bool HasSameParentIdentity(NetherPlannedAction owner, NetherPlannedAction settlement) =>
        settlement.Kind == NetherActionKind.SelectFloor
        && settlement.FloorId == owner.FloorId
        && settlement.FloorLevel == owner.FloorLevel
        && settlement.FloorIndex == owner.FloorIndex
        && settlement.ExpectedBeforeStatus == owner.ExpectedBeforeStatus;

    private static NetherSessionStatus GetTerminal(NetherRuntimePopupKind popupKind, NetherPlannedAction child) =>
        popupKind switch
        {
            NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
                when child.Kind == NetherActionKind.SelectEventOption && IsExactEventAction(child) =>
                    child.ExpectedEffects.Any(effect => effect.Kind == NetherEffectKind.Battle)
                        ? NetherSessionStatus.Battle
                        : NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.LeaveShop => NetherSessionStatus.Play,
            NetherRuntimePopupKind.Shop when child.Kind == NetherActionKind.BuyShopItem && IsExactShopBuy(child) =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.SelectCode && child.CodeId > 0 =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.ReloadCode =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeOffer when child.Kind == NetherActionKind.KeepCode =>
                NetherSessionStatus.Play,
            NetherRuntimePopupKind.CodeTransform when child.Kind == NetherActionKind.TransformCode
                && child.ReplaceCodeId > 0 => NetherSessionStatus.Play,
            _ => NetherSessionStatus.Unknown,
        };

    private static NetherSessionStatus ResolveTerminal(
        NetherSessionStatus existingTerminal,
        NetherRuntimePopupKind popupKind,
        NetherPlannedAction child
    )
    {
        NetherSessionStatus intrinsic = GetTerminal(popupKind, child);
        return intrinsic == NetherSessionStatus.Play
            && existingTerminal == NetherSessionStatus.Battle
            && popupKind is NetherRuntimePopupKind.CodeOffer or NetherRuntimePopupKind.CodeTransform
            && child.Kind is NetherActionKind.SelectCode or NetherActionKind.ReloadCode
                or NetherActionKind.KeepCode or NetherActionKind.TransformCode
                ? NetherSessionStatus.Battle
                : intrinsic;
    }

    private static bool CanUseTerminal(NetherSessionStatus existing, NetherSessionStatus next) =>
        existing is NetherSessionStatus.Wait or NetherSessionStatus.Unknown
        || existing == next;

    /// <summary>
    /// Route selection begins with a provisional Play terminal for every non-combat floor.
    /// The only packaged owned-modal flow that can refine that provisional value to Battle is
    /// a first exact Event/Recovery/Treasure option that itself contains the Battle effect.
    /// Keeping this narrow prevents a later stale popup or an unrelated child from rewriting
    /// an established transaction terminal.
    /// </summary>
    private static bool CanPromoteProvisionalInteractiveParent(
        NetherPlannedAction settlement,
        IReadOnlyList<NetherFloorPopupStage> stages,
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        NetherSessionStatus terminal
    ) => stages.Count == 0
        && settlement.OwnedPopupKind == NetherRuntimePopupKind.None
        && settlement.OwnedPopupActionKind == NetherActionKind.None
        && settlement.ExpectedAfterStatus == NetherSessionStatus.Play
        && terminal == NetherSessionStatus.Battle
        && popup.Kind is NetherRuntimePopupKind.Event or NetherRuntimePopupKind.Recovery or NetherRuntimePopupKind.Treasure
        && child.Kind == NetherActionKind.SelectEventOption
        && child.ExpectedEffects.Any(effect => effect.Kind == NetherEffectKind.Battle);

    private static bool TryGetStages(NetherPlannedAction settlement, out List<NetherFloorPopupStage> stages)
    {
        stages = settlement.OwnedPopupStages?.ToList() ?? new List<NetherFloorPopupStage>();
        if (stages.Count != 0)
            return stages.All(IsWellFormedStage);

        // Older state-machine characterization fixtures may supply only scalar legacy fields.
        // Convert those to one stage before appending rather than silently dropping evidence.
        if (settlement.OwnedPopupKind == NetherRuntimePopupKind.None
            && settlement.OwnedPopupActionKind == NetherActionKind.None)
        {
            return true;
        }
        if (settlement.OwnedPopupKind == NetherRuntimePopupKind.None
            || settlement.OwnedPopupActionKind == NetherActionKind.None)
        {
            return false;
        }

        stages.Add(new NetherFloorPopupStage(
            settlement.OwnedPopupKind,
            settlement.OwnedPopupActionKind,
            OwnerGeneration: 0,
            Sequence: 0,
            settlement.ExpectedAfterStatus,
            settlement.OptionNumber,
            settlement.ExpectedEffects?.ToArray() ?? Array.Empty<NetherEffect>(),
            settlement.ContentId,
            settlement.ContentAmount,
            settlement.GoldCost,
            settlement.CodeId,
            settlement.ReplaceCodeId
        ));
        return IsWellFormedStage(stages[0]);
    }

    private static bool TryCreateStage(
        NetherRuntimePopupContext popup,
        NetherPlannedAction child,
        NetherSessionStatus terminal,
        out NetherFloorPopupStage? stage
    )
    {
        stage = null;
        if (popup.OwnerAction != NetherActionKind.None
            && popup.OwnerAction != NetherActionKind.SelectFloor)
        {
            return false;
        }
        if (popup.OwnerGeneration < 0 || popup.Sequence < 0)
            return false;

        stage = new NetherFloorPopupStage(
            popup.Kind,
            child.Kind,
            popup.OwnerGeneration,
            popup.Sequence,
            terminal,
            child.OptionNumber,
            child.ExpectedEffects?.ToArray() ?? Array.Empty<NetherEffect>(),
            child.ContentId,
            child.ContentAmount,
            child.GoldCost,
            child.CodeId,
            child.ReplaceCodeId,
            popup.DecisionEpoch
        );
        return IsWellFormedStage(stage);
    }

    private static bool IsWellFormedStage(NetherFloorPopupStage? stage)
    {
        if (stage == null || stage.PopupKind == NetherRuntimePopupKind.None
            || stage.ActionKind == NetherActionKind.None
            || stage.ExpectedAfterStatus == NetherSessionStatus.Unknown)
        {
            return false;
        }

        NetherPlannedAction child = new(stage.ActionKind)
        {
            OptionNumber = stage.OptionNumber,
            ExpectedEffects = stage.ExpectedEffects,
            ContentId = stage.ContentId,
            ContentAmount = stage.ContentAmount,
            GoldCost = stage.GoldCost,
            CodeId = stage.CodeId,
            ReplaceCodeId = stage.ReplaceCodeId,
        };
        NetherSessionStatus intrinsic = GetTerminal(stage.PopupKind, child);
        return intrinsic == stage.ExpectedAfterStatus
            || (intrinsic == NetherSessionStatus.Play
                && stage.ExpectedAfterStatus == NetherSessionStatus.Battle
                && stage.PopupKind is NetherRuntimePopupKind.CodeOffer or NetherRuntimePopupKind.CodeTransform
                && stage.ActionKind is NetherActionKind.SelectCode or NetherActionKind.ReloadCode
                    or NetherActionKind.KeepCode or NetherActionKind.TransformCode);
    }

    private static bool CanAppend(
        NetherPlannedAction ownerParent,
        IReadOnlyList<NetherFloorPopupStage> stages,
        NetherFloorPopupStage next
    )
    {
        if (stages.Count == 0)
            return true;

        NetherFloorPopupStage previous = stages[^1];
        // A multi-stage production flow has a stamped owner tuple.  Zero is tolerated only
        // for one isolated legacy-stage fixture; it cannot safely grow into a live sequence.
        if (previous.OwnerGeneration <= 0 || previous.Sequence <= 0
            || next.OwnerGeneration <= 0 || next.Sequence <= 0
            || next.OwnerGeneration != previous.OwnerGeneration
            || !HasMonotonicStageIdentity(previous, next))
        {
            return false;
        }

        if (next.ExpectedAfterStatus != previous.ExpectedAfterStatus)
            return false;

        CodeFlowRequirements requirements = GetCodeFlowRequirements(stages);
        if (requirements.Invalid)
            return false;

        // An exact code selection is terminal for the current owned chain.  The only legal
        // same-popup continuation is a Reload run: [Reload e0, Reload e1, ..., Select eN].
        if (previous.PopupKind == NetherRuntimePopupKind.CodeOffer)
        {
            if (previous.ActionKind != NetherActionKind.ReloadCode
                || next.PopupKind != NetherRuntimePopupKind.CodeOffer
                || next.ActionKind is not (NetherActionKind.ReloadCode or NetherActionKind.SelectCode or NetherActionKind.KeepCode))
            {
                return false;
            }
        }

        bool hasEventStage = stages.Any(stage => stage.ActionKind == NetherActionKind.SelectEventOption);
        int transformStages = stages.Count(stage => stage.PopupKind == NetherRuntimePopupKind.CodeTransform
            && stage.ActionKind == NetherActionKind.TransformCode);
        bool hasCodeOfferStage = stages.Any(stage => stage.PopupKind == NetherRuntimePopupKind.CodeOffer);

        if (next.PopupKind == NetherRuntimePopupKind.CodeTransform)
        {
            return requirements.Transform
                && transformStages == 0
                && !hasCodeOfferStage
                && next.ActionKind == NetherActionKind.TransformCode
                && next.ReplaceCodeId > 0;
        }

        if (next.PopupKind == NetherRuntimePopupKind.CodeOffer)
        {
            if (hasEventStage && !requirements.Offer)
                return false;
            if (requirements.Transform && transformStages != 1)
                return false;
            return next.ActionKind is NetherActionKind.ReloadCode
                or NetherActionKind.SelectCode
                or NetherActionKind.KeepCode;
        }

        return false;
    }

    private static bool HasMonotonicStageIdentity(
        NetherFloorPopupStage previous,
        NetherFloorPopupStage next
    )
    {
        if (next.Sequence > previous.Sequence)
            return next.DecisionEpoch == 0;

        // RerollAsync preserves the same CodeOffer instance/registration.  Only the bridge's
        // post-task authoritative refresh can advance an epoch on that exact owner tuple.
        return next.Sequence == previous.Sequence
            && previous.PopupKind == NetherRuntimePopupKind.CodeOffer
            && next.PopupKind == NetherRuntimePopupKind.CodeOffer
            && next.DecisionEpoch > previous.DecisionEpoch;
    }

    private static CodeFlowRequirements GetCodeFlowRequirements(
        IEnumerable<NetherFloorPopupStage> stages
    )
    {
        NetherEffect[] effects = stages
            .Where(stage => stage.ActionKind == NetherActionKind.SelectEventOption)
            .SelectMany(stage => stage.ExpectedEffects)
            .ToArray();
        int transform = effects.Count(effect => effect.Kind == NetherEffectKind.AbyssCodeTransform);
        int offer = effects.Count(effect => effect.Kind == NetherEffectKind.AbyssCodeOffer);
        return new CodeFlowRequirements(transform == 1, offer == 1, transform > 1 || offer > 1);
    }

    private static bool HasRequiredCodeFlowStages(IReadOnlyList<NetherFloorPopupStage> stages)
    {
        CodeFlowRequirements requirements = GetCodeFlowRequirements(stages);
        if (requirements.Invalid || !HasTerminalSelectionAfterEveryReload(stages))
            return false;

        int eventIndex = -1;
        int transformIndex = -1;
        int offerIndex = -1;
        int transformCount = 0;
        for (int index = 0; index < stages.Count; index++)
        {
            NetherFloorPopupStage stage = stages[index];
            if (stage.ActionKind == NetherActionKind.SelectEventOption)
                eventIndex = index;
            if (stage.PopupKind == NetherRuntimePopupKind.CodeTransform)
            {
                if (stage.ActionKind != NetherActionKind.TransformCode || stage.ReplaceCodeId <= 0)
                    return false;
                transformCount++;
                transformIndex = index;
            }
            if (stage.PopupKind == NetherRuntimePopupKind.CodeOffer && offerIndex < 0)
                offerIndex = index;
        }

        if (requirements.Transform != (transformCount == 1))
            return false;
        if (requirements.Offer != (offerIndex >= 0) && eventIndex >= 0)
            return false;
        if (requirements.Transform && transformIndex <= eventIndex)
            return false;
        if (requirements.Offer
            && (offerIndex <= eventIndex || requirements.Transform && offerIndex <= transformIndex))
        {
            return false;
        }
        return true;
    }

    private static bool IsExactEventAction(NetherPlannedAction action) =>
        action.OptionNumber > 0
        && action.ExpectedEffects != null
        && action.ExpectedEffects.Count > 0
        && !HasDuplicateEffectKeys(action.ExpectedEffects)
        && action.ExpectedEffects.All(effect => effect != null
            && effect.Known
            && effect.ContentKnown
            && effect.Kind != NetherEffectKind.Unknown
            && effect.Amount >= 0
            && (effect.Kind != NetherEffectKind.Item || effect.ContentId > 0));

    private static bool HasDuplicateEffectKeys(IReadOnlyList<NetherEffect> effects)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (NetherEffect effect in effects)
        {
            if (effect == null)
                return true;
            string key = effect.Kind switch
            {
                NetherEffectKind.Item => "item:" + effect.ContentId,
                NetherEffectKind.AbyssCodeTransform => "code-transform",
                NetherEffectKind.AbyssCodeOffer => "code-offer",
                _ => "effect:" + (int)effect.Kind,
            };
            if (!keys.Add(key))
                return true;
        }
        return false;
    }

    private static bool IsExactShopBuy(NetherPlannedAction action) =>
        action.ContentId > 0 && action.ContentAmount > 0 && action.GoldCost >= 0;
}
