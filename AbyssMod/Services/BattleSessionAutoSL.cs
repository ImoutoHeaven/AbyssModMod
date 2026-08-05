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
                            Logger.Error($"[F11][BattleAutoSL] source fault: {ex}");
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
                Logger.Error($"[F11][BattleAutoSL] update fault: {ex}");
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
                $"[F11][BattleAutoSL] {mode} retry scheduled, "
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
