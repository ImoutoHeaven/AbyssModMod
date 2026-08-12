using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Project.Api;

namespace AbyssMod.Services;

/// <summary>
/// Correlates the exact battle-session response accepted by Auto-SL with the
/// stage_results payload generated when the client later settles that battle.
/// </summary>
public static class BattleSettlementPayloadTrace
{
    private const int LogChunkSize = 6000;
    private static readonly object Sync = new();
    private static long _nextTraceId;
    private static AcceptedSnapshot _accepted;

    public static void CaptureAccepted(
        string mode,
        string source,
        int attempt,
        BattleSessionStatusResponseEntity response,
        IReadOnlyList<BattleDropItem> rootItems,
        IReadOnlyList<BattleDropItem> targets
    )
    {
        if (response == null)
            return;

        var rootBySid = new Dictionary<long, BattleDropItem>();
        foreach (BattleDropItem item in rootItems)
            rootBySid[item.Sid] = item;

        var targetSids = new List<long>(targets.Count);
        foreach (BattleDropItem target in targets)
            targetSids.Add(target.Sid);

        bool isExploration = mode == "exploration" || mode == "idle-exploration";
        ExplorationStageDropAnalysis dropAnalysis = isExploration
            ? ExplorationStageDropReachability.Parse(response.stage_detail ?? string.Empty)
            : null;
        IReadOnlyList<ExplorationTreasureChest> targetChests = dropAnalysis
            ?.FindActiveTargetChests(targetSids)
            ?? Array.Empty<ExplorationTreasureChest>();
        IReadOnlyList<ExplorationTreasureRankReward> targetRankRewards = dropAnalysis
            ?.FindActiveTargetRankRewards(targetSids)
            ?? Array.Empty<ExplorationTreasureRankReward>();

        AcceptedSnapshot snapshot;
        lock (Sync)
        {
            snapshot = new AcceptedSnapshot(
                ++_nextTraceId,
                mode,
                source,
                attempt,
                response.status,
                response.quest_type,
                response.quest_id,
                response.stage_type,
                response.start_at ?? string.Empty,
                rootBySid,
                targetSids,
                targetChests,
                targetRankRewards
            );
            _accepted = snapshot;
        }

        string stageDetail = response.stage_detail ?? string.Empty;
        string sessionDetail = response.session_detail ?? string.Empty;
        Logger.Info(
            $"[F11][SettlementProbe][Accepted] traceId={snapshot.TraceId}, "
                + $"mode={mode}, source={source}, attempt={attempt}, status={response.status}, "
                + $"questType={response.quest_type}, questId={response.quest_id}, "
                + $"stageType={response.stage_type}, startAt={snapshot.StartAt}, "
                + $"rootDrops={rootItems.Count}, targetSids={FormatArray(targetSids)}, "
                + $"targetChests={FormatChests(targetChests)}, "
                + $"targetRankRewards={FormatRankRewards(targetRankRewards)}"
        );
        LogPayloadChunks(snapshot.TraceId, "accepted-stage-detail", stageDetail);
        LogPayloadChunks(snapshot.TraceId, "accepted-session-detail", sessionDetail);
    }

    public static void CaptureAcceptedNether(
        string source,
        int attempt,
        BattleSessionStatusResponseEntity response,
        IReadOnlyList<BattleDropItem> rootItems,
        IReadOnlyList<NetherTargetDrop> targets
    )
    {
        var targetItems = new List<BattleDropItem>(targets.Count);
        foreach (NetherTargetDrop target in targets)
            targetItems.Add(target.Drop);

        CaptureAccepted("nether", source, attempt, response, rootItems, targetItems);
    }

