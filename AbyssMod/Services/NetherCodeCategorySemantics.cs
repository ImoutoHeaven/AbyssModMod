#nullable enable

using System;

namespace AbyssMod.Services;

/// <summary>
/// A category-derived semantic copied from MNetherCodes.category.  It intentionally separates
/// what the packaged enum proves (category, group, pair, Safe/Risk direction) from the ability
/// semantics it does not prove (Rush/Impact, coverage, research-only).
/// </summary>
internal readonly record struct NetherCodeMasterSemantic(
    NetherCodeCategory Category,
    NetherCodeCategoryGroup Group,
    NetherCodeCategory PairedCategory,
    NetherCodeEffectKind EffectKind,
    bool IsKnown
);

/// <summary>
/// Exact translation of Project.NetherCodeCategoryTypeExtensions:
/// Technique/Strength are the tactics group and pair with each other; ErosionResistance/
/// ErosionEnhancement are the erosion group and pair with each other.  IsExclusive is true
/// only for distinct values in the same group.
/// </summary>
internal static class NetherCodeCategorySemantics
{
    private const long PreferredSafeCodeId = 30024;
    private const long RejectedRiskCodeId = 40024;

    public static NetherCodeMasterSemantic Resolve(long codeId, int rawCategory, int effectType)
    {
        if (codeId <= 0)
            return Unknown();

        // The two documented IDs are explicit policy overrides.  Preserve them even if an old
        // save or partial diagnostic row does not expose a currently valid category.
        if (codeId == PreferredSafeCodeId)
            return Override(rawCategory, NetherCodeEffectKind.Safe);
        if (codeId == RejectedRiskCodeId)
            return Override(rawCategory, NetherCodeEffectKind.Risk);

        if (!Enum.IsDefined(typeof(NetherCodeCategory), rawCategory)
            || rawCategory == (int)NetherCodeCategory.Unknown)
        {
            return Unknown();
        }

        NetherCodeCategory category = (NetherCodeCategory)rawCategory;
        return category switch
        {
            NetherCodeCategory.Technique => Known(
                category,
                NetherCodeCategoryGroup.Tactics,
                NetherCodeCategory.Strength,
                NetherCodeEffectKind.General
            ),
            NetherCodeCategory.Strength => Known(
                category,
                NetherCodeCategoryGroup.Tactics,
                NetherCodeCategory.Technique,
                NetherCodeEffectKind.General
            ),
            NetherCodeCategory.ErosionResistance => Known(
                category,
                NetherCodeCategoryGroup.Erosion,
                NetherCodeCategory.ErosionEnhancement,
                NetherCodeEffectKind.Safe
            ),
            NetherCodeCategory.ErosionEnhancement => Known(
                category,
                NetherCodeCategoryGroup.Erosion,
                NetherCodeCategory.ErosionResistance,
                NetherCodeEffectKind.Risk
            ),
            _ => Unknown(),
        };
    }

    public static NetherCodeCategory GetPairedCategory(NetherCodeCategory category) => category switch
    {
        NetherCodeCategory.Technique => NetherCodeCategory.Strength,
        NetherCodeCategory.Strength => NetherCodeCategory.Technique,
        NetherCodeCategory.ErosionResistance => NetherCodeCategory.ErosionEnhancement,
        NetherCodeCategory.ErosionEnhancement => NetherCodeCategory.ErosionResistance,
        _ => NetherCodeCategory.Unknown,
    };

    public static NetherCodeCategoryGroup GetGroup(NetherCodeCategory category) => category switch
    {
        NetherCodeCategory.Technique or NetherCodeCategory.Strength => NetherCodeCategoryGroup.Tactics,
        NetherCodeCategory.ErosionResistance or NetherCodeCategory.ErosionEnhancement => NetherCodeCategoryGroup.Erosion,
        _ => NetherCodeCategoryGroup.Unknown,
    };

    public static bool IsExclusive(NetherCodeCategory left, NetherCodeCategory right)
    {
        if (left == NetherCodeCategory.Unknown || right == NetherCodeCategory.Unknown || left == right)
            return false;
        NetherCodeCategoryGroup group = GetGroup(left);
        return group != NetherCodeCategoryGroup.Unknown && group == GetGroup(right);
    }

    private static NetherCodeMasterSemantic Override(int rawCategory, NetherCodeEffectKind effect)
    {
        NetherCodeCategory category = Enum.IsDefined(typeof(NetherCodeCategory), rawCategory)
            ? (NetherCodeCategory)rawCategory
            : NetherCodeCategory.Unknown;
        return new NetherCodeMasterSemantic(
            category,
            GetGroup(category),
            GetPairedCategory(category),
            effect,
            true
        );
    }

    private static NetherCodeMasterSemantic Known(
        NetherCodeCategory category,
        NetherCodeCategoryGroup group,
        NetherCodeCategory paired,
        NetherCodeEffectKind effect
    ) => new(category, group, paired, effect, true);

    private static NetherCodeMasterSemantic Unknown() => new(
        NetherCodeCategory.Unknown,
        NetherCodeCategoryGroup.Unknown,
        NetherCodeCategory.Unknown,
        NetherCodeEffectKind.Unknown,
        false
    );
}
