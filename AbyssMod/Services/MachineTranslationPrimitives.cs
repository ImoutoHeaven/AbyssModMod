using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;

namespace AbyssMod.Services;

internal enum TranslationFailureDisposition
{
    FastRetry,
    PeriodicOnly,
}

internal readonly record struct TranslationJob(
    string Key,
    string Template,
    string Category,
    string ContextPath,
    int QueueVersion
);

internal sealed class PendingTranslation
{
    public string Category { get; set; } = "ui_misc";

    public string Template { get; set; }

    public string ContextPath { get; set; }

    public int FastRetryCount { get; set; }

    public bool PeriodicOnly { get; set; }

    [JsonIgnore]
    public bool Queued { get; set; }

    [JsonIgnore]
    public bool InFlight { get; set; }

    [JsonIgnore]
    public bool Foreground { get; set; }

    [JsonIgnore]
    public int QueueVersion { get; set; }

    public PendingTranslation Clone() => new()
    {
        Category = Category,
        Template = Template,
        ContextPath = ContextPath,
        FastRetryCount = FastRetryCount,
        PeriodicOnly = PeriodicOnly,
    };
}

/// <summary>
/// Owns pending translation state and two FIFO queues. All mutable state is protected
/// by one lock so game-thread enqueueing and background dispatching cannot duplicate work.
/// </summary>
internal sealed class TranslationQueue
{
    private const int ForegroundBurstLimit = 4;

    private readonly object _lock = new();
    private readonly Dictionary<string, PendingTranslation> _pending = new(StringComparer.Ordinal);
    private readonly Queue<TranslationJob> _foreground = new();
    private readonly Queue<TranslationJob> _background = new();
    private int _foregroundDequeuesSinceBackground;

    public int Count
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    public bool HasQueuedWork
    {
        get
        {
            lock (_lock)
                return _foreground.Count > 0 || _background.Count > 0;
        }
    }

    public bool Enqueue(string template, string category, bool foreground)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(template, out var pending))
            {
                pending = new PendingTranslation { Category = category, Template = template };
                _pending.Add(template, pending);
            }

            return EnqueueNoLock(template, pending, foreground);
        }
    }

    public bool EnqueueContextual(
        string contextPath,
        string template,
        string category,
        bool foreground
    )
    {
        string key = ContextualMachineTranslationProtocol.BuildPendingKey(contextPath, template);
        lock (_lock)
        {
            if (!_pending.TryGetValue(key, out var pending))
            {
                pending = new PendingTranslation
                {
                    Category = category,
                    Template = template,
                    ContextPath = contextPath,
                };
                _pending.Add(key, pending);
            }

            return EnqueueNoLock(key, pending, foreground);
        }
    }

    public void AddPending(string template, PendingTranslation pending)
    {
        lock (_lock)
        {
            pending.Template ??= template;
            _pending.TryAdd(template, pending);
        }
    }

    public bool Contains(string template)
    {
        lock (_lock)
            return _pending.ContainsKey(template);
    }

    public bool Remove(string template)
    {
        lock (_lock)
            return _pending.Remove(template);
    }

    public bool TryDequeue(out TranslationJob job)
    {
        lock (_lock)
        {
            while (TryTakeNoLock(out var candidate, out var foreground))
            {
                if (!_pending.TryGetValue(candidate.Key, out var pending)
                    || !pending.Queued
                    || pending.InFlight
                    || pending.QueueVersion != candidate.QueueVersion)
                    continue;

                pending.Queued = false;
                pending.InFlight = true;
                if (foreground)
                    _foregroundDequeuesSinceBackground++;
                else
                    _foregroundDequeuesSinceBackground = 0;
                job = candidate;
                return true;
            }
        }

        job = default;
        return false;
    }

    public void CompleteSuccess(TranslationJob job)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(job.Key, out var pending) && pending.InFlight)
                _pending.Remove(job.Key);
        }
    }

    public TranslationFailureDisposition CompleteFailure(TranslationJob job, int fastRetryCount)
    {
        lock (_lock)
        {
            if (!_pending.TryGetValue(job.Key, out var pending))
                return TranslationFailureDisposition.PeriodicOnly;

            pending.InFlight = false;
            if (pending.PeriodicOnly)
                return TranslationFailureDisposition.PeriodicOnly;

            if (pending.FastRetryCount < Math.Max(0, fastRetryCount))
            {
                pending.FastRetryCount++;
                EnqueueNoLock(job.Key, pending, foreground: false);
                return TranslationFailureDisposition.FastRetry;
            }

            pending.PeriodicOnly = true;
            return TranslationFailureDisposition.PeriodicOnly;
        }
    }

    public void Release(TranslationJob job)
    {
        lock (_lock)
        {
            if (_pending.TryGetValue(job.Key, out var pending))
                pending.InFlight = false;
        }
    }

    public int EnqueuePeriodicPending()
    {
        lock (_lock)
        {
            var queued = 0;
            foreach (var item in _pending)
                if (EnqueueNoLock(item.Key, item.Value, foreground: false))
                    queued++;
            return queued;
        }
    }

    public bool IsPeriodicOnly(string template)
    {
        lock (_lock)
            return _pending.TryGetValue(template, out var pending) && pending.PeriodicOnly;
    }

    public Dictionary<string, PendingTranslation> Snapshot()
    {
        lock (_lock)
            return _pending.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
    }

    private bool EnqueueNoLock(string template, PendingTranslation pending, bool foreground)
    {
        if (pending.InFlight || (foreground && pending.PeriodicOnly))
            return false;

        if (pending.Queued && (!foreground || pending.Foreground))
            return false;

        pending.Queued = true;
        pending.Foreground = foreground;
        pending.QueueVersion++;
        var job = new TranslationJob(
            template,
            pending.Template ?? template,
            pending.Category,
            pending.ContextPath,
            pending.QueueVersion
        );
        if (foreground)
            _foreground.Enqueue(job);
        else
            _background.Enqueue(job);
        return true;
    }

    private bool TryTakeNoLock(out TranslationJob job, out bool foreground)
    {
        if (_foreground.Count > 0
            && (_background.Count == 0 || _foregroundDequeuesSinceBackground < ForegroundBurstLimit))
        {
            job = _foreground.Dequeue();
            foreground = true;
            return true;
        }

        if (_background.Count > 0)
        {
            job = _background.Dequeue();
            foreground = false;
            return true;
        }

        job = default;
        foreground = false;
        return false;
    }
}

