using DataVortex.Core.Models;
using DataVortex.Core.Pipeline;
using DataVortex.Core.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DataVortex.Tests;

public sealed class DeduplicatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvtest_" + Guid.NewGuid().ToString("N"));

    private DownloadDeduplicator NewDedup()
    {
        var paths = new AppPaths(_dir).EnsureCreated();
        var storage = new StorageService(paths);
        return new DownloadDeduplicator(storage, NullLogger<DownloadDeduplicator>.Instance);
    }

    [Fact]
    public void Duplicate_by_id_or_size_and_name_is_rejected()
    {
        var d = NewDedup();
        Assert.True(d.TryReserve(1, 100, "a.rar"));
        Assert.False(d.TryReserve(1, 100, "a.rar")); // same id
        Assert.False(d.TryReserve(2, 100, "a.rar")); // same size + name (re-upload, different id)
        Assert.True(d.TryReserve(3, 200, "b.rar"));   // genuinely different
    }

    [Fact]
    public void Reservation_alone_does_not_survive_restart()
    {
        NewDedup().TryReserve(1, 100, "a.rar");        // reserved, never committed
        Assert.True(NewDedup().TryReserve(1, 100, "a.rar")); // a fresh instance can still take it
    }

    [Fact]
    public void Commit_survives_restart()
    {
        var d1 = NewDedup();
        d1.TryReserve(1, 100, "a.rar");
        d1.Commit(1, 100, "a.rar");
        Assert.False(NewDedup().TryReserve(1, 100, "a.rar")); // remembered across "restart"
    }

    [Fact]
    public void RemoveReservation_allows_retry()
    {
        var d = NewDedup();
        d.TryReserve(1, 100, "a.rar");
        Assert.True(d.RemoveReservation(1, 100, "a.rar"));
        Assert.True(d.TryReserve(1, 100, "a.rar")); // available again after release
    }

    [Fact]
    public void Clear_wipes_everything()
    {
        var d = NewDedup();
        d.TryReserve(1, 100, "a.rar");
        d.Commit(1, 100, "a.rar");
        d.Clear();
        Assert.True(d.TryReserve(1, 100, "a.rar"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools(); // release the SQLite file handles before deleting the temp dir
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
