namespace SyncMesh.Daemon.Demo;

// Bound from the "Daemon:SyntheticOrderGenerator" configuration section —
// see ARCHITECTURE.md -> Configuration. Off by default: SyncMesh.Daemon is
// the real, reusable daemon component, and fabricating orders on every
// run isn't a sensible zero-configuration default for it — see the
// 2026-07-28 review finding. SyncMesh.AppHost's demo topology explicitly
// turns this on for daemon-a so the mesh is still visibly alive there
// without any manual steps.
public sealed class SyntheticOrderGeneratorOptions
{
    public const string SectionName = "Daemon:SyntheticOrderGenerator";

    public bool Enabled { get; set; }
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public List<string> Symbols { get; set; } = ["ACME", "GLOBEX", "INITECH"];
    public decimal MinPrice { get; set; } = 10m;
    public decimal MaxPrice { get; set; } = 200m;
    public decimal MinQuantity { get; set; } = 1m;
    public decimal MaxQuantity { get; set; } = 100m;

    // Chance, per tick, of cancelling one of this generator's own
    // still-open orders instead of placing a new one — gives the book
    // visible churn (orders appearing AND disappearing), not just
    // monotonic growth.
    public double CancelProbability { get; set; } = 0.3;
}
