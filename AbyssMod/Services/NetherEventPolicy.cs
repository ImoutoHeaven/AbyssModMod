#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace AbyssMod.Services;

internal sealed record NetherEventOption(int OptionNumber, IReadOnlyList<NetherEffect> Effects);

internal enum NetherEventDecisionKind
{
    Select,
    Pause,
}

internal sealed record NetherEventDecision
{
    public NetherEventDecisionKind Kind { get; init; }
    public NetherActionKind ActionKind { get; init; }
    public int OptionNumber { get; init; }
    public long ReplacementCodeId { get; init; }
    public int ProjectedErosion { get; init; }
    public int HpDelta { get; init; }
    public bool StartsBattleAfterSelection { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal readonly record struct NetherShopContent(
    long contentId,
    long itemId,
    int itemType,
    NetherRewardRarity rarity,
    int price,
    bool usesNetherGold,
    int amount = 1,
    bool known = true
)
{
    public long ContentId => contentId;
    public long ItemId => itemId;
    public int ItemType => itemType;
    public NetherRewardRarity Rarity => rarity;
    public int Price => price;
    public bool UsesNetherGold => usesNetherGold;
    public int Amount => amount;
    public bool Known => known;
}

internal enum NetherShopDecisionKind
{
    Leave,
    Buy,
    Pause,
}

internal sealed record NetherShopDecision
{
    public NetherShopDecisionKind Kind { get; init; }
    public long ContentId { get; init; }
    public int Amount { get; init; }
    public NetherPauseReason PauseReason { get; init; }
    public string Detail { get; init; } = string.Empty;
}

internal sealed class NetherEventPolicy
{
    private const long PreferredSafeCodeId = 30024;
    private readonly NetherErosionPolicy _erosionPolicy = new();

    public NetherEventDecision DecideEvent(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings
    ) => Decide(snapshot, options, settings, isRecovery: false);

    public NetherEventDecision DecideRecovery(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings
    ) => Decide(snapshot, options, settings, isRecovery: true);

    public NetherEventDecision DecideTreasure(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings
    )
    {
        ValidateInputs(snapshot, options, settings);
        if (settings.TreasureMode != NetherTreasureMode.KeyOnly)
            return Pause(NetherPauseReason.NoSafeRoute, "treasure-mode-off");

        var candidates = new List<EventCandidate>();
        foreach (NetherEventOption option in options)
        {
            if (!TryValidateOption(option, snapshot, settings, out EventCandidate candidate, out _))
                continue;
            int exactKeyCosts = option.Effects.Count(effect => effect.Kind == NetherEffectKind.TreasureKeyUsed && effect.Amount == 1);
            bool hasOnlySafePayments = option.Effects.All(effect => effect.Kind is not NetherEffectKind.Damage and not NetherEffectKind.Erosion);
            bool hasNoOtherKeyCost = option.Effects.All(effect => effect.Kind != NetherEffectKind.TreasureKeyUsed || effect.Amount == 1);
            if (exactKeyCosts != 1 || !hasNoOtherKeyCost || !hasOnlySafePayments || snapshot.TreasureKeyCount < 1)
                continue;
            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return Pause(NetherPauseReason.NoSafeRoute, "no-key-only-treasure-option");

        EventCandidate selected = candidates
            .OrderByDescending(candidate => candidate.Benefit)
            .ThenBy(candidate => candidate.Option.OptionNumber)
            .First();
        return Select(selected);
    }

    public NetherShopDecision DecideShop(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherShopContent> contents,
        NetherAutoClimbSettings settings
    )
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (contents == null)
            throw new ArgumentNullException(nameof(contents));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (settings.ShopMode == NetherShopMode.Off)
            return new NetherShopDecision { Kind = NetherShopDecisionKind.Leave };
        if (contents.Any(content => !content.Known || content.ContentId <= 0 || content.ItemId <= 0 || content.Amount <= 0 || content.Price < 0))
            return new NetherShopDecision { Kind = NetherShopDecisionKind.Pause, PauseReason = NetherPauseReason.UnknownMasterData, Detail = "invalid-shop-content" };

        NetherShopContent? selected = contents
            .Where(content => content.ItemType == 91)
            .Where(content => content.Rarity >= NetherRewardRarity.Gold)
            .Where(content => content.UsesNetherGold)
            .Where(content => content.Price <= snapshot.NetherGold)
            .OrderByDescending(content => content.Rarity)
            .ThenBy(content => content.Price)
            .ThenBy(content => content.ContentId)
            .Cast<NetherShopContent?>()
            .FirstOrDefault();
        if (selected == null)
            return new NetherShopDecision { Kind = NetherShopDecisionKind.Leave };

        return new NetherShopDecision
        {
            Kind = NetherShopDecisionKind.Buy,
            ContentId = selected.Value.ContentId,
            Amount = selected.Value.Amount,
        };
    }

    private NetherEventDecision Decide(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings,
        bool isRecovery
    )
    {
        ValidateInputs(snapshot, options, settings);
        var candidates = new List<EventCandidate>();
        NetherPauseReason firstRejection = NetherPauseReason.NoSafeRoute;
        string firstDetail = "no-safe-event-option";
        foreach (NetherEventOption option in options)
        {
            if (!TryValidateOption(option, snapshot, settings, out EventCandidate candidate, out NetherEventDecision rejection))
            {
                if (firstRejection == NetherPauseReason.NoSafeRoute)
                {
                    firstRejection = rejection.PauseReason;
                    firstDetail = rejection.Detail;
                }
                continue;
            }

            if (isRecovery && !candidate.HasPositiveOrNeutralRecoveryEffect)
            {
                firstRejection = NetherPauseReason.NoSafeRoute;
                firstDetail = "no-positive-recovery-effect";
                continue;
            }
            candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return Pause(firstRejection, firstDetail);

        bool belowHpSoftLimit = snapshot.Characters.Any(character => character.IsActive && character.HpPermille < settings.MinimumCharacterHpPermille);
        EventCandidate selected = candidates
            .OrderByDescending(candidate => belowHpSoftLimit && candidate.HpDelta > 0)
            .ThenBy(candidate => candidate.ErosionDelta)
            .ThenByDescending(candidate => candidate.HpDelta)
            .ThenByDescending(candidate => candidate.SafeCodeBenefit)
            .ThenByDescending(candidate => candidate.Benefit)
            .ThenBy(candidate => candidate.OptionalBattle)
            .ThenBy(candidate => candidate.Option.OptionNumber)
            .First();
        return Select(selected);
    }

    private bool TryValidateOption(
        NetherEventOption option,
        NetherSnapshot snapshot,
        NetherAutoClimbSettings settings,
        out EventCandidate candidate,
        out NetherEventDecision rejection
    )
    {
        candidate = default;
        rejection = default!;
        if (option == null || option.OptionNumber < 1 || option.Effects == null || option.Effects.Count is < 1 or > 3)
        {
            rejection = Pause(NetherPauseReason.UnknownEffect, "invalid-event-option");
            return false;
        }
        if (option.Effects.Any(effect => !effect.Known || !effect.ContentKnown || effect.Kind == NetherEffectKind.Unknown || effect.Amount < 0))
        {
            rejection = Pause(NetherPauseReason.UnknownEffect, "unknown-event-effect");
            return false;
        }
        if (option.Effects.Count(effect => effect.Kind == NetherEffectKind.AbyssCodeChanged) > 1
            || option.Effects.Any(effect => effect.Kind == NetherEffectKind.AbyssCodeChanged && effect.ReplacementCodeId <= 0))
        {
            rejection = Pause(NetherPauseReason.UnknownEffect, "ambiguous-code-change");
            return false;
        }
        if (option.Effects.Any(effect => effect.Kind == NetherEffectKind.NetherGoldUsed && effect.Amount > snapshot.NetherGold)
            || option.Effects.Any(effect => effect.Kind == NetherEffectKind.TreasureKeyUsed && effect.Amount > snapshot.TreasureKeyCount))
        {
            rejection = Pause(NetherPauseReason.NoSafeRoute, "insufficient-event-resource");
            return false;
        }

        int hpDelta;
        try
        {
            hpDelta = option.Effects.Aggregate(0, (total, effect) => effect.Kind switch
            {
                NetherEffectKind.Heal => checked(total + effect.Amount),
                NetherEffectKind.Damage => checked(total - effect.Amount),
                _ => total,
            });
        }
        catch (OverflowException)
        {
            rejection = Pause(NetherPauseReason.UnknownEffect, "event-hp-overflow");
            return false;
        }
        if (snapshot.Characters.Any(character => character.IsActive && character.HpPermille + hpDelta <= 0))
        {
            rejection = Pause(NetherPauseReason.UnsafeHp, "lethal-event-damage");
            return false;
        }

        NetherErosionProjection erosion = _erosionPolicy.ProjectEffects(
            snapshot.ErosionPoint,
            option.Effects,
            settings.SoftErosionLimit,
            isMandatoryBoss: false
        );
        if (!erosion.IsAllowed)
        {
            rejection = Pause(erosion.PauseReason, erosion.Detail);
            return false;
        }

        int erosionDelta = erosion.ProjectedErosion - snapshot.ErosionPoint;
        long replacement = option.Effects.FirstOrDefault(effect => effect.Kind == NetherEffectKind.AbyssCodeChanged)?.ReplacementCodeId ?? 0;
        bool startsBattle = option.Effects.Any(effect => effect.Kind == NetherEffectKind.Battle);
        bool optionalBattle = option.Effects.Any(effect => effect.Kind == NetherEffectKind.Battle && effect.IsOptionalBattle);
        int benefit = option.Effects.Count(effect => effect.Kind is NetherEffectKind.Item or NetherEffectKind.NetherGoldGain or NetherEffectKind.TreasureKeyGain);
        candidate = new EventCandidate(
            option,
            erosion.ProjectedErosion,
            erosionDelta,
            hpDelta,
            replacement,
            replacement == PreferredSafeCodeId ? 1 : 0,
            benefit,
            startsBattle,
            optionalBattle
        );
        return true;
    }

    private static NetherEventDecision Select(EventCandidate candidate) => new()
    {
        Kind = NetherEventDecisionKind.Select,
        ActionKind = NetherActionKind.SelectEventOption,
        OptionNumber = candidate.Option.OptionNumber,
        ReplacementCodeId = candidate.ReplacementCodeId,
        ProjectedErosion = candidate.ProjectedErosion,
        HpDelta = candidate.HpDelta,
        StartsBattleAfterSelection = candidate.StartsBattle,
    };

    private static NetherEventDecision Pause(NetherPauseReason reason, string detail) => new()
    {
        Kind = NetherEventDecisionKind.Pause,
        PauseReason = reason,
        Detail = detail,
    };

    private static void ValidateInputs(
        NetherSnapshot snapshot,
        IReadOnlyList<NetherEventOption> options,
        NetherAutoClimbSettings settings
    )
    {
        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));
        if (options == null)
            throw new ArgumentNullException(nameof(options));
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
    }

    private readonly record struct EventCandidate(
        NetherEventOption Option,
        int ProjectedErosion,
        int ErosionDelta,
        int HpDelta,
        long ReplacementCodeId,
        int SafeCodeBenefit,
        int Benefit,
        bool StartsBattle,
        bool OptionalBattle
    )
    {
        public bool HasPositiveOrNeutralRecoveryEffect => ErosionDelta <= 0 && HpDelta >= 0 && (ErosionDelta < 0 || HpDelta > 0 || SafeCodeBenefit > 0 || Benefit > 0);
    }
}
