#nullable enable

namespace AbyssMod.Services;

/// <summary>
/// Converts only the category semantics proven by the packaged MNetherCodes row into the
/// policy model.  It intentionally leaves lane/party/research facts explicit unknown: a zero
/// coverage or false research flag must never be mistaken for master evidence.
/// </summary>
internal static class NetherCodeRuntimeSemanticMapper
{
    public static NetherCodeCandidate MapCandidate(
        long codeId,
        int rawCategory,
        int effectType,
        int level,
        int rarity
    )
    {
        NetherCodeMasterSemantic semantic = NetherCodeCategorySemantics.Resolve(
            codeId,
            rawCategory,
            effectType
        );
        return new NetherCodeCandidate(codeId, semantic.EffectKind, level)
        {
            IsKnown = semantic.IsKnown,
            Category = semantic.Category,
            Rarity = rarity,
            PartyCoverageKnown = false,
            PartyCoverage = 0,
            IsResearchOnlyKnown = false,
            IsResearchOnly = false,
        };
    }

    public static NetherCodeState MapState(
        long codeId,
        int rawCategory,
        int effectType,
        int level,
        int rarity
    )
    {
        NetherCodeMasterSemantic semantic = NetherCodeCategorySemantics.Resolve(
            codeId,
            rawCategory,
            effectType
        );
        return new NetherCodeState(codeId, semantic.EffectKind, level)
        {
            IsKnown = semantic.IsKnown,
            Category = semantic.Category,
            Rarity = rarity,
            PartyCoverageKnown = false,
            PartyCoverage = 0,
            IsResearchOnlyKnown = false,
            IsResearchOnly = false,
        };
    }

    public static bool RequiresBoundedSemanticAudit(NetherCodeCandidate candidate) =>
        candidate != null
        && (candidate.EffectKind == NetherCodeEffectKind.General
            || !candidate.IsKnown);
}
