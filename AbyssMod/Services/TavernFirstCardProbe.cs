using System;
using System.Collections.Generic;
using System.Linq;
using Absf;
using Project.Api;
using Project.Master;
using Project.Master.NoaMessagePack;

namespace AbyssMod.Services;

internal sealed class TavernFirstCardProbeReport
{
    public IReadOnlyList<TavernCardCandidate> Candidates { get; }
    public int WorkedCount { get; }
    public int SelectedCount { get; }
    public string Error { get; }

    public TavernFirstCardProbeReport(
        IReadOnlyList<TavernCardCandidate> candidates,
        int workedCount,
        int selectedCount,
        string error = ""
    )
    {
        Candidates = candidates;
        WorkedCount = workedCount;
        SelectedCount = selectedCount;
        Error = error;
    }

    public string FormatCandidates()
    {
        if (Candidates.Count == 0)
            return "none";

        return string.Join(
            "; ",
            Candidates.Select(candidate =>
                $"sid={candidate.ServerCardId} master={candidate.MasterCardId} effects="
                + string.Join(
                    "|",
                    candidate.Effects.Select(effect =>
                        $"target={effect.TargetType},type={effect.EffectType},param={effect.EffectParam}"
                    )
                )
            )
        );
    }
}

internal static class TavernFirstCardProbe
{
    private static IReadOnlyDictionary<long, IReadOnlyList<TavernCardEffect>> _effectsByCardId;

    public static TavernFirstCardProbeReport Parse(TavernExecWorkResponseEntity response)
    {
        if (response == null)
            return Error("missing-exec-work-response");
        if (response.tavern_daily_card == null)
            return Error("missing-tavern-daily-card");
        if (response.tavern_cards == null || response.tavern_cards.Length == 0)
        {
            return Error(
                "missing-tavern-cards",
                response.tavern_daily_card.worked_count,
                response.tavern_daily_card.selected_count
            );
        }
        if (!TryGetMasterEffects(out var effectsByCardId, out string error))
        {
            return Error(
                error,
                response.tavern_daily_card.worked_count,
                response.tavern_daily_card.selected_count
            );
        }

        var candidates = new List<TavernCardCandidate>(response.tavern_cards.Length);
        for (int i = 0; i < response.tavern_cards.Length; i++)
        {
            TavernCardsEntity serverCard = response.tavern_cards[i];
            if (serverCard == null)
            {
                return Error(
                    $"null-tavern-card:{i}",
                    response.tavern_daily_card.worked_count,
                    response.tavern_daily_card.selected_count
                );
            }
            if (!effectsByCardId.TryGetValue(serverCard.m_tavern_card_id, out var effects))
            {
                return Error(
                    $"missing-m-tavern-card:{serverCard.m_tavern_card_id}",
                    response.tavern_daily_card.worked_count,
                    response.tavern_daily_card.selected_count
                );
            }

            candidates.Add(
                new TavernCardCandidate(serverCard.id, serverCard.m_tavern_card_id, effects)
            );
        }

        return new TavernFirstCardProbeReport(
            candidates,
            response.tavern_daily_card.worked_count,
            response.tavern_daily_card.selected_count
        );
    }

    private static bool TryGetMasterEffects(
        out IReadOnlyDictionary<long, IReadOnlyList<TavernCardEffect>> effectsByCardId,
        out string error
    )
    {
        if (_effectsByCardId != null)
        {
            effectsByCardId = _effectsByCardId;
            error = string.Empty;
            return true;
        }

        try
        {
            MasterDataStore store = Engine.Get<MasterDataStore>();
            MTavernCards[] cards = store?.GetCache<MTavernCards>();
            MTavernCardEffects[] effects = store?.GetCache<MTavernCardEffects>();
            if (cards == null || cards.Length == 0)
                return Missing(out effectsByCardId, out error, "missing-m-tavern-cards-cache");
            if (effects == null || effects.Length == 0)
                return Missing(out effectsByCardId, out error, "missing-m-tavern-card-effects-cache");

            var effectsById = new Dictionary<long, TavernCardEffect>();
            for (int i = 0; i < effects.Length; i++)
            {
                MTavernCardEffects row = effects[i];
                if (row == null)
                    continue;
                effectsById[row.id] = new TavernCardEffect(
                    row.target_type,
                    row.effect_type,
                    row.effect_param
                );
            }

            var loaded = new Dictionary<long, IReadOnlyList<TavernCardEffect>>();
            for (int i = 0; i < cards.Length; i++)
            {
                MTavernCards row = cards[i];
                if (row == null)
                    continue;

                int[] effectIds =
                [
                    row.m_tavern_card_effect_id_1,
                    row.m_tavern_card_effect_id_2,
                    row.m_tavern_card_effect_id_3,
                ];
                var cardEffects = new List<TavernCardEffect>(effectIds.Length);
                foreach (int effectId in effectIds)
                {
                    if (effectId == 0)
                        continue;
                    if (!effectsById.TryGetValue(effectId, out TavernCardEffect effect))
                    {
                        return Missing(
                            out effectsByCardId,
                            out error,
                            $"missing-m-tavern-card-effect:{effectId}:card={row.id}"
                        );
                    }
                    cardEffects.Add(effect);
                }
                loaded[row.id] = cardEffects;
            }

            _effectsByCardId = loaded;
            effectsByCardId = _effectsByCardId;
            error = string.Empty;
            Logger.Info(
                $"[F11][TavernAutoSL] master catalog loaded, cards={cards.Length}, "
                    + $"effects={effects.Length}"
            );
            return true;
        }
        catch (Exception ex)
        {
            effectsByCardId = null;
            error = $"tavern-master-load-error:{ex.GetType().Name}:{ex.Message}";
            return false;
        }
    }

    private static bool Missing(
        out IReadOnlyDictionary<long, IReadOnlyList<TavernCardEffect>> effectsByCardId,
        out string error,
        string detail
    )
    {
        effectsByCardId = null;
        error = detail;
        return false;
    }

    private static TavernFirstCardProbeReport Error(
        string error,
        int workedCount = 0,
        int selectedCount = 0
    ) => new(Array.Empty<TavernCardCandidate>(), workedCount, selectedCount, error);
}
