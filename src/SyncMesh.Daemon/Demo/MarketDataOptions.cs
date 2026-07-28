using System.ComponentModel.DataAnnotations;

namespace SyncMesh.Daemon.Demo;

// Bound from the "Daemon:MarketDataGenerator" configuration section — see
// ARCHITECTURE.md -> Configuration. This project's first dependency on a
// live external network service — see docs/adr/0008-live-market-data-
// generator.md. ApiKey/Symbols default to exactly what Twelve Data's
// shared "demo" key supports with zero signup (verified directly against
// the live API: AAPL works, every other symbol 401s asking for a free
// personal key) — a real key unlocks more symbols/headroom, get one at
// https://twelvedata.com/pricing.
public sealed class MarketDataOptions
{
    public const string SectionName = "Daemon:MarketDataGenerator";

    public bool Enabled { get; set; } = true;

    [Required]
    public string ApiKey { get; set; } = "demo";

    [Required]
    public string BaseUrl { get; set; } = "https://api.twelvedata.com";

    public List<string> Symbols { get; set; } = ["AAPL"];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public decimal MinQuantity { get; set; } = 1m;
    public decimal MaxQuantity { get; set; } = 50m;

    // Chance, per tick, of cancelling one of this generator's own
    // still-open orders instead of placing a new one — same reasoning as
    // SyntheticOrderGeneratorOptions.
    public double CancelProbability { get; set; } = 0.3;
}
