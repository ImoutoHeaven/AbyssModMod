#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal sealed record NetherCodeCandidate(long CodeId, NetherCodeEffectKind EffectKind, int Level)
{
    public bool IsKnown { get; init; } = true;
    public NetherCodeCategory Category { get; init; }
    public int Rarity { get; init; }
    public bool PartyCoverageKnown { get; init; }
    public int PartyCoverage { get; init; }
    public bool IsResearchOnlyKnown { get; init; }
    public bool IsResearchOnly { get; init; }
}

internal sealed record NetherCodePortfolio
{
    public IReadOnlyList<NetherCodeState> CurrentCodes { get; init; } = Array.Empty<NetherCodeState>();
    public int Capacity { get; init; }
    public int ReloadCount { get; init; }
    public bool IsMasterComplete { get; init; }
    public NetherCombatLane? LockedLane { get; init; }
}

internal readonly record struct NetherCodeEffectiveLevels(int Safe, int Risk, int Rush, int Impact);

internal enum NetherCodeDecisionKind
{
    Select,
    Reload,
    Keep,
    Pause,
}

internal sealed record NetherCodeDecision
{
    public NetherCodeDecisionKind Kind { get; init; }
    public long SelectedCodeId { get; init; }
    public long RemoveCodeId { get; init; }
    public NetherCombatLane LockedLane { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<long> ProtectedCodeIds { get; init; } = Array.Empty<long>();
    public IReadOnlyList<long> RemovableCodeIds { get; init; } = Array.Empty<long>();
}

internal sealed class NetherCodePolicy
{
    private const long PreferredSafeCodeId = 30024;
    private const long RejectedRiskCodeId = 40024;

