namespace SyncMesh.Contracts;

// Assigns and merges Hybrid Logical Clock values for one site (one daemon
// or server process). Register as a singleton — the internal counter must
// be monotonic across every event this process produces. See
// docs/06-data-model.md §3: this is a starting point, not a drop-in
// production library — clock-skew handling and counter-overflow behavior
// should be validated against real deployment conditions.
public sealed class HlcGenerator
{
    private long _lastPhysical;
    private int _counter;
    private readonly object _lock = new();

    public HybridLogicalClock Next()
    {
        lock (_lock)
        {
            var physicalNow = DateTimeOffset.UtcNow.UtcTicks;
            if (physicalNow > _lastPhysical)
            {
                _lastPhysical = physicalNow;
                _counter = 0;
            }
            else
            {
                _counter++;
            }
            return new HybridLogicalClock(_lastPhysical, _counter);
        }
    }

    // Call when receiving an event from another site, to fold its clock
    // into ours and preserve causal ordering going forward.
    public HybridLogicalClock Merge(HybridLogicalClock received)
    {
        lock (_lock)
        {
            var physicalNow = DateTimeOffset.UtcNow.UtcTicks;
            var maxPhysical = Math.Max(physicalNow, Math.Max(_lastPhysical, received.PhysicalTicks));

            if (maxPhysical == _lastPhysical && maxPhysical == received.PhysicalTicks)
                _counter = Math.Max(_counter, received.LogicalCounter) + 1;
            else if (maxPhysical == _lastPhysical)
                _counter++;
            else if (maxPhysical == received.PhysicalTicks)
                _counter = received.LogicalCounter + 1;
            else
                _counter = 0;

            _lastPhysical = maxPhysical;
            return new HybridLogicalClock(_lastPhysical, _counter);
        }
    }
}
