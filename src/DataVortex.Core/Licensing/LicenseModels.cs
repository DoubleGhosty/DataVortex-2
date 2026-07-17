namespace DataVortex.Core.Licensing;

/// <summary>Runtime state of the licence, surfaced to the UI by the licence manager. Client-only: <see
/// cref="Degraded"/> and <see cref="Blocked"/> reflect a network outage / grace period, while <see
/// cref="Revoked"/> reflects a server decision confirmed at verification time. (The token claims themselves live
/// in the shared <c>DataVortex.Licensing</c> project.)</summary>
public enum LicenseState
{
    Unknown,          // not evaluated yet
    NotActivated,     // no licence on this machine
    Active,           // a valid, unexpired lease is held
    Degraded,         // lease expired AND server unreachable — running on the grace period
    Expired,          // the licence itself has passed its expiry date
    Revoked,          // revoked or suspended server-side (confirmed at /verify)
    HardwareChanged,  // the current fingerprint no longer matches the bound one
    Blocked           // grace period exhausted — features gated until re-verification
}
