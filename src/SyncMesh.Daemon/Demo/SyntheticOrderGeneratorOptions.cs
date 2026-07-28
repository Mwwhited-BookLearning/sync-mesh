namespace SyncMesh.Daemon.Demo;

// Bound from the "Daemon:SyntheticOrderGenerator" configuration section —
// see ARCHITECTURE.md -> Configuration. On by default so a freshly-run
// dev topology (SyncMesh.AppHost) is visibly alive without any manual
// steps — this is what makes "leaf nodes generating data to be
// replicated across the mesh" something you can actually watch happen.
public sealed class SyntheticOrderGeneratorOptions
{
    public const string SectionName = "Daemon:SyntheticOrderGenerator";

    public bool Enabled { get; set; } = true;
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
