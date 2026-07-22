namespace SyncMesh.Contracts;

// Combines wall-clock time with a logical counter so that causally-related
// events across sites can be ordered deterministically, without requiring
// synchronized clocks. See docs/06-data-model.md §3 and ADR-0003.
public readonly record struct HybridLogicalClock(long PhysicalTicks, int LogicalCounter)
    : IComparable<HybridLogicalClock>
{
    public int CompareTo(HybridLogicalClock other)
    {
        var physical = PhysicalTicks.CompareTo(other.PhysicalTicks);
        return physical != 0 ? physical : LogicalCounter.CompareTo(other.LogicalCounter);
    }

    public static bool operator <(HybridLogicalClock left, HybridLogicalClock right) => left.CompareTo(right) < 0;
    public static bool operator >(HybridLogicalClock left, HybridLogicalClock right) => left.CompareTo(right) > 0;
    public static bool operator <=(HybridLogicalClock left, HybridLogicalClock right) => left.CompareTo(right) <= 0;
    public static bool operator >=(HybridLogicalClock left, HybridLogicalClock right) => left.CompareTo(right) >= 0;
}