    public NetherCodeDecision Decide(
        NetherCodePortfolio portfolio,
        IReadOnlyList<NetherCodeCandidate> candidates,
        NetherAutoClimbSettings settings
    )
    {
        if (portfolio == null)
            throw new ArgumentNullException(nameof(portfolio));
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (!portfolio.IsMasterComplete || portfolio.Capacity < 1 || portfolio.ReloadCount < 0
            || portfolio.CurrentCodes.Count > portfolio.Capacity
            || portfolio.CurrentCodes.Any(code => !code.IsKnown)
            || candidates.Any(candidate => !candidate.IsKnown))
            return Pause(NetherPauseReason.UnknownMasterData, "incomplete-code-portfolio");

        NetherCodeEffectiveLevels effective = CalculateEffectiveLevels(portfolio.CurrentCodes);
        if (effective.Safe < 0 || effective.Risk < 0 || effective.Rush < 0 || effective.Impact < 0)
            return Pause(NetherPauseReason.UnknownEffect, "code-level-overflow");
        IReadOnlyList<long> protectedIds = BuildProtectedIds(portfolio.CurrentCodes, effective);

        List<NetherCodeCandidate> eligible = candidates
            .Where(candidate => !portfolio.CurrentCodes.Any(current => current.CodeId == candidate.CodeId))
            .Where(candidate => candidate.EffectKind != NetherCodeEffectKind.Risk && candidate.CodeId != RejectedRiskCodeId)
            // The native category extension proves that paired categories are mutually
            // exclusive.  A non-full portfolio has no exact replacement parent to resolve that
            // conflict, so leave it for player control; a full portfolio can name the single
            // conflicting code as the verified replacement target below.
            .Where(candidate => portfolio.CurrentCodes.Count == portfolio.Capacity
                || !HasCategoryConflict(candidate, portfolio.CurrentCodes))
            .ToList();

        // Category is enough to prove that ErosionResistance is a Safe candidate.  A free
        // capacity slot lets us accept that independently of unproven Technique/Strength
        // lane, coverage, or research facts elsewhere in the offer.  This keeps common
        // category-proven Safe codes usable without reintroducing a guessed combat lane.
        if (portfolio.CurrentCodes.Count < portfolio.Capacity)
        {
            NetherCodeCandidate? independentSafe = SelectIndependentSafeCandidate(eligible, effective);
            if (independentSafe != null)
            {
                return Select(
                    independentSafe,
                    removal: 0,
                    lockedLane: PreserveConfiguredLane(portfolio, settings),
                    protectedIds,
                    removable: Array.Empty<NetherCodeState>()
                );
            }
        }

        // Technique/Strength are authoritative selectable categories even though their master
        // rows do not label them Rush/Impact or expose party coverage.  Keep those extra facts
        // explicitly unknown, but rank ordinary offers by the real rarity/level/category data
        // instead of turning a valid native reward into UnknownMasterData.

        // A full portfolio may still replace the one exact paired Risk code for a
        // category-proven Safe code.  That removal is immutable and does not consult unknown
        // coverage/research ranking of another ordinary code in the portfolio.
        if (portfolio.CurrentCodes.Count == portfolio.Capacity)
        {
            NetherCodeCandidate? pairedSafe = SelectIndependentSafeCandidate(eligible, effective);
            if (pairedSafe != null)
            {
                IReadOnlyList<NetherCodeState> pairedConflicts = FindCategoryConflicts(pairedSafe, portfolio.CurrentCodes);
                if (pairedConflicts.Count == 1 && !protectedIds.Contains(pairedConflicts[0].CodeId))
                {
                    return Select(
                        pairedSafe,
                        pairedConflicts[0].CodeId,
                        PreserveConfiguredLane(portfolio, settings),
                        protectedIds,
                        Array.Empty<NetherCodeState>()
                    );
                }
                if (pairedConflicts.Count > 0)
                    return Keep(
                        PreserveConfiguredLane(portfolio, settings),
                        protectedIds,
                        "category-conflict-protected"
                    );
            }
        }

        NetherCombatLane lane = ResolveLane(portfolio, eligible, settings.CombatLane);
        NetherCodeCandidate? selected = SelectCandidate(eligible, effective, lane);
        if (selected != null)
        {
            long removal = 0;
            IReadOnlyList<NetherCodeState> removable = Array.Empty<NetherCodeState>();
            if (portfolio.CurrentCodes.Count == portfolio.Capacity)
            {
                IReadOnlyList<NetherCodeState> conflicts = FindCategoryConflicts(selected, portfolio.CurrentCodes);
                if (conflicts.Count > 0)
                {
                    if (conflicts.Count != 1 || protectedIds.Contains(conflicts[0].CodeId))
                        return Keep(lane, protectedIds, "category-conflict-protected", removable);
                    removal = conflicts[0].CodeId;
                    if (!IsCandidateUpgrade(selected, conflicts[0], lane))
                        return ReloadOrKeep(portfolio, settings, effective, lane, protectedIds, "paired-code-not-an-upgrade");
                }
                else
                {
                    removable = FindRemovableCodes(portfolio.CurrentCodes, protectedIds, lane);
                    if (removable.Count == 0)
                        return Keep(lane, protectedIds, "all-codes-protected");
                    removal = removable[0].CodeId;
                    if (!IsCandidateUpgrade(selected, removable[0], lane))
                        return ReloadOrKeep(portfolio, settings, effective, lane, protectedIds, "candidate-not-an-upgrade", removable);
                }
            }
            return Select(selected, removal, lane, protectedIds, removable);
        }

        if (effective.Safe < 5 && portfolio.ReloadCount > settings.CodeReloadReserve)
        {
            return new NetherCodeDecision
            {
                Kind = NetherCodeDecisionKind.Reload,
                LockedLane = lane,
                ProtectedCodeIds = protectedIds,
                RemovableCodeIds = Array.Empty<long>(),
            };
        }

        IReadOnlyList<NetherCodeState> keepRemovable = FindRemovableCodes(portfolio.CurrentCodes, protectedIds, lane);
        return Keep(lane, protectedIds, "no-safe-code-candidate", keepRemovable);
    }

    public static NetherCodeEffectiveLevels CalculateEffectiveLevels(IReadOnlyList<NetherCodeState> codes)
    {
        if (codes == null)
            throw new ArgumentNullException(nameof(codes));
        try
        {
            int safe = SumLevels(codes, NetherCodeEffectKind.Safe);
            int risk = SumLevels(codes, NetherCodeEffectKind.Risk);
            int rush = SumLevels(codes, NetherCodeEffectKind.Rush);
            int impact = SumLevels(codes, NetherCodeEffectKind.Impact);
            return new NetherCodeEffectiveLevels(
                Math.Max(0, checked(safe - risk)),
                Math.Max(0, checked(risk - safe)),
                Math.Max(0, checked(rush - impact)),
                Math.Max(0, checked(impact - rush))
            );
        }
        catch (OverflowException)
        {
            return new NetherCodeEffectiveLevels(-1, -1, -1, -1);
        }
    }

    private static int SumLevels(IEnumerable<NetherCodeState> codes, NetherCodeEffectKind kind)
    {
        int sum = 0;
        foreach (NetherCodeState code in codes.Where(code => code.EffectKind == kind))
            sum = checked(sum + code.Level);
        return sum;
    }