    public static void CompleteAndLogExplorationStageResults(ExplorationStageResults results)
    {
        if (results == null)
        {
            Logger.Warn("[F11][SettlementProbe][ClearPayload] stage_results=null");
            return;
        }

        long[] destroyedEnemies = Copy(results.destroyed_enemies);
        long[] dropItems = Copy(results.drop_items);
        long[] passedFloorParts = Copy(results.passed_floor_parts);
        AcceptedSnapshot snapshot;
        lock (Sync)
            snapshot = _accepted;

        if (Config.BattleSessionAutoSL.Value
            && snapshot != null
            && (snapshot.Mode == "exploration" || snapshot.Mode == "idle-exploration")
            && (snapshot.TargetChests.Count != 0
                || snapshot.TargetRankRewards.Count != 0))
        {
            ExplorationTreasureDropCompletion completion =
                ExplorationTreasureDropCompleter.Complete(
                    dropItems,
                    passedFloorParts,
                    snapshot.TargetChests,
                    snapshot.TargetRankRewards
                );
            var passed = new HashSet<long>(passedFloorParts);
            bool targetRewardPassed = snapshot.TargetChests.Any(chest =>
                    passed.Contains(chest.FloorSid))
                || snapshot.TargetRankRewards.Any(reward =>
                    passed.Contains(reward.FloorSid));
            string outcome = completion.AddedDropSids.Count != 0
                ? "completed"
                : targetRewardPassed
                    ? "already-complete"
                    : "skipped-unpassed";
            Logger.Info(
                $"[F11][SettlementProbe][DropCompletion] traceId={snapshot.TraceId}, "
                    + $"outcome={outcome}, targetChests={FormatChests(snapshot.TargetChests)}, "
                    + $"targetRankRewards={FormatRankRewards(snapshot.TargetRankRewards)}, "
                    + $"passedFloorParts={FormatArray(passedFloorParts)}, "
                    + $"addedDropSids={FormatArray(completion.AddedDropSids)}, "
                    + $"completedResourceSids={FormatArray(completion.CompletedResourceSids)}, "
                    + $"completedRankRewards={FormatRankRewards(completion.CompletedRankRewards)}, "
                    + $"before={dropItems.Length}, after={completion.DropSids.Count}"
            );
            if (completion.AddedDropSids.Count != 0)
            {
                dropItems = completion.DropSids.ToArray();
                results.drop_items = new Il2CppStructArray<long>(dropItems);
            }
        }

        string payload =
            "{\"stage_results\":{"
            + $"\"play_time\":{results.play_time},"
            + $"\"destroyed_enemies\":{FormatArray(destroyedEnemies)},"
            + $"\"drop_items\":{FormatArray(dropItems)},"
            + $"\"mana_gem_count\":{results.mana_gem_count},"
            + $"\"top_fc_damage\":{results.top_fc_damage},"
            + $"\"passed_floor_parts\":{FormatArray(passedFloorParts)},"
            + $"\"clear_rank\":{results.clear_rank}"
            + "}}";

        LogClearPayload("exploration", payload, dropItems);
    }

    public static void LogDisasterStageResults(DisasterStageResults results)
    {
        if (results == null)
        {
            Logger.Warn("[F11][SettlementProbe][ClearPayload] disaster_stage_results=null");
            return;
        }

        long[] destroyedEnemies = Copy(results.destroyed_enemies);
        long[] dropItems = Copy(results.drop_items);
        string payload = "{\"stage_results\":{"
            + $"\"play_time\":{results.play_time},"
            + $"\"destroyed_enemies\":{FormatArray(destroyedEnemies)},"
            + $"\"drop_items\":{FormatArray(dropItems)},"
            + $"\"mana_gem_count\":{results.mana_gem_count},"
            + $"\"top_fc_damage\":{results.top_fc_damage}"
            + "}}";

        LogClearPayload("disaster", payload, dropItems);
    }

    public static void LogFinishResponse(IFinishQuestResponseEntity entity)
    {
        if (entity == null)
        {
            Logger.Warn("[F11][SettlementProbe][ClearResponse] entity=null");
            return;
        }

        AcceptedSnapshot snapshot;
        lock (Sync)
            snapshot = _accepted;

        IFinishQuestResponseEntity.IDrop dropView =
            entity.TryCast<IFinishQuestResponseEntity.IDrop>();
        if (dropView == null)
        {
            Logger.Info(
                $"[F11][SettlementProbe][ClearResponse] traceId={snapshot?.TraceId ?? 0}, "
                    + $"resultType={entity.ResultType}, questType={entity.QuestType}, drops=unsupported"
            );
            ClearAccepted(snapshot);
            return;
        }

        var drops = dropView.Drops;
        var builder = new StringBuilder();
        int count = drops?.Length ?? 0;
        for (int i = 0; i < count; i++)
        {
            DropContentEntity item = drops[i];
            if (i > 0)
                builder.Append("; ");
            if (item == null)
            {
                builder.Append("null");
                continue;
            }

            builder.Append("contentType=").Append(item.content_type)
                .Append(" contentId=").Append(item.content_id)
                .Append(" amount=").Append(item.amount)
                .Append(" bonusType=").Append(item.bonus_type)
                .Append(" isRare=").Append(item.is_rare_drop);

            AdditionalInfo additional = item.additional_info;
            if (additional != null)
            {
                builder.Append(" tWeaponId=").Append(additional.t_weapon_id)
                    .Append(" tArmorId=").Append(additional.t_armor_id)
                    .Append(" tAccessoryId=").Append(additional.t_accessory_id);
            }
        }

        Logger.Info(
            $"[F11][SettlementProbe][ClearResponse] traceId={snapshot?.TraceId ?? 0}, "
                + $"resultType={entity.ResultType}, questType={entity.QuestType}, "
                + $"dropCount={count}, drops={builder}"
        );
        ClearAccepted(snapshot);
    }

