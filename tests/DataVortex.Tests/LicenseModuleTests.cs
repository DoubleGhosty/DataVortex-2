using System.Security.Cryptography;
using DataVortex.Core.Licensing;
using DataVortex.Licensing;
using Xunit;

namespace DataVortex.Tests;

/// <summary>Covers the client licence layer: DPAPI store round-trip, and the LicenseManager state machine
/// (activation, valid/expired lease, renewal, revocation, grace/degraded, hardware change, clock rollback).</summary>
public sealed class LicenseModuleTests : IDisposable
{
    // ---------------------------------------------------------------- DPAPI store

    [Fact]
    public void DpapiStore_roundtrips_andClears()
    {
        var path = Path.Combine(Path.GetTempPath(), "dvx_lic_" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            var store = new DpapiLicenseStore(path);
            Assert.Null(store.Load()); // nothing yet

            var data = new LicenseStoreData("payload.signature", HardwareFingerprint.Collect().Snapshot(), DateTimeOffset.UtcNow);
            store.Save(data);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("payload.signature", loaded!.Token);
            Assert.Equal(data.Reference.Hash, loaded.Reference.Hash);
            Assert.Equal(data.LastSeen.ToUnixTimeSeconds(), loaded.LastSeen.ToUnixTimeSeconds());

            store.Clear();
            Assert.Null(store.Load());
        }
        finally { try { File.Delete(path); } catch { /* ignore */ } }
    }

    // ---------------------------------------------------------------- manager fixtures

    private sealed class FakeStore : ILicenseStore
    {
        public LicenseStoreData? Data;
        public LicenseStoreData? Load() => Data;
        public void Save(LicenseStoreData data) => Data = data;
        public void Clear() => Data = null;
    }

    private sealed class FakeApi : ILicenseApiClient
    {
        public LicenseResponse ActivateResult = new(false, null, LicenseServerStatus.ServerError, null);
        public LicenseResponse VerifyResult = new(false, null, LicenseServerStatus.ServerError, null);
        public LicenseResponse RenewResult = new(false, null, LicenseServerStatus.ServerError, null);
        public bool ThrowOnVerify;

        public Task<LicenseResponse> ActivateAsync(ActivationRequest request, CancellationToken ct = default) => Task.FromResult(ActivateResult);
        public Task<LicenseResponse> VerifyAsync(string token, FingerprintSnapshot fingerprint, CancellationToken ct = default)
            => ThrowOnVerify ? throw new HttpRequestException("offline") : Task.FromResult(VerifyResult);
        public Task<LicenseResponse> RenewAsync(string token, CancellationToken ct = default) => Task.FromResult(RenewResult);
        public Task DeactivateAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Dispose() => _key.Dispose();

    private LicenseOptions Options() => new()
    {
        PublicKeys = new[] { Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) },
        FingerprintThreshold = 0.6,
        GracePeriod = TimeSpan.FromDays(5),
    };

    private string Token(DateTimeOffset leaseExp, DateTimeOffset? licenseExp) => LicenseToken.Sign(new LicenseClaims
    {
        LicenseId = "LIC",
        Type = LicenseType.Pro,
        Features = new[] { "checker" },
        FingerprintHash = "bound",
        IssuedAt = Base,
        LeaseExpiresAt = leaseExp,
        LicenseExpiresAt = licenseExp,
    }, _key, "k1");

    private static FingerprintSnapshot RealFp() => HardwareFingerprint.Collect().Snapshot();

    // ---------------------------------------------------------------- manager scenarios

