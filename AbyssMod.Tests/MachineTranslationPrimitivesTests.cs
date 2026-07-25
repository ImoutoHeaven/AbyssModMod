using AbyssMod.Services;
using Xunit;

namespace AbyssMod.Tests;

public class MachineTranslationPrimitivesTests
{
    [Fact]
    public void Dequeue_prefers_event_text_over_background_text()
    {
        var queue = new TranslationQueue();
        queue.Enqueue("background", "ui_misc", foreground: false);
        queue.Enqueue("event", "system", foreground: true);

        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal("event", job.Template);
    }

    [Fact]
    public void Event_enqueue_promotes_an_already_queued_background_template()
    {
        var queue = new TranslationQueue();
        queue.Enqueue("other", "system", foreground: false);
        queue.Enqueue("existing", "ui_misc", foreground: false);
        queue.Enqueue("existing", "ui_misc", foreground: true);

        Assert.True(queue.TryDequeue(out var job));
        Assert.Equal("existing", job.Template);
    }

    [Fact]
    public void Three_fast_retries_switch_a_template_to_periodic_only()
    {
        var queue = new TranslationQueue();
        queue.Enqueue("template", "ui_misc", foreground: true);

        for (var retry = 0; retry < 3; retry++)
        {
            Assert.True(queue.TryDequeue(out var job));
            Assert.Equal(TranslationFailureDisposition.FastRetry, queue.CompleteFailure(job, 3));
        }

        Assert.True(queue.TryDequeue(out var lastFastRetry));
        Assert.Equal(TranslationFailureDisposition.PeriodicOnly, queue.CompleteFailure(lastFastRetry, 3));
        Assert.True(queue.IsPeriodicOnly("template"));
        Assert.False(queue.TryDequeue(out _));

        queue.EnqueuePeriodicPending();

        Assert.True(queue.TryDequeue(out var periodicJob));
        Assert.Equal("template", periodicJob.Template);
    }

    [Theory]
    [InlineData("カタカナ")]
    [InlineData("ひらがな")]
    [InlineData("伤害提升")]
    [InlineData("123 <color=#FFFFFF>HP</color>")]
    public void Candidate_detection_requires_kana(string text)
    {
        var expected = text is "カタカナ" or "ひらがな";

        Assert.Equal(expected, MachineTranslationTextProtection.HasKana(text));
    }

    [Fact]
    public void Request_starts_are_spaced_by_the_configured_rate()
    {
        var limiter = new RequestStartRateLimiter();
        var now = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(TimeSpan.Zero, limiter.ReserveDelay(now, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), limiter.ReserveDelay(now, 2));
        Assert.Equal(TimeSpan.FromMilliseconds(500), limiter.ReserveDelay(now.AddMilliseconds(500), 2));
    }

    [Fact]
    public void In_flight_gate_rejects_requests_at_the_configured_limit()
    {
        var gate = new RequestInFlightGate();

        Assert.True(gate.TryAcquire(2));
        Assert.True(gate.TryAcquire(2));
        Assert.False(gate.TryAcquire(2));

        gate.Release();

        Assert.True(gate.TryAcquire(2));
    }

    [Fact]
    public void Ready_work_is_not_reserved_before_the_dispatcher_selects_it()
    {
        var queue = new TranslationQueue();

        Assert.False(queue.HasQueuedWork);
        queue.Enqueue("background", "ui_misc", foreground: false);
        Assert.True(queue.HasQueuedWork);

        Assert.True(queue.TryDequeue(out _));
        Assert.False(queue.HasQueuedWork);
    }

    [Fact]
    public void Background_work_progresses_after_a_foreground_burst()
    {
        var queue = new TranslationQueue();
        queue.Enqueue("background", "ui_misc", foreground: false);
        for (var i = 0; i < 5; i++)
            queue.Enqueue($"event-{i}", "system", foreground: true);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(queue.TryDequeue(out var foregroundJob));
            Assert.Equal($"event-{i}", foregroundJob.Template);
            queue.Release(foregroundJob);
        }

        Assert.True(queue.TryDequeue(out var backgroundJob));
        Assert.Equal("background", backgroundJob.Template);
    }

    [Fact]
    public void Translation_log_counters_report_and_reset_period_statistics()
    {
        var counters = new MachineTranslationLogCounters();
        counters.RecordEventEnqueued();
        counters.RecordPeriodicEnqueued(2);
        counters.RecordTranslated();
        counters.RecordFastRetry();
        counters.RecordPeriodicOnlyRetry();

        var report = counters.Drain(pending: 4);

        Assert.Equal(4, report.Pending);
        Assert.Equal(1, report.EventEnqueued);
        Assert.Equal(2, report.PeriodicEnqueued);
        Assert.Equal(1, report.Translated);
        Assert.Equal(1, report.FastRetries);
        Assert.Equal(1, report.PeriodicOnlyRetries);
        Assert.True(report.HasActivity);
        Assert.False(counters.Drain(pending: 0).HasActivity);
    }
}
