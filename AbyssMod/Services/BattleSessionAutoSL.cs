using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Absf;
using Cysharp.Threading.Tasks;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Ingame.Disaster;
using Project.Ingame.Exploration;

namespace AbyssMod.Services;

public static class BattleSessionAutoSL
{
    private static readonly List<Operation> Operations = new();

    /// <summary>
    /// F12 reads this only to wait for an existing F11 Nether operation.  The list itself
    /// remains private so the two hotkeys cannot mutate one another's operation lifetime.
    /// </summary>
    public static bool HasActiveNetherOperation => Operations.Any(operation => operation.IsNetherOperation);

    public static UniTask<BattleSessionStatusResponseEntity> RunExploration(
        IExplorationQuestAPIService apiService,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct,
        string source
    )
    {
        var operation = new ExplorationOperation(
            apiService,
            source,
            ct,
            Engine.RebootCancellationToken,
            initial
        );
        Operations.Add(operation);
        return operation.Task;
    }

    public static UniTask<BattleSessionStatusResponseEntity> RunDisaster(
        IDisasterQuestAPIService apiService,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct,
        string source
    )
    {
        var operation = new DisasterOperation(
            apiService,
            source,
            ct,
            Engine.RebootCancellationToken,
            initial
        );
        Operations.Add(operation);
        return operation.Task;
    }

    public static UniTask<BattleSessionStatusResponseEntity> RunNether(
        IExplorationQuestAPIService apiService,
        NetherAPIService netherApiService,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct,
        string source
    )
    {
        var operation = new NetherOperation(
            apiService,
            netherApiService,
            source,
            ct,
            Engine.RebootCancellationToken,
            initial
        );
        Operations.Add(operation);
        return operation.Task;
    }

    public static void Update()
    {
        for (int i = Operations.Count - 1; i >= 0; i--)
        {
            if (Operations[i].Update())
                Operations.RemoveAt(i);
        }
    }

    private abstract class Operation
    {
        protected readonly UniTaskCompletionSource<BattleSessionStatusResponseEntity> Completion =
            new();
        protected readonly BattleSessionAutoSLStateMachine State;
        protected readonly CancellationToken RequestCancellationToken;
        protected readonly CancellationToken RetryCancellationToken;
        protected UniTask<BattleSessionStatusResponseEntity> Current;
        private BattleSessionStatusResponseEntity _lastResponse;
        private long _retryDueTimestamp;

        protected virtual string LogPrefix => "[F11][BattleAutoSL]";

        public virtual bool IsNetherOperation => false;

        protected Operation(
            UniTask<BattleSessionStatusResponseEntity> initial,
            CancellationToken cancellationToken,
            CancellationToken retryCancellationToken
        )
        {
            Current = initial;
            RequestCancellationToken = cancellationToken;
            RetryCancellationToken = retryCancellationToken;
            State = new BattleSessionAutoSLStateMachine();
        }

        public UniTask<BattleSessionStatusResponseEntity> Task => Completion.Task;

        public bool Update()
        {
            try
            {
                if (_retryDueTimestamp != 0)
                {
                    if (!Config.BattleSessionAutoSL.Value)
                    {
                        _retryDueTimestamp = 0;
                        State.ObserveDecision(false);
                        Completion.TrySetResult(_lastResponse);
                        Logger.Info(
                            $"{LogPrefix} disabled during cooldown; accepting previous response"
                        );
                        return true;
                    }

                    if (Stopwatch.GetTimestamp() < _retryDueTimestamp)
                        return false;

                    _retryDueTimestamp = 0;
                    StartRetry();
                    return false;
                }

                switch (Current.Status)
                {
                    case UniTaskStatus.Pending:
                        State.ObservePending();
                        return false;
                    case UniTaskStatus.Canceled:
                        State.ObserveCanceled();
                        Completion.TrySetCanceled(RequestCancellationToken);
                        return true;
                    case UniTaskStatus.Faulted:
                        State.ObserveFaulted();
                        try
                        {
                            Current.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"{LogPrefix} source fault: {ex}");
                            Completion.TrySetException(new Il2CppSystem.Exception(ex.Message));
                        }
                        return true;
                    case UniTaskStatus.Succeeded:
                        _lastResponse = Current.GetAwaiter().GetResult();
                        return HandleResponse(_lastResponse);
                    default:
                        State.ObserveFaulted();
                        Completion.TrySetException(
                            new Il2CppSystem.Exception("Unknown UniTask status.")
                        );
                        return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"{LogPrefix} update fault: {ex}");
                State.ObserveFaulted();
                Completion.TrySetException(new Il2CppSystem.Exception(ex.Message));
                return true;
            }
        }

