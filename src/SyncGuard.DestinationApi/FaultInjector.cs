namespace SyncGuard.DestinationApi;

public enum FaultKind { Http500, Http503, Http429, Timeout, LostResponse }

/// <summary>
/// Fire <see cref="Fault"/> on every <see cref="EveryNth"/>-th incoming order request, <see cref="Count"/> times;
/// optionally only for requests carrying <see cref="OrderNumber"/>. Request counting (not randomness) keeps runs deterministic.
/// </summary>
public sealed record FaultRule(FaultKind Fault, int Count = 1, int EveryNth = 1, int? RetryAfterSeconds = null, int DelayMs = 10_000, string? OrderNumber = null)
{
    public string? Validate() =>
        Count is < 1 or > 100_000 ? "count must be 1..100000" :
        EveryNth is < 1 or > 1_000 ? "everyNth must be 1..1000" :
        RetryAfterSeconds is < 0 or > 3_600 ? "retryAfterSeconds must be 0..3600" :
        DelayMs is < 0 or > 120_000 ? "delayMs must be 0..120000" : null;
}

/// <summary>
/// Demo-only fault script held in memory. Losing it on restart is fine: it is test control, not business state.
/// Only registered when FaultInjection:Enabled is true.
/// </summary>
public sealed class FaultInjector
{
    private sealed class ActiveRule(FaultRule rule)
    {
        public FaultRule Rule { get; } = rule;
        public int Remaining { get; set; } = rule.Count;
        public long Seen { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly List<ActiveRule> _rules = [];

    public void Add(FaultRule rule) { lock (_gate) _rules.Add(new ActiveRule(rule)); }

    public void Clear() { lock (_gate) _rules.Clear(); }

    public object[] Snapshot()
    {
        lock (_gate) return [.. _rules.Select(r => new { r.Rule, r.Remaining })];
    }

    /// <summary>Called once per order request; returns the fault to apply, if any. First matching rule wins.</summary>
    public FaultRule? Next(string? orderNumber)
    {
        lock (_gate)
        {
            FaultRule? hit = null;
            foreach (var r in _rules)
            {
                if (r.Rule.OrderNumber is not null && r.Rule.OrderNumber != orderNumber) continue;
                r.Seen++;
                if (hit is null && r.Seen % r.Rule.EveryNth == 0)
                {
                    r.Remaining--;
                    hit = r.Rule;
                }
            }
            _rules.RemoveAll(r => r.Remaining <= 0);
            return hit;
        }
    }
}