    private static NetherCombatLane ResolveLane(
        NetherCodePortfolio portfolio,
        IReadOnlyList<NetherCodeCandidate> candidates,
        NetherCombatLane configuredLane
    )
    {
        if (configuredLane != NetherCombatLane.Auto)
            return configuredLane;
        if (portfolio.LockedLane is NetherCombatLane locked && locked != NetherCombatLane.Auto)
            return locked;

        int rushCoverage = Coverage(portfolio.CurrentCodes, candidates, NetherCombatLane.Rush);
        int impactCoverage = Coverage(portfolio.CurrentCodes, candidates, NetherCombatLane.Impact);
        if (rushCoverage == 0 && impactCoverage == 0)
            return NetherCombatLane.Auto;
        return rushCoverage >= impactCoverage ? NetherCombatLane.Rush : NetherCombatLane.Impact;
    }

    private static NetherCombatLane PreserveConfiguredLane(
        NetherCodePortfolio portfolio,
        NetherAutoClimbSettings settings
    ) => portfolio.LockedLane
        ?? (settings.CombatLane == NetherCombatLane.Auto ? NetherCombatLane.Auto : settings.CombatLane);

    private static NetherCodeCandidate? SelectIndependentSafeCandidate(
        IEnumerable<NetherCodeCandidate> candidates,
        NetherCodeEffectiveLevels effective
    ) => candidates
        .Where(candidate => candidate.EffectKind == NetherCodeEffectKind.Safe)
        .OrderByDescending(candidate => candidate.CodeId == PreferredSafeCodeId)
        .ThenByDescending(candidate => SafeAfterCandidate(effective, candidate) >= 5)
        .ThenByDescending(candidate => candidate.Rarity)
        .ThenByDescending(candidate => candidate.Level)
        .ThenBy(candidate => candidate.CodeId)
        .FirstOrDefault();

    private static NetherCodeDecision Select(
        NetherCodeCandidate candidate,
        long removal,
        NetherCombatLane lockedLane,
        IReadOnlyList<long> protectedIds,
        IReadOnlyList<NetherCodeState> removable
    ) => new()
    {
        Kind = NetherCodeDecisionKind.Select,
        SelectedCodeId = candidate.CodeId,
        RemoveCodeId = removal,
        LockedLane = lockedLane,
        ProtectedCodeIds = protectedIds,
        RemovableCodeIds = removable.Select(code => code.CodeId).ToArray(),
    };

    private static int Coverage(
        IEnumerable<NetherCodeState> current,
        IEnumerable<NetherCodeCandidate> candidates,
        NetherCombatLane lane
    ) => current.Where(code => code.PartyCoverageKnown && ToLane(code.EffectKind) == lane).Sum(code => code.PartyCoverage)
        + candidates.Where(code => code.PartyCoverageKnown && ToLane(code.EffectKind) == lane).Sum(code => code.PartyCoverage);

    private static NetherCombatLane? ToLane(NetherCodeEffectKind kind) => kind switch
    {
        NetherCodeEffectKind.Rush => NetherCombatLane.Rush,
        NetherCodeEffectKind.Impact => NetherCombatLane.Impact,
        _ => null,
    };

    private static bool HasCategoryConflict(
        NetherCodeCandidate candidate,
        IReadOnlyList<NetherCodeState> current
    ) => current.Any(code => NetherCodeCategorySemantics.IsExclusive(candidate.Category, code.Category));

    private static IReadOnlyList<NetherCodeState> FindCategoryConflicts(
        NetherCodeCandidate candidate,
        IReadOnlyList<NetherCodeState> current
    ) => current
        .Where(code => NetherCodeCategorySemantics.IsExclusive(candidate.Category, code.Category))
        .OrderBy(code => code.CodeId)
        .ToArray();

    private static IReadOnlyList<long> BuildProtectedIds(
        IReadOnlyList<NetherCodeState> codes,
        NetherCodeEffectiveLevels effective
    )
    {
        var protectedIds = new List<long>();
        foreach (NetherCodeState code in codes)
        {
            if (code.CodeId == PreferredSafeCodeId || (effective.Safe >= 5 && code.EffectKind == NetherCodeEffectKind.Safe))
                protectedIds.Add(code.CodeId);
        }
        return protectedIds;
    }

    private static IReadOnlyList<NetherCodeState> FindRemovableCodes(
        IReadOnlyList<NetherCodeState> codes,
        IReadOnlyList<long> protectedIds,
        NetherCombatLane lane
    ) => codes
        .Where(code => !protectedIds.Contains(code.CodeId))
        .OrderByDescending(code => code.EffectKind == NetherCodeEffectKind.Risk || code.CodeId == RejectedRiskCodeId)
        .ThenByDescending(code => code.IsResearchOnlyKnown && code.IsResearchOnly
            || code.EffectKind == NetherCodeEffectKind.ResearchOnly)
        .ThenByDescending(code => lane != NetherCombatLane.Auto
            && ToLane(code.EffectKind) is NetherCombatLane currentLane
            && currentLane != lane)
        .ThenBy(code => code.Rarity)
        .ThenBy(code => code.Level)
        .ThenBy(code => code.PartyCoverageKnown ? code.PartyCoverage : int.MaxValue)
        .ThenBy(code => code.CodeId)
        .ToArray();