    [Fact]
    public async Task NoLicence_isNotActivated()
    {
        var mgr = new LicenseManager(new FakeStore(), new FakeApi(), Options(), () => Base);
        Assert.Equal(LicenseState.NotActivated, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task Activation_ok_persistsAndBecomesActive()
    {
        var store = new FakeStore();
        var api = new FakeApi { ActivateResult = new(true, Token(Base.AddDays(14), Base.AddYears(1)), LicenseServerStatus.Ok, null) };
        var mgr = new LicenseManager(store, api, Options(), () => Base);

        var st = await mgr.ActivateAsync("A7F2K-9QW3E");
        Assert.Equal(LicenseState.Active, st.State);
        Assert.Equal(LicenseType.Pro, st.Claims!.Type);
        Assert.NotNull(store.Data);
    }

    [Fact]
    public async Task Activation_invalidKey_reportsReason()
    {
        var api = new FakeApi { ActivateResult = new(false, null, LicenseServerStatus.InvalidKey, null) };
        var mgr = new LicenseManager(new FakeStore(), api, Options(), () => Base);

        var st = await mgr.ActivateAsync("nope");
        Assert.Equal(LicenseState.NotActivated, st.State);
        Assert.Contains("invalide", st.Message!);
    }

    [Fact]
    public async Task ValidLease_isActiveOffline_withoutCallingServer()
    {
        var store = new FakeStore { Data = new(Token(Base.AddDays(14), Base.AddYears(1)), RealFp(), Base) };
        var api = new FakeApi { ThrowOnVerify = true }; // would blow up if called
        var mgr = new LicenseManager(store, api, Options(), () => Base.AddDays(2));

        Assert.Equal(LicenseState.Active, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task ExpiredLease_reverifies_andRenews()
    {
        var store = new FakeStore { Data = new(Token(Base.AddDays(10), Base.AddYears(1)), RealFp(), Base) };
        var api = new FakeApi { VerifyResult = new(true, Token(Base.AddDays(30), Base.AddYears(1)), LicenseServerStatus.Ok, null) };
        var mgr = new LicenseManager(store, api, Options(), () => Base.AddDays(20));

        Assert.Equal(LicenseState.Active, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task ExpiredLease_revoked_blocksAndWipes()
    {
        var store = new FakeStore { Data = new(Token(Base.AddDays(10), Base.AddYears(1)), RealFp(), Base) };
        var api = new FakeApi { VerifyResult = new(false, null, LicenseServerStatus.Revoked, null) };
        var mgr = new LicenseManager(store, api, Options(), () => Base.AddDays(20));

        Assert.Equal(LicenseState.Revoked, (await mgr.EvaluateAsync()).State);
        Assert.Null(store.Data);
    }

    [Fact]
    public async Task ExpiredLease_offline_withinGrace_isDegraded()
    {
        var store = new FakeStore { Data = new(Token(Base.AddDays(10), Base.AddYears(1)), RealFp(), Base) };
        var api = new FakeApi { ThrowOnVerify = true };
        var mgr = new LicenseManager(store, api, Options(), () => Base.AddDays(12)); // grace ends day 15

        Assert.Equal(LicenseState.Degraded, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task ExpiredLease_offline_pastGrace_isBlocked()
    {
        var store = new FakeStore { Data = new(Token(Base.AddDays(10), Base.AddYears(1)), RealFp(), Base) };
        var api = new FakeApi { ThrowOnVerify = true };
        var mgr = new LicenseManager(store, api, Options(), () => Base.AddDays(20)); // past grace (day 15)

        Assert.Equal(LicenseState.Blocked, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task DifferentHardware_isFlagged()
    {
        var bogus = new FingerprintSnapshot(new[]
        {
            new ComponentHash("machine-name", "deadbeef", 1),
            new ComponentHash("mac", "deadbeef", 3),
            new ComponentHash("os", "deadbeef", 1),
            new ComponentHash("cpu-arch", "deadbeef", 1),
            new ComponentHash("cpu-count", "deadbeef", 1),
        });
        var store = new FakeStore { Data = new(Token(Base.AddDays(14), Base.AddYears(1)), bogus, Base) };
        var mgr = new LicenseManager(store, new FakeApi(), Options(), () => Base.AddDays(1));

        Assert.Equal(LicenseState.HardwareChanged, (await mgr.EvaluateAsync()).State);
    }

    [Fact]
    public async Task ClockRolledBack_isBlocked()
    {
        // Last trusted server time is far in the future relative to "now" → the clock was set back.
        var store = new FakeStore { Data = new(Token(Base.AddDays(14), Base.AddYears(1)), RealFp(), Base.AddDays(100)) };
        var mgr = new LicenseManager(store, new FakeApi(), Options(), () => Base.AddDays(20));

        Assert.Equal(LicenseState.Blocked, (await mgr.EvaluateAsync()).State);
    }
}
