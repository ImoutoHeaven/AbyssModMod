using System;
using System.Collections.Generic;
using System.Diagnostics;
using Absf;
using Cysharp.Threading.Tasks;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Tavern;

namespace AbyssMod.Services;

public static class TavernFirstCardAutoSL
{
    private static readonly List<Operation> Operations = new();

    [ThreadStatic]
    private static int _replayInvocationDepth;

    internal static bool IsReplayInvocation => _replayInvocationDepth != 0;

    public static UniTask<TavernExecWorkResponseEntity> Run(
        long dailyCardId,
        bool useTicket,
        UniTask<TavernExecWorkResponseEntity> initial,
        CancellationToken requestCancellationToken
    )
    {
        var operation = new Operation(
            dailyCardId,
            useTicket,
            initial,
            requestCancellationToken,
            Engine.RebootCancellationToken
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

    internal static bool TryGetEnabledTarget(
        out TavernFirstCardTarget target,
        out string error
    )
    {
        if (!Config.BattleSessionAutoSL.Value)
        {
            target = TavernFirstCardTarget.Off;
            error = "f11-disabled";
            return false;
        }
        if (!TavernFirstCardAutoSLPolicy.TryParseTarget(
                Config.TavernAutoSLFirstCardTarget.Value,
                out target
            ))
        {
            error = $"invalid-first-card-target:{Config.TavernAutoSLFirstCardTarget.Value}";
            return false;
        }
        if (target == TavernFirstCardTarget.Off)
        {
            error = "first-card-target-off";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static UniTask<TavernExecWorkResponseEntity> ReplayRequest(
        long dailyCardId,
        bool useTicket,
        CancellationToken cancellationToken
    )
    {
        _replayInvocationDepth++;
        try
        {
            return TavernApiService.RequestExecWorkAsync(
                dailyCardId,
                useTicket,
                cancellationToken
            );
        }
        finally
        {
            _replayInvocationDepth--;
        }
    }

    private sealed class Operation
    {
        private readonly long _dailyCardId;
        private readonly bool _useTicket;
        private readonly CancellationToken _requestCancellationToken;
        private readonly CancellationToken _retryCancellationToken;
        private readonly UniTaskCompletionSource<TavernExecWorkResponseEntity> _completion =
            new();
        private readonly TavernFirstCardRetryFlow _flow = new();

        private UniTask<TavernExecWorkResponseEntity> _current;
        private TavernExecWorkResponseEntity _lastResponse;
        private bool _currentIsReplay;
        private long _retryDueTimestamp;

        public Operation(
            long dailyCardId,
            bool useTicket,
            UniTask<TavernExecWorkResponseEntity> initial,
            CancellationToken requestCancellationToken,
            CancellationToken retryCancellationToken
        )
        {
            _dailyCardId = dailyCardId;
            _useTicket = useTicket;
            _current = initial;
            _requestCancellationToken = requestCancellationToken;
            _retryCancellationToken = retryCancellationToken;
        }

        public UniTask<TavernExecWorkResponseEntity> Task => _completion.Task;

        public bool Update()
        {
            try
            {
                if (_retryDueTimestamp != 0)
                    return PollCooldown();

                switch (_current.Status)
                {
                    case UniTaskStatus.Pending:
                        return false;
                    case UniTaskStatus.Canceled:
                        _completion.TrySetCanceled(_requestCancellationToken);
                        return true;
                    case UniTaskStatus.Faulted:
                        return HandleFaultedRequest();
                    case UniTaskStatus.Succeeded:
                        return HandleResponse(_current.GetAwaiter().GetResult());
                    default:
                        _completion.TrySetException(
                            new Il2CppSystem.Exception("Unknown Tavern UniTask status.")
                        );
                        return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[F11][TavernAutoSL] update fault: {ex}");
                if (_lastResponse != null)
                    _completion.TrySetResult(_lastResponse);
                else
                    _completion.TrySetException(new Il2CppSystem.Exception(ex.Message));
                return true;
            }
        }

        private bool PollCooldown()
        {
            if (Stopwatch.GetTimestamp() < _retryDueTimestamp)
                return false;

            _retryDueTimestamp = 0;
            bool enabled = TryGetEnabledTarget(out _, out string disabledReason);
            TavernFirstCardRetryAction action = _flow.OnCooldownElapsed(enabled);
            if (action == TavernFirstCardRetryAction.AcceptCurrentResponse)
            {
                Logger.Info(
                    $"[F11][TavernAutoSL] disabled before replay; accepting response, "
                        + $"reason={disabledReason}"
                );
                _completion.TrySetResult(_lastResponse);
                return true;
            }

            _current = ReplayRequest(_dailyCardId, _useTicket, _retryCancellationToken);
            _currentIsReplay = true;
            Logger.Info(
                $"[F11][TavernAutoSL] replay RequestExecWorkAsync invoked, "
                    + $"dailyCardId={_dailyCardId}, useTicket={_useTicket}, "
                    + $"taskStatus={_current.Status}"
            );
            return false;
        }

        private bool HandleFaultedRequest()
        {
            Exception fault;
            try
            {
                _current.GetAwaiter().GetResult();
                fault = new InvalidOperationException("Faulted Tavern task returned no exception.");
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            if (_currentIsReplay && _lastResponse != null)
            {
                _flow.OnReplayFault();
                Logger.Warn(
                    $"[F11][TavernAutoSL] replay faulted; accepting previous response: "
                        + $"{fault.GetType().Name}: {fault.Message}"
                );
                _completion.TrySetResult(_lastResponse);
                return true;
            }

            Logger.Error($"[F11][TavernAutoSL] initial request fault: {fault}");
            _completion.TrySetException(new Il2CppSystem.Exception(fault.Message));
            return true;
        }

        private bool HandleResponse(TavernExecWorkResponseEntity response)
        {
            _lastResponse = response;
            if (!TryGetEnabledTarget(out TavernFirstCardTarget target, out string disabledReason))
            {
                Logger.Info(
                    $"[F11][TavernAutoSL] accepting response without evaluation, "
                        + $"reason={disabledReason}"
                );
                _completion.TrySetResult(response);
                return true;
            }

            TavernFirstCardProbeReport report = TavernFirstCardProbe.Parse(response);
            if (report.Error.Length != 0)
            {
                Logger.Warn(
                    $"[F11][TavernAutoSL] probe failed; accepting response, error={report.Error}"
                );
                _completion.TrySetResult(response);
                return true;
            }
            if (!TavernFirstCardAutoSLPolicy.IsFirstCardTurn(report.SelectedCount))
            {
                Logger.Info(
                    $"[F11][TavernAutoSL] later turn bypassed, "
                        + $"selectedCount={report.SelectedCount}, workedCount={report.WorkedCount}"
                );
                _completion.TrySetResult(response);
                return true;
            }

            TavernFirstCardEvaluation evaluation = TavernFirstCardAutoSLPolicy.Evaluate(
                report.Candidates,
                target
            );
            TavernFirstCardRetryAction action = _flow.ObserveResponse(
                evaluation.ShouldRetry,
                report.WorkedCount,
                enabled: true
            );
            string decision = action switch
            {
                TavernFirstCardRetryAction.ScheduleRetry => "retry",
                _ when evaluation.Matches.Count != 0 => "accept-target",
                _ => "accept-worked-count-changed",
            };
            Logger.Info(
                $"[F11][TavernAutoSL] attempt={_flow.RetryCount + (action == TavernFirstCardRetryAction.ScheduleRetry ? 0 : 1)}, "
                    + $"retry={_flow.RetryCount}, dailyCardId={_dailyCardId}, "
                    + $"target={target.ToString().ToLowerInvariant()}, workedCount={report.WorkedCount}, "
                    + $"selectedCount={report.SelectedCount}, candidates={report.Candidates.Count}, "
                    + $"matches={evaluation.Matches.Count}, decision={decision}"
            );
            Logger.Info($"[F11][TavernAutoSL] cards={report.FormatCandidates()}");

            if (action == TavernFirstCardRetryAction.AcceptCurrentResponse)
            {
                _completion.TrySetResult(response);
                return true;
            }

            float cooldownSeconds = BattleSessionAutoSLPolicy.ClampCooldown(
                Config.BattleSessionAutoSLCooldown.Value
            );
            _retryDueTimestamp = Stopwatch.GetTimestamp()
                + (long)(cooldownSeconds * Stopwatch.Frequency);
            Logger.Info(
                $"[F11][TavernAutoSL] retry cooldown scheduled before exec/work replay, "
                    + $"cooldown={cooldownSeconds:0.0}s"
            );
            return false;
        }
    }
}
