using System.ComponentModel.DataAnnotations;

namespace SyncMesh.MeshMonitor.Api.Auth;

// Bound from the "MeshMonitor:Auth" configuration section — see
// ARCHITECTURE.md → Configuration for the smart-defaults convention this
// project inherits from the rest of the solution. SigningKey is the one
// deliberate exception to "every option has a smart default": a baked-in
// default signing key would be actively insecure (anyone reading this
// source could forge tokens), the same reasoning already applied to
// EventStore connection strings elsewhere in this solution (required,
// not defaulted, fails fast at startup instead).
//
// This dashboard does not issue bearer tokens itself (see
// docs/adr/0009-ticket-based-signalr-auth.md) — some external issuer
// signs JWTs with this same symmetric key; this options class only
// carries what's needed to validate one.
public sealed class MeshMonitorAuthOptions
{
    public const string SectionName = "MeshMonitor:Auth";

    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    // Optional — left null means "don't validate this claim," since an
    // external issuer's exact Issuer/Audience values aren't this
    // project's to assume. Set both if the issuer sets them.
    public string? Issuer { get; set; }

    public string? Audience { get; set; }
}
