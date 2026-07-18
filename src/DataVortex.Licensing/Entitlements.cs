namespace DataVortex.Licensing;

/// <summary>A distinct thing the app can do. Kept granular ON PURPOSE: the old design funnelled every decision
/// through one boolean (<c>LicenseStatus.IsUsable</c>), so a single 2-byte patch unlocked everything. Each
/// capability is now its own question, answered from the SIGNED claims at the real execution site.</summary>
public enum Capability
{
    /// <summary>Archive Telegram channels (scan + download) — uses the user's own account.</summary>
    ScanTelegram,
    /// <summary>Run the download/extraction pipeline.</summary>
    RunPipeline,
    /// <summary>Check Passculture accounts (the differentiating feature).</summary>
    CheckPassculture,
    /// <summary>Idle history catch-up.</summary>
    Backfill,
    /// <summary>Export / browse extracted results.</summary>
    Export
}

/// <summary>What the current licence actually permits, derived ONLY from the cryptographically-signed
/// <see cref="LicenseClaims"/> (tier + feature flags) plus whether a live server session is held. There is no
/// global "am I licensed?" boolean to patch — callers ask <see cref="Can"/> a specific question at the point of
/// use, so neutralising one site doesn't unlock the rest. An attacker can't forge a higher tier or extra features
/// because <see cref="From"/> only trusts values that came out of a verified token.</summary>
public sealed class Entitlements
{
    private readonly bool _licensed;
    private readonly HashSet<string> _features;

    /// <summary>The signed tier. Higher tiers imply more baseline capabilities (see <see cref="AtLeast"/>).</summary>
    public LicenseType Type { get; }

    /// <summary>True while a live, server-verified session backs the licence (Palier B). Reserved: Phase B makes
    /// the online-only capabilities require it, so they go dark the moment it drops — with no central gate.</summary>
    public bool Online { get; }

    private Entitlements(bool licensed, LicenseType type, IEnumerable<string> features, bool online)
    {
        _licensed = licensed;
        Type = type;
        _features = new HashSet<string>(features, StringComparer.OrdinalIgnoreCase);
        Online = online;
    }

    /// <summary>No licence at all — denies every capability. This is what an un-fed gate holds, so bypassing the
    /// startup check (which is what feeds the gate) leaves every feature denied rather than unlocked.</summary>
    public static Entitlements None { get; } = new(licensed: false, LicenseType.Trial, Array.Empty<string>(), online: false);

    /// <summary>DEV/DEBUG only — grants everything so the app runs with no licence server. Compiled into Debug
    /// builds exclusively (see App startup); the Release binary contains no path that produces this.</summary>
    public static Entitlements Unrestricted { get; } = new(licensed: true, LicenseType.Enterprise, Array.Empty<string>(), online: true);

    /// <summary>Builds the entitlement set from a VERIFIED token's claims. <paramref name="online"/> is whether a
    /// live server session currently backs it.</summary>
    public static Entitlements From(LicenseClaims claims, bool online)
        => new(licensed: true, claims.Type, claims.Features, online);

    /// <summary>Answers one capability question. Phase A: any valid signed licence grants the core capabilities —
    /// the win over the old design is that this is asked at each real call-site, from signed claims, with no single
    /// reusable boolean. Phase B tightens RunPipeline / CheckPassculture to also require <see cref="Online"/>;
    /// Phase C makes CheckPassculture impossible without the session-keyed recipe regardless.</summary>
    public bool Can(Capability capability)
    {
        if (!_licensed) return false;
        return capability switch
        {
            // Offline-tolerant: use the user's own resources, allowed while the lease covers the licence.
            Capability.ScanTelegram     => true,
            Capability.Backfill         => true,
            Capability.Export           => true,
            // Online-only (Palier B): require a live server session, so a lapse / revocation kills them within one
            // session window regardless of the long offline lease. Palier C makes CheckPassculture impossible
            // without the session-keyed recipe even if this were bypassed.
            Capability.RunPipeline      => Online,
            Capability.CheckPassculture => Online,
            _ => false
        };
    }

    /// <summary>True if the signed claims carry an explicit feature flag (reserved for finer per-feature gating).</summary>
    public bool Has(string feature) => _licensed && _features.Contains(feature);

    /// <summary>True only for the tier(s) at or above <paramref name="minimum"/> (signed, so un-forgeable).</summary>
    public bool AtLeast(LicenseType minimum) => _licensed && Type >= minimum;
}