    private static void ClearAccepted(AcceptedSnapshot snapshot)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_accepted, snapshot))
                _accepted = null;
        }
    }

    private static void LogClearPayload(string resultType, string payload, long[] dropItems)
    {
        AcceptedSnapshot snapshot;
        lock (Sync)
            snapshot = _accepted;

        long traceId = snapshot?.TraceId ?? 0;
        Logger.Info(
            $"[F11][SettlementProbe][ClearPayload] traceId={traceId}, "
                + $"resultType={resultType}, payload={payload}"
        );

        if (snapshot == null)
        {
            Logger.Warn(
                $"[F11][SettlementProbe][Correlation] traceId=0, "
                    + $"payloadDropSids={FormatArray(dropItems)}, acceptedSnapshot=missing"
            );
            return;
        }

        var payloadSids = new HashSet<long>(dropItems);
        var matchedTargets = new List<long>();
        var missingTargets = new List<long>();
        foreach (long targetSid in snapshot.TargetSids)
        {
            if (payloadSids.Contains(targetSid))
                matchedTargets.Add(targetSid);
            else
                missingTargets.Add(targetSid);
        }

        var unresolvedPayloadSids = new List<long>();
        foreach (long sid in dropItems)
        {
            if (!snapshot.RootBySid.ContainsKey(sid))
                unresolvedPayloadSids.Add(sid);
        }

        Logger.Info(
            $"[F11][SettlementProbe][Correlation] traceId={snapshot.TraceId}, "
                + $"acceptedMode={snapshot.Mode}, acceptedSource={snapshot.Source}, "
                + $"acceptedAttempt={snapshot.Attempt}, acceptedQuestId={snapshot.QuestId}, "
                + $"acceptedStageType={snapshot.StageType}, rootDropCount={snapshot.RootBySid.Count}, "
                + $"acceptedTargetSids={FormatArray(snapshot.TargetSids)}, "
                + $"payloadDropSids={FormatArray(dropItems)}, "
                + $"matchedTargetSids={FormatArray(matchedTargets)}, "
                + $"missingTargetSids={FormatArray(missingTargets)}, "
                + $"unresolvedPayloadSids={FormatArray(unresolvedPayloadSids)}"
        );
        Logger.Info(
            $"[F11][SettlementProbe][PayloadItems] traceId={snapshot.TraceId}, "
                + $"items={FormatResolvedItems(dropItems, snapshot.RootBySid)}"
        );
    }

    private static string FormatResolvedItems(
        IReadOnlyList<long> sids,
        IReadOnlyDictionary<long, BattleDropItem> rootBySid
    )
    {
        var builder = new StringBuilder();
        for (int i = 0; i < sids.Count; i++)
        {
            if (i > 0)
                builder.Append("; ");

            long sid = sids[i];
            builder.Append("sid=").Append(sid);
            if (!rootBySid.TryGetValue(sid, out BattleDropItem item))
            {
                builder.Append(" unresolved=1");
                continue;
            }

            builder.Append(" contentType=").Append(item.ContentType)
                .Append(" contentId=").Append(item.ContentId)
                .Append(" amount=").Append(item.Amount)
                .Append(" rarity=").Append(item.RarityLevel)
                .Append(" isRare=").Append(item.IsRare ? 1 : 0);
        }
        return builder.ToString();
    }

    private static void LogPayloadChunks(long traceId, string label, string payload)
    {
        string value = payload ?? string.Empty;
        string hash = ComputeHash(value);
        int chunkCount = Math.Max(1, (value.Length + LogChunkSize - 1) / LogChunkSize);
        Logger.Info(
            $"[F11][SettlementProbe][Raw] traceId={traceId}, label={label}, "
                + $"length={value.Length}, sha256={hash}, chunks={chunkCount}"
        );

        for (int i = 0; i < chunkCount; i++)
        {
            int offset = i * LogChunkSize;
            int length = Math.Min(LogChunkSize, value.Length - offset);
            string chunk = length > 0 ? value.Substring(offset, length) : string.Empty;
            Logger.Info(
                $"[F11][SettlementProbe][RawChunk] traceId={traceId}, label={label}, "
                    + $"part={i + 1}/{chunkCount}, data={chunk}"
            );
        }
    }

    private static long[] Copy(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<long> source)
    {
        if (source == null)
            return Array.Empty<long>();

        var result = new long[source.Length];
        for (int i = 0; i < source.Length; i++)
            result[i] = source[i];
        return result;
    }

    private static string FormatArray(IReadOnlyList<long> values)
    {
        if (values == null || values.Count == 0)
            return "[]";

        var builder = new StringBuilder("[");
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(values[i]);
        }
        return builder.Append(']').ToString();
    }

    private static string FormatChests(IReadOnlyList<ExplorationTreasureChest> chests)
    {
        if (chests == null || chests.Count == 0)
            return "none";

        var builder = new StringBuilder();
        for (int i = 0; i < chests.Count; i++)
        {
            if (i > 0)
                builder.Append('|');
            ExplorationTreasureChest chest = chests[i];
            builder.Append("floor=").Append(chest.FloorSid)
                .Append(" resource=").Append(chest.ResourceSid)
                .Append(" asset=").Append(chest.AssetId)
                .Append(" drops=").Append(FormatArray(chest.DropSids));
        }
        return builder.ToString();
    }

    private static string FormatRankRewards(
        IReadOnlyList<ExplorationTreasureRankReward> rewards
    )
    {
        if (rewards == null || rewards.Count == 0)
            return "none";

        var builder = new StringBuilder();
        for (int i = 0; i < rewards.Count; i++)
        {
            if (i > 0)
                builder.Append('|');
            ExplorationTreasureRankReward reward = rewards[i];
            builder.Append("floor=").Append(reward.FloorSid)
                .Append(" rank=").Append(reward.Rank)
                .Append(" asset=").Append(reward.AssetId)
                .Append(" timeLimit=").Append(reward.TimeLimit)
                .Append(" drops=").Append(FormatArray(reward.DropSids));
        }
        return builder.ToString();
    }

    private static string ComputeHash(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class AcceptedSnapshot
    {
        public long TraceId { get; }
        public string Mode { get; }
        public string Source { get; }
        public int Attempt { get; }
        public int Status { get; }
        public int QuestType { get; }
        public long QuestId { get; }
        public int StageType { get; }
        public string StartAt { get; }
        public IReadOnlyDictionary<long, BattleDropItem> RootBySid { get; }
        public IReadOnlyList<long> TargetSids { get; }
        public IReadOnlyList<ExplorationTreasureChest> TargetChests { get; }
        public IReadOnlyList<ExplorationTreasureRankReward> TargetRankRewards { get; }

        public AcceptedSnapshot(
            long traceId,
            string mode,
            string source,
            int attempt,
            int status,
            int questType,
            long questId,
            int stageType,
            string startAt,
            IReadOnlyDictionary<long, BattleDropItem> rootBySid,
            IReadOnlyList<long> targetSids,
            IReadOnlyList<ExplorationTreasureChest> targetChests,
            IReadOnlyList<ExplorationTreasureRankReward> targetRankRewards
        )
        {
            TraceId = traceId;
            Mode = mode;
            Source = source;
            Attempt = attempt;
            Status = status;
            QuestType = questType;
            QuestId = questId;
            StageType = stageType;
            StartAt = startAt;
            RootBySid = rootBySid;
            TargetSids = targetSids;
            TargetChests = targetChests;
            TargetRankRewards = targetRankRewards;
        }
    }
}
