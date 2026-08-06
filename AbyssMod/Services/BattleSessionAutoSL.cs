using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    public static UniTask<BattleSessionStatusResponseEntity> RunExploration(
        ResumedQuestAPIService resumed,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct
    )
    {
        var operation = new ExplorationOperation(
            resumed,
            ct,
            Engine.RebootCancellationToken,
            initial
        );
        Operations.Add(operation);
        return operation.Task;
    }

    public static UniTask<BattleSessionStatusResponseEntity> RunDisaster(
        ResumedDisasterQuestAPIService resumed,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct
    )
    {
        var operation = new DisasterOperation(
            resumed,
            ct,
            Engine.RebootCancellationToken,
            initial
        );
        Operations.Add(operation);
        return operation.Task;
    }

    public static UniTask<BattleSessionStatusResponseEntity> RunNether(
        ExplorationQuestPreserveAPIService preserved,
        NetherAPIService netherApiService,
        UniTask<BattleSessionStatusResponseEntity> initial,
        CancellationToken ct
    )
    {
        var operation = new NetherOperation(
            preserved,
            netherApiService,
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
        private long _retryDueTimestamp;

        protected virtual string LogPrefix => "[F11][BattleAutoSL]";

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
                        return HandleResponse(Current.GetAwaiter().GetResult());
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
        private readonly ResumedQuestAPIService _resumed;

        public ExplorationOperation(
            ResumedQuestAPIService resumed,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _resumed = resumed;
        }

        protected override void StartRetry()
        {
            Current = _resumed._apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info($"[F11][BattleAutoSL] exploration retry task status={Current.Status}");
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Completion.TrySetResult(response);
                return true;
            }

            BattleDropProbeReport report = BattleSessionDropProbe.Parse(response?.stage_detail);
            LogAttempt("exploration", State.RetryCount, response, report);
            if (State.ObserveResponse(report) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("exploration");
                return false;
            }

            Completion.TrySetResult(response);
            return true;
        }
    }

    private sealed class DisasterOperation : Operation
    {
        private readonly ResumedDisasterQuestAPIService _resumed;

        public DisasterOperation(
            ResumedDisasterQuestAPIService resumed,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _resumed = resumed;
        }

        protected override void StartRetry()
        {
            Current = _resumed._apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info($"[F11][BattleAutoSL] disaster retry task status={Current.Status}");
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Completion.TrySetResult(response);
                return true;
            }

            BattleDropProbeReport report = BattleSessionDropProbe.Parse(response?.stage_detail);
            LogAttempt("disaster", State.RetryCount, response, report);
            if (State.ObserveResponse(report) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("disaster");
                return false;
            }

            Completion.TrySetResult(response);
            return true;
        }
    }

    private sealed class NetherOperation : Operation
    {
        private readonly ExplorationQuestPreserveAPIService _preserved;
        private readonly NetherAPIService _netherApiService;

        protected override string LogPrefix => "[F11][NetherAutoSL]";

        public NetherOperation(
            ExplorationQuestPreserveAPIService preserved,
            NetherAPIService netherApiService,
            CancellationToken ct,
            CancellationToken retryCancellationToken,
            UniTask<BattleSessionStatusResponseEntity> initial
        )
            : base(initial, ct, retryCancellationToken)
        {
            _preserved = preserved;
            _netherApiService = netherApiService;
        }

        protected override void StartRetry()
        {
            Current = _preserved._apiService.StartQuestAsync(RetryCancellationToken);
            Logger.Info($"{LogPrefix} retry task status={Current.Status}, {FormatLocation()}");
        }

        protected override bool HandleResponse(BattleSessionStatusResponseEntity response)
        {
            if (!Config.BattleSessionAutoSL.Value)
            {
                Logger.Info($"{LogPrefix} disabled; accepting current response");
                Completion.TrySetResult(response);
                return true;
            }

            NetherBattleDropProbeReport report = NetherBattleDropProbe.Parse(
                response?.stage_detail
            );
            NetherBattleDropEvaluation evaluation;
            if (
                !NetherItemMasterCatalog.TryGet(
                    out IReadOnlyDictionary<long, NetherItemMasterInfo> masterItems,
                    out string masterError
                )
            )
            {
                evaluation = NetherBattleAutoSLPolicy.Error(masterError);
            }
            else
            {
                evaluation = NetherBattleAutoSLPolicy.Evaluate(report, masterItems);
            }

            LogNetherAttempt(response, report, evaluation);
            if (State.ObserveDecision(evaluation.ShouldRetry) == BattleSessionAutoSLTransition.Retry)
            {
                ScheduleRetry("nether");
                return false;
            }

            Completion.TrySetResult(response);
            return true;
        }

        private void LogNetherAttempt(
            BattleSessionStatusResponseEntity response,
            NetherBattleDropProbeReport report,
            NetherBattleDropEvaluation evaluation
        )
        {
            string decision = evaluation.ShouldRetry
                ? "retry"
                : evaluation.Targets.Count > 0
                    ? "accept-target"
                    : "accept-error";

            Logger.Info(
                $"{LogPrefix} attempt={State.RetryCount + 1}, retry={State.RetryCount}, "
                    + $"questId={response?.quest_id ?? 0}, stageType={response?.stage_type ?? 0}, "
                    + $"drops={report.DropCount}, enemyDrops={report.EnemyDropCount}, "
                    + $"targets={evaluation.Targets.Count}, decision={decision}, "
                    + $"probeError={report.Error}, policyError={evaluation.Error}, {FormatLocation()}"
            );
            Logger.Info($"{LogPrefix} allItems={report.FormatAllItems()}");
            Logger.Info($"{LogPrefix} enemyItems={report.FormatEnemyItems()}");
            Logger.Info($"{LogPrefix} targets={evaluation.FormatTargets()}");
        }

        private string FormatLocation()
        {
            var param = _netherApiService?._param;
            if (param == null)
                return "netherLocation=missing";

            return $"netherId={param.MNetherId}, mapId={param.MNetherMapId}, "
                + $"floor={param.FloorLevel}:{param.FloorIndex}";
        }
    }

    private static void LogAttempt(
        string mode,
        int retryCount,
        BattleSessionStatusResponseEntity response,
        BattleDropProbeReport report
    )
    {
        Logger.Info(
            $"[F11][BattleAutoSL] mode={mode}, attempt={retryCount + 1}, "
                + $"retry={retryCount}, questId={response?.quest_id ?? 0}, "
                + $"drops={report.DropCount}, rare={report.RareDropCount}, error={report.Error}"
        );
        Logger.Info($"[F11][BattleAutoSL] items={report.FormatItemList()}");
    }
}
