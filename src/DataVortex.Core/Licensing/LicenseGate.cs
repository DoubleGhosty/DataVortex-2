using DataVortex.Licensing;

namespace DataVortex.Core.Licensing;

/// <summary>Thrown when a licensed capability is invoked without the entitlement. Business services throw this at
/// the point of use, so there is no single boolean to patch — the check lives where the work happens.</summary>
public sealed class LicenseDeniedException : Exception
{
    public Capability Capability { get; }
    public LicenseDeniedException(Capability capability)
        : base($"This build is not licensed for {capability}.") => Capability = capability;
}

/// <summary>The runtime entitlement holder. The licence layer (App's LicenseGuard) pushes the current
/// <see cref="Entitlements"/> in via <see cref="Set"/>; the Core business services read it at their real
/// execution points via <see cref="Allows"/> / <see cref="Require"/>. It defaults to <see cref="Entitlements.None"/>
/// (deny-all), which is the whole point: if the startup licence check is bypassed, nothing ever feeds this gate, so
/// every gated feature stays denied — a cracked shell that does nothing, instead of a single unlocked boolean.</summary>
public interface ILicenseGate
{
    Entitlements Current { get; }
    void Set(Entitlements entitlements);
    bool Allows(Capability capability);
    void Require(Capability capability);

    /// <summary>Permanently deny everything for the rest of the process. Called by the tamper watchdog on
    /// detection (debugger / integrity failure), after a delay, so features go dark far from the check site and
    /// the guard re-feeding entitlements can't undo it.</summary>
    void Trip();
}

/// <inheritdoc cref="ILicenseGate"/>
public sealed class LicenseGate : ILicenseGate
{
    private volatile Entitlements _current = Entitlements.None;
    private volatile bool _tripped;

    public Entitlements Current => _current;

    public void Set(Entitlements entitlements) => _current = entitlements ?? Entitlements.None;

    public void Trip() => _tripped = true;

    public bool Allows(Capability capability) => !_tripped && _current.Can(capability);

    public void Require(Capability capability)
    {
        if (_tripped || !_current.Can(capability)) throw new LicenseDeniedException(capability);
    }
}