    private static NetherCodeCandidate? SelectCandidate(
        IEnumerable<NetherCodeCandidate> candidates,
        NetherCodeEffectiveLevels effective,
        NetherCombatLane lane
    ) => candidates
        .OrderByDescending(candidate => candidate.CodeId == PreferredSafeCodeId)
        .ThenByDescending(candidate => candidate.EffectKind == NetherCodeEffectKind.Safe && SafeAfterCandidate(effective, candidate) >= 5)
        .ThenByDescending(candidate => candidate.EffectKind == NetherCodeEffectKind.Safe)
        .ThenByDescending(candidate => lane != NetherCombatLane.Auto && ToLane(candidate.EffectKind) == lane)
        .ThenByDescending(candidate => candidate.PartyCoverageKnown ? candidate.PartyCoverage : -1)
        .ThenByDescending(candidate => candidate.Rarity)
        .ThenByDescending(candidate => candidate.Level)
        .ThenBy(candidate => candidate.CodeId)
        .FirstOrDefault();

    private static bool IsCandidateUpgrade(
        NetherCodeCandidate candidate,
        NetherCodeState current,
        NetherCombatLane lane
    )
    {
        if (current.EffectKind == NetherCodeEffectKind.Risk || current.CodeId == RejectedRiskCodeId)
            return true;
        if (candidate.EffectKind == NetherCodeEffectKind.Safe
            && current.EffectKind != NetherCodeEffectKind.Safe)
        {
            return true;
        }
        if (current.EffectKind == NetherCodeEffectKind.Safe
            && candidate.EffectKind != NetherCodeEffectKind.Safe)
        {
            return false;
        }

        if (lane != NetherCombatLane.Auto)
        {
            bool candidateMatches = ToLane(candidate.EffectKind) == lane;
            bool currentMatches = ToLane(current.EffectKind) == lane;
            if (candidateMatches != currentMatches)
                return candidateMatches;
        }
        if (candidate.Rarity != current.Rarity)
            return candidate.Rarity > current.Rarity;
        if (candidate.Level != current.Level)
            return candidate.Level > current.Level;
        if (candidate.PartyCoverageKnown && current.PartyCoverageKnown
            && candidate.PartyCoverage != current.PartyCoverage)
        {
            return candidate.PartyCoverage > current.PartyCoverage;
        }
        if (candidate.IsResearchOnlyKnown && current.IsResearchOnlyKnown
            && candidate.IsResearchOnly != current.IsResearchOnly)
        {
            return !candidate.IsResearchOnly;
        }
        return false;
    }

    private static NetherCodeDecision ReloadOrKeep(
        NetherCodePortfolio portfolio,
        NetherAutoClimbSettings settings,
        NetherCodeEffectiveLevels effective,
        NetherCombatLane lane,
        IReadOnlyList<long> protectedIds,
        string detail,
        IReadOnlyList<NetherCodeState>? removable = null
    ) => effective.Safe < 5 && portfolio.ReloadCount > settings.CodeReloadReserve
        ? new NetherCodeDecision
        {
            Kind = NetherCodeDecisionKind.Reload,
            LockedLane = lane,
            ProtectedCodeIds = protectedIds,
            RemovableCodeIds = removable?.Select(code => code.CodeId).ToArray() ?? Array.Empty<long>(),
            Detail = detail + ":reload",
        }
        : Keep(lane, protectedIds, detail, removable);

    private static int SafeAfterCandidate(NetherCodeEffectiveLevels effective, NetherCodeCandidate candidate) =>
        candidate.EffectKind == NetherCodeEffectKind.Safe ? checked(effective.Safe + candidate.Level) : effective.Safe;

    private static NetherCodeDecision Keep(
        NetherCombatLane lane,
        IReadOnlyList<long> protectedIds,
        string detail,
        IReadOnlyList<NetherCodeState>? removable = null
    ) => new()
    {
        Kind = NetherCodeDecisionKind.Keep,
        LockedLane = lane,
        Detail = detail,
        ProtectedCodeIds = protectedIds,
        RemovableCodeIds = removable?.Select(code => code.CodeId).ToArray() ?? Array.Empty<long>(),
    };

    private static NetherCodeDecision Pause(NetherPauseReason reason, string detail) => new()
    {
        Kind = NetherCodeDecisionKind.Pause,
        PauseReason = reason,
        Detail = detail,
    };
}