        protected abstract bool HandleResponse(BattleSessionStatusResponseEntity response);

        protected abstract void StartRetry();

        protected void ScheduleRetry(string mode)
        {
            float cooldownSeconds = BattleSessionAutoSLPolicy.ClampCooldown(
                Config.BattleSessionAutoSLCooldown.Value
            );
            _retryDueTimestamp = Stopwatch.GetTimestamp()
                + (long)(cooldownSeconds * Stopwatch.Frequency);
            Logger.Info(
                $"{LogPrefix} {mode} retry scheduled, "
                    + $"requestCanceled={RequestCancellationToken.IsCancellationRequested}, "
                    + $"retryCanceled={RetryCancellationToken.IsCancellationRequested}, "
                    + $"cooldown={cooldownSeconds:0.0}s"
            );
        }
    }

    private sealed class ExplorationOperation : Operation
    {
        private readonly IExplorationQuestAPIService _apiService;
        private readonly string _source;

        public ExplorationOperation(
            IExplorationQuestAPIService apiService,
            string source,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _apiService = apiService;
            _source = source;
        }

        protected override void StartRetry()
        {
            Current = _apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info(
                $"[F11][BattleAutoSL] exploration retry task status={Current.Status}, "
                    + $"source={_source}"
            );
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Logger.Info(
                    $"[F11][BattleAutoSL] disabled; accepting current exploration response, "
                        + $"source={_source}"
                );
                Completion.TrySetResult(response);
                return true;
            }

            BattleDropProbeReport report = BattleSessionDropProbe.Parse(response?.stage_detail);
            BattleSessionAutoSLStopMode stopMode =
                Config.BattleSessionAutoSLNormalStopMode.Value;
            BattleSessionDropRarity minimumRarity =
                Config.BattleSessionAutoSLNormalMinimumRarity.Value;
            BattleSessionNormalContentTypeFilter contentTypes =
                Config.BattleSessionAutoSLNormalContentTypes.Value;
            BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
                report,
                stopMode,
                minimumRarity,
                contentTypes
            );
            LogAttempt(
                "exploration",
                _source,
                State.RetryCount,
                response,
                report,
                evaluation,
                stopMode,
                minimumRarity,
                contentTypes
            );
            if (State.ObserveDecision(evaluation.ShouldRetry) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("exploration");
                return false;
            }

            BattleSettlementPayloadTrace.CaptureAccepted(
                "exploration",
                _source,
                State.RetryCount + 1,
                response,
                report.Items,
                evaluation.Targets
            );
            Completion.TrySetResult(response);
            return true;
        }
    }

    private sealed class DisasterOperation : Operation
    {
        private readonly IDisasterQuestAPIService _apiService;
        private readonly string _source;

        public DisasterOperation(
            IDisasterQuestAPIService apiService,
            string source,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _apiService = apiService;
            _source = source;
        }

        protected override void StartRetry()
        {
            Current = _apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info(
                $"[F11][BattleAutoSL] disaster retry task status={Current.Status}, "
                    + $"source={_source}"
            );
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Logger.Info(
                    $"[F11][BattleAutoSL] disabled; accepting current disaster response, "
                        + $"source={_source}"
                );
                Completion.TrySetResult(response);
                return true;
            }

            BattleDropProbeReport report = BattleSessionDropProbe.Parse(response?.stage_detail);
            BattleSessionAutoSLStopMode stopMode =
                Config.BattleSessionAutoSLNormalStopMode.Value;
            BattleSessionDropRarity minimumRarity =
                Config.BattleSessionAutoSLNormalMinimumRarity.Value;
            BattleSessionNormalContentTypeFilter contentTypes =
                Config.BattleSessionAutoSLNormalContentTypes.Value;
            BattleSessionDropEvaluation evaluation = BattleSessionAutoSLPolicy.Evaluate(
                report,
                stopMode,
                minimumRarity,
                contentTypes
            );
            LogAttempt(
                "disaster",
                _source,
                State.RetryCount,
                response,
                report,
                evaluation,
                stopMode,
                minimumRarity,
                contentTypes
            );
            if (State.ObserveDecision(evaluation.ShouldRetry) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("disaster");
                return false;
            }

            BattleSettlementPayloadTrace.CaptureAccepted(
                "disaster",
                _source,
                State.RetryCount + 1,
                response,
                report.Items,
                evaluation.Targets
            );
            Completion.TrySetResult(response);
            return true;
        }
    }

    private sealed class NetherOperation : Operation
    {
        private readonly IExplorationQuestAPIService _apiService;
        private readonly NetherAPIService _netherApiService;
        private readonly string _source;

        protected override string LogPrefix => "[F11][NetherAutoSL]";

        public override bool IsNetherOperation => true;

        public NetherOperation(
            IExplorationQuestAPIService apiService,
            NetherAPIService netherApiService,
            string source,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _apiService = apiService;
            _netherApiService = netherApiService;
            _source = source;
        }

        protected override void StartRetry()
        {
            Current = _apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info(
                $"{LogPrefix} retry task status={Current.Status}, source={_source}, "
                    + FormatLocation()
            );
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Logger.Info($"{LogPrefix} disabled; accepting current response");
                Completion.TrySetResult(response);
                return true;
            }

            var param = _netherApiService?._param;
            if (param == null)
            {
                return AcceptWithoutNetherPolicy(response, CreateLocationBypass("missing-nether-param"));
            }
            if (param.FloorLevel < 1)
            {
                return AcceptWithoutNetherPolicy(response, CreateLocationBypass($"invalid-floor-level:{param.FloorLevel}"));
            }
            if (param.MNetherMapFloorId <= 0)
            {
                return AcceptWithoutNetherPolicy(response, CreateLocationBypass($"invalid-nether-map-floor-id:{param.MNetherMapFloorId}"));
            }

            if (!NetherFloorEncounterCatalog.TryGetRawFloorType(
                param.MNetherMapFloorId, out int rawFloorType, out string classificationError
            ))
            {
                return AcceptWithoutNetherPolicy(response, CreateLocationBypass(classificationError));
            }

            NetherRuntimeDecision runtimeDecision = NetherRuntimeDecisionEngine.Resolve(
                rawFloorType,
                param.FloorLevel,
                new NetherStrategySettings(
                    Config.BattleSessionAutoSLNetherBattleStrategy.Value,
                    Config.BattleSessionAutoSLNetherMiniBossStrategy.Value,
                    Config.BattleSessionAutoSLNetherBossStrategy.Value
                )
            );
            if (!runtimeDecision.RequiresDropEvaluation)
                return AcceptWithoutNetherPolicy(response, runtimeDecision);

            NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(response?.stage_detail);
            bool equipmentOnly = Config.BattleSessionAutoSLNetherEquipmentOnly.Value;
            NetherPreserveMode preserveMode = Config.BattleSessionAutoSLNetherPreserveMode.Value;
            bool preserveConfigValid = NetherPreserveItemIdParser.TryParse(
                Config.BattleSessionAutoSLNetherPreserveItemIds.Value,
                out HashSet<long> preservedItemIds,
                out string preserveConfigError
            );

            IReadOnlyDictionary<long, NetherItemMasterInfo> masterItems = null;
            string masterError = string.Empty;
            bool requiresMasterItems = equipmentOnly || preservedItemIds.Count > 0;
            bool hasMasterItems = !requiresMasterItems
                || NetherItemMasterCatalog.TryGet(out masterItems, out masterError);
            NetherBattleDropEvaluation evaluation;
            if (!preserveConfigValid)
            {
                evaluation = NetherBattleAutoSLPolicy.Error(preserveConfigError);
            }
            else if (!hasMasterItems)
            {
                evaluation = NetherBattleAutoSLPolicy.Error(masterError);
            }
            else
            {
                evaluation = NetherBattleAutoSLPolicy.Evaluate(
                    report,
                    masterItems,
                    runtimeDecision.Target,
                    equipmentOnly,
                    preserveMode,
                    preservedItemIds
                );
            }

            LogNetherAttempt(
                response,
                report,
                evaluation,
                evaluation.ShouldRetry ? "retry" : evaluation.Targets.Count > 0 ? "accept-target" : "accept-error",
                runtimeDecision.Reason,
                runtimeDecision.EncounterKind,
                runtimeDecision.RawFloorType,
                runtimeDecision.ConfigKey,
                runtimeDecision.StrategyText,
                runtimeDecision.StrategyDecision,
                equipmentOnly,
                preserveMode,
                preservedItemIds,
                Config.BattleSessionAutoSLNetherPreserveItemIds.Value,
                false
            );
            if (State.ObserveDecision(evaluation.ShouldRetry) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("nether");
                return false;
            }

            BattleSettlementPayloadTrace.CaptureAcceptedNether(
                _source,
                State.RetryCount + 1,
                response,
                report.AllItems,
                evaluation.Targets
            );
            Completion.TrySetResult(response);
            return true;
        }

        private void LogNetherAttempt(
            BattleSessionStatusResponseEntity response,
            NetherBattleDropProbeReport report,
            NetherBattleDropEvaluation evaluation,
            string decision,
            string strategyError,
            NetherEncounterKind encounterKind,
            int rawFloorType,
            string configKey,
            string strategyText,
            NetherFloorStrategyDecision strategyDecision,
            bool equipmentOnly,
            NetherPreserveMode preserveMode,
            HashSet<long> preservedItemIds,
            string rawPreserveItemIds,
            bool warning
        )
        {
            string message =
                $"{LogPrefix} attempt={State.RetryCount + 1}, retry={State.RetryCount}, "
                    + $"source={_source}, questId={response?.quest_id ?? 0}, "
                    + $"stageType={response?.stage_type ?? 0}, drops={report.DropCount}, "
                    + $"enemyDrops={report.EnemyDropCount}, targets={evaluation.Targets.Count}, "
                    + $"equipmentTargets={evaluation.EquipmentTargetCount}, "
                    + $"preserveTargets={evaluation.PreservedItemTargetCount}, "
                    + $"combinedMatched={evaluation.StopConditionMatched}, decision={decision}, "
                    + $"floorType={rawFloorType}, encounter={encounterKind}, configKey={configKey}, "
                    + $"strategy={strategyText}, selector={strategyDecision.Selector}, clause={strategyDecision.ClauseIndex}, "
                    + $"target={strategyDecision.Target}, strategyMatched={strategyDecision.Matched}, strategyError={strategyError}, "
                    + $"equipmentOnly={equipmentOnly}, "
                    + $"preserveMode={preserveMode}, rawPreserveItemIds={rawPreserveItemIds}, "
                    + $"parsedPreserveItemIds={NetherPreserveItemIdParser.Format(preservedItemIds)}, "
                    + $"probeError={report.Error}, "
                    + $"policyError={evaluation.Error}, {FormatLocation()}";
            if (warning)
                Logger.Warn(message);
            else
                Logger.Info(message);
            Logger.Info($"{LogPrefix} allItems={report.FormatAllItems()}");
            Logger.Info($"{LogPrefix} enemyItems={report.FormatEnemyItems()}");
            Logger.Info($"{LogPrefix} targets={evaluation.FormatTargets()}");
        }

        private bool AcceptWithoutNetherPolicy(BattleSessionStatusResponseEntity response, NetherRuntimeDecision runtimeDecision)
        {
            NetherBypassTraceInput trace = NetherBypassTraceInput.FromStageDetail(response?.stage_detail);
            var report = new NetherBattleDropProbeReport(
                trace.RootDrops, Array.Empty<BattleDropItem>(), trace.Error
            );
            var evaluation = new NetherBattleDropEvaluation(Array.Empty<NetherTargetDrop>(), false);
            string rawPreserveItemIds = Config.BattleSessionAutoSLNetherPreserveItemIds.Value;
            NetherPreserveMode preserveMode = Config.BattleSessionAutoSLNetherPreserveMode.Value;
            NetherAttemptLogContext logContext = new(rawPreserveItemIds, preserveMode, runtimeDecision);
            LogNetherAttempt(
                response, report, evaluation,
                runtimeDecision.Kind == NetherRuntimeDecisionKind.AcceptError ? "accept-error" : "accept-off",
                runtimeDecision.Reason, runtimeDecision.EncounterKind, runtimeDecision.RawFloorType,
                runtimeDecision.ConfigKey, runtimeDecision.StrategyText, runtimeDecision.StrategyDecision,
                Config.BattleSessionAutoSLNetherEquipmentOnly.Value,
                preserveMode,
                new HashSet<long>(),
                logContext.RawPreserveItemIds,
                logContext.Level == NetherAttemptLogLevel.Warning
            );
            BattleSettlementPayloadTrace.CaptureAcceptedNether(
                _source, State.RetryCount + 1, response, report.AllItems, evaluation.Targets
            );
            Completion.TrySetResult(response);
            return true;
        }

        private static NetherRuntimeDecision CreateLocationBypass(string reason) =>
            NetherRuntimeDecisionEngine.CreateBypass(reason);

        private string FormatLocation()
        {
            var param = _netherApiService?._param;
            if (param == null)
                return "netherLocation=missing";

            return $"netherId={param.MNetherId}, mapId={param.MNetherMapId}, "
                + $"mNetherMapFloorId={param.MNetherMapFloorId}, floorLevel={param.FloorLevel}, floorIndex={param.FloorIndex}";
        }
    }

    private static void LogAttempt(
        string mode,
        string source,
        int retryCount,
        BattleSessionStatusResponseEntity response,
        BattleDropProbeReport report,
        BattleSessionDropEvaluation evaluation,
        BattleSessionAutoSLStopMode stopMode,
        BattleSessionDropRarity minimumRarity,
        BattleSessionNormalContentTypeFilter contentTypes
    )
    {
        string decision = evaluation.ShouldRetry
            ? "retry"
            : evaluation.Targets.Count > 0
                ? "accept-target"
                : "accept-error";
        string condition = BattleSessionAutoSLPolicy.DescribeNormalStopCondition(
            stopMode,
            minimumRarity,
            contentTypes
        );

        Logger.Info(
            $"[F11][BattleAutoSL] mode={mode}, source={source}, attempt={retryCount + 1}, "
                + $"retry={retryCount}, questId={response?.quest_id ?? 0}, "
                + $"drops={report.DropCount}, rare={report.RareDropCount}, "
                + $"targets={evaluation.Targets.Count}, decision={decision}, "
                + $"condition={condition}, error={evaluation.Error}"
        );
        Logger.Info($"[F11][BattleAutoSL] items={report.FormatItemList()}");
    }
}
