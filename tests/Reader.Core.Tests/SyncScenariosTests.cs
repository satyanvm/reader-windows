using Reader.Core.Data;
using Reader.Core.Model;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>Port of the Mac SyncScenariosTests (Android's SyncScenariosTest plus RoomPersistenceTest).</summary>
public class SyncScenariosTests
{
    private static ReadingPosition? Load(Device d) => d.Repo.Load(DOC);

    [Fact]
    public void ClockIsMonotonicAndOrdersAfterAnythingObserved()
    {
        long wall = 100;
        var clock = new HybridClock(() => wall);
        var a = clock.Now();
        var b = clock.Now(); // wall clock didn't move
        Assert.True(b > a);
        clock.Observe(10_000);
        Assert.True(clock.Now() > 10_000);
        wall = 5; // wall clock jumps backwards
        Assert.True(clock.Now() > 10_000);
    }

    [Fact]
    public void SavedPositionSurvivesAnAppRestart()
    {
        var laptop = new Device("laptop", null);
        laptop.Repo.SaveLocal(Pos(186, 0.5f));
        var reopened = laptop.Restarted();
        Assert.Equal(186, Load(reopened)?.PageIndex);
        Assert.Equal(0.5f, Load(reopened)?.PageOffset);
    }

    [Fact]
    public void ClockIsReseededFromDiskAfterRestartSoNewSavesStillWin()
    {
        var phone = new Device("phone", null, wallClock: 5_000);
        var first = phone.Repo.SaveLocal(Pos(10));
        var restarted = phone.Restarted();
        restarted.WallClock = 1_000; // clock went backwards
        var second = restarted.Repo.SaveLocal(Pos(11));
        Assert.True(second.Stamp > first.Stamp);
        Assert.Equal(11, Load(restarted)?.PageIndex);
    }

    [Fact]
    public void PositionReadOnOneDeviceOpensOnAnother() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server);
        var phone = new Device("phone", server);

        laptop.Repo.SaveLocal(Pos(186, 0.5f));
        Assert.True(await laptop.Sync.PushPendingAsync());
        Assert.Empty(laptop.Repo.Pending());

        var remote = await phone.Sync.PullAsync(DOC);
        Assert.Equal(186, remote?.PageIndex);
        Assert.Equal(186, Load(phone)?.PageIndex);
    });

    [Fact]
    public void OfflineReadingSyncsWhenConnectivityReturns() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server);
        laptop.Repo.SaveLocal(Pos(50));
        await laptop.Sync.PushPendingAsync();

        server.Online = false;
        laptop.WallClock += 60_000;
        laptop.Repo.SaveLocal(Pos(120));
        Assert.False(await laptop.Sync.PushPendingAsync()); // push fails while offline
        Assert.Single(laptop.Repo.Pending());
        Assert.Equal(50, server.Rows[DOC].PageIndex);

        server.Online = true;
        Assert.True(await laptop.Sync.PushPendingAsync());
        Assert.Equal(120, server.Rows[DOC].PageIndex);
        Assert.Empty(laptop.Repo.Pending());
    });

    [Fact]
    public void NewerReadingOnAnotherDeviceWinsOverOlderOfflineProgress() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server, wallClock: 1_000_000);
        var phone = new Device("phone", server, wallClock: 1_000_000);

        server.Online = false;
        laptop.Repo.SaveLocal(Pos(100)); // read offline on the laptop...
        server.Online = true;
        phone.WallClock = 2_000_000;
        phone.Repo.SaveLocal(Pos(250)); // ...then later on the phone, online
        await phone.Sync.PushPendingAsync();

        await laptop.Sync.PushPendingAsync(); // laptop reconnects; its older position loses
        Assert.Equal(250, server.Rows[DOC].PageIndex);
        Assert.Equal(250, Load(laptop)?.PageIndex);
        Assert.Empty(laptop.Repo.Pending());
    });

    [Fact]
    public void RestartFromTheBeginningPropagatesEvenThoughItsALowerPage() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server);
        var phone = new Device("phone", server);
        laptop.Repo.SaveLocal(Pos(300));
        await laptop.Sync.PushPendingAsync();
        await phone.Sync.PullAsync(DOC);

        phone.WallClock += 1_000;
        phone.Repo.SaveLocal(Pos(0));
        await phone.Sync.PushPendingAsync();
        await laptop.Sync.PullAsync(DOC);
        Assert.Equal(0, Load(laptop)?.PageIndex);
    });

    [Fact]
    public void LateOrOutOfOrderServerResponsesNeverOverwriteNewerLocalProgress()
    {
        var server = new FakeServer();
        var phone = new Device("phone", server);
        var old = new Device("tablet", server).Repo.SaveLocal(Pos(40));
        server.Put(old);

        phone.WallClock += 10_000;
        phone.Repo.SaveLocal(Pos(90));

        // A response for an old request arrives after the newer local save.
        var winner = phone.Repo.MergeRemote(old);
        Assert.Equal(90, winner.PageIndex);
        Assert.Equal(90, Load(phone)?.PageIndex);
        Assert.Single(phone.Repo.Pending()); // still needs pushing

        // Responses delivered in reverse order converge to the newest.
        var a = new Stamp(5_000_000, "x");
        var b = new Stamp(6_000_000, "x");
        phone.Repo.MergeRemote(Pos(200).WithStamp(b));
        phone.Repo.MergeRemote(Pos(150).WithStamp(a));
        Assert.Equal(200, Load(phone)?.PageIndex);
    }

    [Fact]
    public void DeviceWithASlowClockStillOrdersAfterWhatItHasSeen() => Run(async () =>
    {
        var server = new FakeServer();
        var fast = new Device("fast", server, wallClock: 9_000_000);
        var slow = new Device("slow", server, wallClock: 1_000_000);

        fast.Repo.SaveLocal(Pos(100));
        await fast.Sync.PushPendingAsync();
        await slow.Sync.PullAsync(DOC);

        // Later reading on the slow-clock device must win despite its smaller wall time.
        slow.Repo.SaveLocal(Pos(101));
        await slow.Sync.PushPendingAsync();
        Assert.Equal(101, server.Rows[DOC].PageIndex);
    });

    [Fact]
    public void EqualStampsFromARetriedPushClearPendingWithoutRewriting() => Run(async () =>
    {
        var server = new FakeServer();
        var phone = new Device("phone", server);
        phone.Repo.SaveLocal(Pos(7));
        await phone.Sync.PushPendingAsync();
        await phone.Sync.PushPendingAsync(); // nothing pending, no-op
        Assert.Empty(phone.Repo.Pending());
        Assert.Equal(7, server.Rows[DOC].PageIndex);
    });

    [Fact]
    public void SyncIsDisabledWithoutAServer() => Run(async () =>
    {
        var laptop = new Device("laptop", null);
        laptop.Repo.SaveLocal(Pos(3));
        Assert.False(laptop.Sync.IsEnabled);
        Assert.Null(await laptop.Sync.PullAsync(DOC));
        Assert.True(await laptop.Sync.PushPendingAsync());
        Assert.Single(laptop.Repo.Pending()); // kept for when a server is configured
    });

    // Real SQLite (Android: RoomPersistenceTest)

    [Fact]
    public void SqliteFileClosedAndReopenedKeepsThePositionAndTheClock()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "reader.sqlite");
        long wall = 9_000_000;
        var db = new ReaderDatabase(path);
        var repo = new ReadingPositionRepository(db, new HybridClock(() => wall), "mac");
        var saved = repo.SaveLocal(Pos(186, 0.4f));
        db.Sql.Close();

        // A fresh process: new connection, new clock that is behind.
        wall = 1_000;
        using var reopened = new ReaderDatabase(path);
        var repo2 = new ReadingPositionRepository(reopened, new HybridClock(() => wall), "mac");
        var loaded = repo2.Load(DOC);
        Assert.NotNull(loaded);
        Assert.Equal(saved, loaded); // every field, including float bits and the stamp
        Assert.Equal(BitConverter.SingleToInt32Bits(0.4f), BitConverter.SingleToInt32Bits(loaded!.PageOffset));
        Assert.Single(repo2.Pending());
        var next = repo2.SaveLocal(Pos(187));
        Assert.True(next.Stamp > saved.Stamp); // clock reseeded from disk

        var mode = reopened.Sql.Query("PRAGMA journal_mode").FirstOrDefault()?.String("journal_mode");
        Assert.Equal("wal", mode);
        var sync = reopened.Sql.Query("PRAGMA synchronous").FirstOrDefault()?.Int("synchronous");
        Assert.Equal(2, sync); // FULL
    }
}
