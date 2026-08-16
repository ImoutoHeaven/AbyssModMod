using System;
using System.Collections.Generic;
using System.Diagnostics;
using Absf;
using Cysharp.Threading.Tasks;
using Il2CppSystem.Threading;
using Project.Api;
using Project.Tavern;
using Il2CppVignetteList = Il2CppSystem.Collections.Generic.List<Project.Tavern.Top.VignetteData>;
using TavernGameViewController = Project.Tavern.Top.GameViewController;

namespace AbyssMod.Services;

public static class TavernFirstCardAutoSL
{
    private static readonly List<CreateGameDataOperation> Operations = new();

    [ThreadStatic]
    private static int _nativeCreateGameDataInvocationDepth;

    internal static bool IsNativeCreateGameDataInvocation =>
        _nativeCreateGameDataInvocationDepth != 0;

    public static UniTask RunCreateGameData(
        TavernGameViewController controller,
        Il2CppVignetteList vignetteIds,
        TavernExecWorkResponseEntity initialResponse,
        long dailyId
    )
    {
        var operation = new CreateGameDataOperation(
            controller,
            vignetteIds,
            initialResponse,
            dailyId,
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
    ) => TavernApiService.RequestExecWorkAsync(dailyCardId, useTicket, cancellationToken);

    private static UniTask InvokeNativeCreateGameData(
        TavernGameViewController controller,
        Il2CppVignetteList vignetteIds,
        TavernExecWorkResponseEntity response,
        long dailyId
    )
    {
        _nativeCreateGameDataInvocationDepth++;
        try
        {
            return controller.CreateGameData(vignetteIds, response, dailyId);
        }
        finally
        {
            _nativeCreateGameDataInvocationDepth--;
        }
    }

    private sealed class CreateGameDataOperation
    {
        private readonly TavernGameViewController _controller;
        private readonly Il2CppVignetteList _vignetteIds;
        private readonly long _dailyCardId;
        private readonly bool _useTicket;
        private readonly CancellationToken _cancellationToken;
        private readonly UniTaskCompletionSource _completion = new();
        private readonly TavernFirstCardRetryFlow _flow = new();

        private TavernExecWorkResponseEntity _pendingResponse;
        private TavernExecWorkResponseEntity _lastResponse;
        private UniTask<TavernExecWorkResponseEntity> _replayTask;
        private UniTask _createGameDataTask;
        private bool _replayActive;
        private bool _createGameDataActive;
        private long _retryDueTimestamp;

        public CreateGameDataOperation(
            TavernGameViewController controller,
            Il2CppVignetteList vignetteIds,
            TavernExecWorkResponseEntity initialResponse,
            long dailyId,
            CancellationToken cancellationToken
        )
        {
            _controller = controller;
            _vignetteIds = vignetteIds;
            _pendingResponse = initialResponse;
            _dailyCardId = dailyId;
            _useTicket = initialResponse.tavern_daily_card.use_ticket != 0;
            _cancellationToken = cancellationToken;
        }

        public UniTask Task => _completion.Task;

        public bool Update()
        {
            try
            {
                if (_createGameDataActive)
                    return PollCreateGameData();
                if (_retryDueTimestamp != 0)
                    return PollCooldown();
                if (_replayActive)
                    return PollReplay();
                if (_pendingResponse != null)
                {
                    TavernExecWorkResponseEntity response = _pendingResponse;
                    _pendingResponse = null;
                    return HandleResponse(response);
                }

                _completion.TrySetException(
                    new Il2CppSystem.Exception("Tavern Auto-SL entered an idle state.")
                );
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[F11][TavernAutoSL] update fault: {ex}");
                if (_lastResponse != null && !_createGameDataActive)
                    return StartNativeCreateGameData(_lastResponse, "update-fault-fail-open");

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
                return StartNativeCreateGameData(_lastResponse, "disabled-before-replay");
            }

            _replayTask = ReplayRequest(
                _dailyCardId,
                _useTicket,
                _cancellationToken
            );
            _replayActive = true;
            Logger.Info(
                $"[F11][TavernAutoSL] replay RequestExecWorkAsync invoked, "
                    + $"dailyCardId={_dailyCardId}, useTicket={_useTicket}, "
                    + $"taskStatus={_replayTask.Status}"
            );
            return false;
        }

        private bool PollReplay()
        {
            switch (_replayTask.Status)
            {
                case UniTaskStatus.Pending:
                    return false;
                case UniTaskStatus.Canceled:
                    _replayActive = false;
                    _flow.OnReplayFault();
                    Logger.Warn(
                        "[F11][TavernAutoSL] replay canceled; accepting previous response"
                    );
                    return StartNativeCreateGameData(_lastResponse, "replay-canceled-fail-open");
                case UniTaskStatus.Faulted:
                    return HandleReplayFault();
                case UniTaskStatus.Succeeded:
                    _replayActive = false;
                    return HandleResponse(_replayTask.GetAwaiter().GetResult());
                default:
                    _replayActive = false;
                    Logger.Warn(
                        $"[F11][TavernAutoSL] unknown replay status "
                            + $"{_replayTask.Status}; accepting previous response"
                    );
                    return StartNativeCreateGameData(_lastResponse, "unknown-replay-status");
            }
        }

        private bool HandleReplayFault()
        {
            _replayActive = false;
            Exception fault;
            try
            {
                _replayTask.GetAwaiter().GetResult();
                fault = new InvalidOperationException(
                    "Faulted Tavern replay task returned no exception."
                );
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            _flow.OnReplayFault();
            Logger.Warn(
                $"[F11][TavernAutoSL] replay faulted; accepting previous response: "
                    + $"{fault.GetType().Name}: {fault.Message}"
            );
            return StartNativeCreateGameData(_lastResponse, "replay-fault-fail-open");
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
                return StartNativeCreateGameData(response, "disabled-before-evaluation");
            }

            TavernFirstCardProbeReport report = TavernFirstCardProbe.Parse(response);
            if (report.Error.Length != 0)
            {
                Logger.Warn(
                    $"[F11][TavernAutoSL] probe failed; accepting response, error={report.Error}"
                );
                return StartNativeCreateGameData(response, "probe-fail-open");
            }
            if (!TavernFirstCardAutoSLPolicy.IsFirstCardTurn(report.SelectedCount))
            {
                Logger.Info(
                    $"[F11][TavernAutoSL] later turn bypassed, "
                        + $"selectedCount={report.SelectedCount}, workedCount={report.WorkedCount}"
                );
                return StartNativeCreateGameData(response, "later-turn-bypass");
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
                return StartNativeCreateGameData(response, decision);

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

        private bool StartNativeCreateGameData(
            TavernExecWorkResponseEntity response,
            string reason
        )
        {
            if (response == null)
            {
                _completion.TrySetException(
                    new Il2CppSystem.Exception(
                        $"Tavern Auto-SL cannot fail open without a response: {reason}"
                    )
                );
                return true;
            }

            try
            {
                _createGameDataTask = InvokeNativeCreateGameData(
                    _controller,
                    _vignetteIds,
                    response,
                    _dailyCardId
                );
                _createGameDataActive = true;
                Logger.Info(
                    $"[F11][TavernAutoSL] native CreateGameData invoked, "
                        + $"reason={reason}, taskStatus={_createGameDataTask.Status}"
                );
                return PollCreateGameData();
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[F11][TavernAutoSL] native CreateGameData invocation fault: {ex}"
                );
                _completion.TrySetException(new Il2CppSystem.Exception(ex.Message));
                return true;
            }
        }

        private bool PollCreateGameData()
        {
            switch (_createGameDataTask.Status)
            {
                case UniTaskStatus.Pending:
                    return false;
                case UniTaskStatus.Canceled:
                    _createGameDataActive = false;
                    _completion.TrySetCanceled(_cancellationToken);
                    return true;
                case UniTaskStatus.Faulted:
                    return HandleCreateGameDataFault();
                case UniTaskStatus.Succeeded:
                    _createGameDataTask.GetAwaiter().GetResult();
                    _createGameDataActive = false;
                    _completion.TrySetResult();
                    return true;
                default:
                    _createGameDataActive = false;
                    _completion.TrySetException(
                        new Il2CppSystem.Exception("Unknown Tavern CreateGameData UniTask status.")
                    );
                    return true;
            }
        }

        private bool HandleCreateGameDataFault()
        {
            _createGameDataActive = false;
            Exception fault;
            try
            {
                _createGameDataTask.GetAwaiter().GetResult();
                fault = new InvalidOperationException(
                    "Faulted Tavern CreateGameData task returned no exception."
                );
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            Logger.Error($"[F11][TavernAutoSL] native CreateGameData fault: {fault}");
            _completion.TrySetException(new Il2CppSystem.Exception(fault.Message));
            return true;
        }
    }
}