internal sealed class RequestStartRateLimiter
{
    private readonly object _lock = new();
    private DateTime _nextRequestStartUtc = DateTime.MinValue;

    public TimeSpan ReserveDelay(DateTime nowUtc, int requestsPerSecond)
    {
        var interval = TimeSpan.FromSeconds(1d / Math.Max(1, requestsPerSecond));
        lock (_lock)
        {
            var start = nowUtc > _nextRequestStartUtc ? nowUtc : _nextRequestStartUtc;
            _nextRequestStartUtc = start + interval;
            return start - nowUtc;
        }
    }
}

internal sealed class RequestInFlightGate
{
    private readonly object _lock = new();
    private int _count;

    public bool TryAcquire(int maximumInFlight)
    {
        lock (_lock)
        {
            if (_count >= Math.Max(1, maximumInFlight))
                return false;
            _count++;
            return true;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            if (_count > 0)
                _count--;
        }
    }
}

internal readonly record struct MachineTranslationLogStats(
    int Pending,
    int EventEnqueued,
    int PeriodicEnqueued,
    int Translated,
    int FastRetries,
    int PeriodicOnlyRetries
)
{
    public bool HasActivity =>
        EventEnqueued > 0
        || PeriodicEnqueued > 0
        || Translated > 0
        || FastRetries > 0
        || PeriodicOnlyRetries > 0;
}

internal sealed class MachineTranslationLogCounters
{
    private int _eventEnqueued;
    private int _periodicEnqueued;
    private int _translated;
    private int _fastRetries;
    private int _periodicOnlyRetries;

    public void RecordEventEnqueued() => Interlocked.Increment(ref _eventEnqueued);

    public void RecordPeriodicEnqueued(int count)
    {
        if (count > 0)
            Interlocked.Add(ref _periodicEnqueued, count);
    }

    public void RecordTranslated() => Interlocked.Increment(ref _translated);

    public void RecordFastRetry() => Interlocked.Increment(ref _fastRetries);

    public void RecordPeriodicOnlyRetry() => Interlocked.Increment(ref _periodicOnlyRetries);

    public MachineTranslationLogStats Drain(int pending) => new(
        pending,
        Interlocked.Exchange(ref _eventEnqueued, 0),
        Interlocked.Exchange(ref _periodicEnqueued, 0),
        Interlocked.Exchange(ref _translated, 0),
        Interlocked.Exchange(ref _fastRetries, 0),
        Interlocked.Exchange(ref _periodicOnlyRetries, 0)
    );
}
