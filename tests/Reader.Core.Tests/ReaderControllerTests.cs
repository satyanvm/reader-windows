using Reader.Core.Model;
using Reader.Core.Reader;
using Reader.Core.Telemetry;
using Xunit;
using static Reader.Core.Tests.T;

namespace Reader.Core.Tests;

/// <summary>
/// Port of the Mac ReaderControllerTests (itself a port of Android's ReaderControllerTest).
/// Drives the controller the way the UI does, on virtual time.
/// </summary>
public class ReaderControllerTests
{
    private readonly LayoutKey _phoneLayout = new(1080, 1);
    private readonly RecordingTelemetry _telemetry = new();
    private readonly TestScheduler _scheduler = new();

    private async Task<ReaderController> Open(Device device, long waitMs = 1_500, ReadingMode mode = ReadingMode.Pages)
    {
        var c = new ReaderController(
            DOC,
            () => Task.FromResult(new LoadedDocument("Book", new FakeDocument(), mode)),
            device.Repo,
            device.Sync,
            _telemetry,
            _scheduler,
            waitMs);
        c.Start();
        await c.StartTask!;
        return c;
    }

    private static ReadingPosition? Target(ReaderController c) => c.State.Phase.AsRestoring?.Target;
    private static ReadingPosition? Load(Device d) => d.Repo.Load(DOC);

    [Fact]
    public void FirstOpenWithNoHistoryStartsAtTheBeginning() => Run(async () =>
    {
        var c = await Open(new Device("phone", null));
        Assert.NotNull(c.State.Phase.AsRestoring);
        Assert.Null(Target(c));
        c.ApplyRestore(_phoneLayout);
        Assert.Equal(Phase.ReadingPhase, c.State.Phase);
        Assert.Equal([RestoreEvent.FirstOpen], _telemetry.Events);
    });

    [Fact]
    public void ReopenResumesAtTheSavedPageAndOffset() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(186, 0.5f));

        var c = await Open(device.Restarted());
        Assert.Equal(186, Target(c)?.PageIndex);
        Assert.Equal(0.5f, Target(c)?.PageOffset);
        c.ApplyRestore(_phoneLayout);
        Assert.Equal(new Notice.Resumed(186), c.State.Notice);
        Assert.Contains(RestoreEvent.RestoredLocal, _telemetry.Events);
    });

    [Fact]
    public void ScrollReportsDuringLoadingAndLayoutNeverOverwriteSavedProgress() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(186, 0.5f));
        var c = await Open(device);

        // The view lays out at page 0 before the restore is applied.
        c.OnLayout(_phoneLayout);
        c.OnScrolled(_phoneLayout, 0, 0, 0);
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.Equal(186, Load(device)?.PageIndex);

        c.ApplyRestore(_phoneLayout);
        c.OnScrolled(_phoneLayout, 186, 0.5f, 0); // not a move
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.Equal(186, Load(device)?.PageIndex);
    });

    [Fact]
    public void ReadingIsSavedAfterTheDebounceWithoutAnyLifecycleEvent() => Run(async () =>
    {
        var device = new Device("laptop", null);
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        c.OnScrolled(_phoneLayout, 186, 0.4f, 0);
        _scheduler.Advance(499);
        Assert.Null(Load(device)); // debounced, not yet written
        _scheduler.Advance(1); // no Flush(): simulates the app being killed without an exit event
        Assert.Equal(186, Load(device)?.PageIndex);
        Assert.Equal(0.4f, Load(device)?.PageOffset);
    });

    [Fact]
    public void ContinuousScrollingIsSavedAtLeastEveryFewSeconds() => Run(async () =>
    {
        var device = new Device("laptop", null);
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        // A scroll report every 100ms for 3.5s never lets the debounce settle.
        for (var i = 1; i <= 35; i++)
        {
            c.OnScrolled(_phoneLayout, i, 0, 0);
            _scheduler.Advance(100);
        }
        var saved = Load(device);
        Assert.True(saved is not null && saved.PageIndex >= 20, "max-wait write happened mid-scroll");
    });

    [Fact]
    public void ResizeReappliesThePositionInsteadOfSavingAShiftedOne() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(186, 0.5f));
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        var wide = new LayoutKey(2400, 1);
        // The view re-measures under the new width before the controller hears about it.
        c.OnScrolled(wide, 186, 0.22f, 0);
        c.OnLayout(wide);
        Assert.Equal(0.5f, Target(c)?.PageOffset);
        c.ApplyRestore(wide);
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.Equal(0.5f, Load(device)?.PageOffset);
    });

    [Fact]
    public void ZoomChangeKeepsTheReadingPositionAndIsSaved() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(40, 0.3f));
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        c.SetZoom(2);
        var zoomed = new LayoutKey(1080, 2);
        c.OnLayout(zoomed);
        Assert.Equal(40, Target(c)?.PageIndex);
        Assert.Equal(0.3f, Target(c)?.PageOffset);
        c.ApplyRestore(zoomed);
        _scheduler.RunUntilIdle();
        Assert.Equal(2f, Load(device)?.Zoom);
        Assert.Equal(40, Load(device)?.PageIndex);
    });

    [Fact]
    public void StartOverSavesPageOneImmediately() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(186));
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        c.RestartFromBeginning();
        Assert.Equal(0, Load(device)?.PageIndex); // written synchronously, no timer needed
        Assert.True(Target(c)?.IsAtStart);
        Assert.Contains(RestoreEvent.UserRestart, _telemetry.Events);
    });

    [Fact]
    public void NewDeviceWaitsBrieflyForTheServerInsteadOfFlashingPageOne() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server);
        laptop.Repo.SaveLocal(Pos(186, 0.5f));
        await laptop.Sync.PushPendingAsync();

        var c = await Open(new Device("phone", server));
        Assert.Equal(186, Target(c)?.PageIndex);
        c.ApplyRestore(_phoneLayout);
        Assert.Contains(RestoreEvent.RestoredRemote, _telemetry.Events);
    });

    [Fact]
    public void NewDeviceStartsAtTheBeginningIfTheServerIsTooSlow() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server);
        laptop.Repo.SaveLocal(Pos(186));
        await laptop.Sync.PushPendingAsync();
        server.GetGate = new Gate();
        var phone = new Device("phone", server);
        var c = new ReaderController(
            DOC,
            () => Task.FromResult(new LoadedDocument("Book", new FakeDocument())),
            phone.Repo,
            phone.Sync,
            _telemetry,
            _scheduler);
        c.Start();
        while (!_scheduler.HasPending) await Task.Yield();
        _scheduler.Advance(1_499);
        Assert.Equal(Phase.LoadingPhase, c.State.Phase);
        _scheduler.Advance(1);
        await c.StartTask!;
        Assert.NotNull(c.State.Phase.AsRestoring);
        Assert.Null(Target(c));
        server.GetGate?.Open();
    });

    [Fact]
    public void SlowSyncAppliesANewerRemotePositionIfTheUserHasntMoved() => Run(async () =>
    {
        var server = new FakeServer();
        var phone = new Device("phone", server, wallClock: 1_000_000);
        phone.Repo.SaveLocal(Pos(20));
        var laptop = new Device("laptop", server, wallClock: 2_000_000);
        laptop.Repo.SaveLocal(Pos(186));
        await laptop.Sync.PushPendingAsync();
        var gate = new Gate();
        server.GetGate = gate;

        var c = await Open(phone);
        Assert.Equal(20, Target(c)?.PageIndex); // opens at local position, not page one
        c.ApplyRestore(_phoneLayout);

        gate.Open();
        await c.RemoteTask!;
        Assert.Equal(186, Target(c)?.PageIndex);
        c.ApplyRestore(_phoneLayout);
        Assert.Equal(new Notice.SyncedFromOtherDevice(186), c.State.Notice);
        Assert.Equal(186, Load(phone)?.PageIndex);
        Assert.Contains(RestoreEvent.RemoteAppliedLate, _telemetry.Events);
    });

    [Fact]
    public void SlowSyncNeverYanksAUserWhoHasStartedReadingItOffersInstead() => Run(async () =>
    {
        var server = new FakeServer();
        var phone = new Device("phone", server, wallClock: 1_000_000);
        phone.Repo.SaveLocal(Pos(20));
        var laptop = new Device("laptop", server, wallClock: 9_000_000);
        laptop.Repo.SaveLocal(Pos(186));
        await laptop.Sync.PushPendingAsync();
        var gate = new Gate();
        server.GetGate = gate;

        var c = await Open(phone);
        c.ApplyRestore(_phoneLayout);
        c.OnScrolled(_phoneLayout, 21, 0.1f, 0);

        gate.Open();
        await c.RemoteTask!;
        _scheduler.RunUntilIdle();
        Assert.Equal(Phase.ReadingPhase, c.State.Phase);
        var offer = Assert.IsType<Notice.OtherDeviceAvailable>(c.State.Notice);
        Assert.Equal(186, offer.Position.PageIndex);
        // This device's reading stays the latest.
        Assert.Equal(21, Load(phone)?.PageIndex);

        c.Jump(offer.Position);
        Assert.Equal(186, Target(c)?.PageIndex);
        _scheduler.RunUntilIdle();
        Assert.Equal(186, Load(phone)?.PageIndex);
    });

    [Fact]
    public void AnOlderServerPositionArrivingLateIsIgnored() => Run(async () =>
    {
        var server = new FakeServer();
        var laptop = new Device("laptop", server, wallClock: 1_000_000);
        laptop.Repo.SaveLocal(Pos(5));
        await laptop.Sync.PushPendingAsync();
        var phone = new Device("phone", server, wallClock: 5_000_000);
        phone.Repo.SaveLocal(Pos(300));
        var gate = new Gate();
        server.GetGate = gate;

        var c = await Open(phone);
        c.ApplyRestore(_phoneLayout);
        gate.Open();
        await c.RemoteTask!;
        _scheduler.RunUntilIdle();

        Assert.Equal(Phase.ReadingPhase, c.State.Phase);
        Assert.IsNotType<Notice.OtherDeviceAvailable>(c.State.Notice);
        Assert.Equal(300, Load(phone)?.PageIndex);
    });

    [Fact]
    public void LandingOnPageOneWhenProgressExistedIsReportedAsADefect() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(186));
        var c = await Open(device);
        var r = c.State.Phase.AsRestoring!;
        c.OnRestoreApplied(r.Token, _phoneLayout, 0, 0, 0, atEnd: false);
        Assert.Contains(RestoreEvent.UnexpectedReset, _telemetry.Events);
    });

    [Fact]
    public void EndOfDocumentClampIsNotADefectAndDoesntDriftTheSavedPosition() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(499, 0.6f));
        var c = await Open(device);
        var r = c.State.Phase.AsRestoring!;
        // The view can't put page 499 at the top; it stops on page 498.
        c.OnRestoreApplied(r.Token, _phoneLayout, 498, 0.7f, 0, atEnd: true);
        c.OnScrolled(_phoneLayout, 498, 0.7f, 0);
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.DoesNotContain(RestoreEvent.RestoreMismatch, _telemetry.Events);
        Assert.Equal(499, Load(device)?.PageIndex);
    });

    [Fact]
    public void SwitchingToFlowingTextKeepsThePlaceAndSavesNothingNew() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(66, 0.4f));
        var before = Load(device);
        var c = await Open(device);
        c.ApplyRestore(_phoneLayout);

        c.SetMode(ReadingMode.Text);
        var text = new LayoutKey(1080, 1, ReadingMode.Text);
        // The text view starts at the top before the position is re-applied to it.
        c.OnScrolled(text, 0, 0, 0);
        c.OnLayout(text);
        Assert.Equal(66, Target(c)?.PageIndex);
        Assert.Equal(0.4f, Target(c)?.PageOffset);
        c.ApplyRestore(text);
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.Equal(before, Load(device));

        // Reading in text mode is saved as usual, in the same page terms.
        c.OnScrolled(text, 67, 0.25f, 0);
        _scheduler.RunUntilIdle();
        Assert.Equal(67, Load(device)?.PageIndex);
        Assert.Equal(0.25f, Load(device)?.PageOffset);
    });

    [Fact]
    public void ChangingTextSizeReappliesThePosition() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(10, 0.5f));
        var c = await Open(device, mode: ReadingMode.Text);
        var small = new LayoutKey(1080, 1, ReadingMode.Text, 1);
        c.ApplyRestore(small);

        c.SetTextScale(1.5f);
        var large = new LayoutKey(1080, 1, ReadingMode.Text, 1.5f);
        c.OnScrolled(large, 10, 0.2f, 0); // shifted by the bigger text: ignored
        c.OnLayout(large);
        Assert.Equal(0.5f, Target(c)?.PageOffset);
        c.ApplyRestore(large);
        c.Flush();
        _scheduler.RunUntilIdle();
        Assert.Equal(0.5f, Load(device)?.PageOffset);
    });

    [Fact]
    public void TheProgressSliderJumpsToAPageAndSavesIt() => Run(async () =>
    {
        var device = new Device("laptop", null);
        var c = await Open(device);
        c.GoToPage(150); // ignored before the initial restore
        Assert.Null(Target(c));
        c.ApplyRestore(_phoneLayout);

        c.GoToPage(150);
        Assert.Equal(150, Target(c)?.PageIndex);
        c.ApplyRestore(_phoneLayout);
        _scheduler.RunUntilIdle();
        Assert.Equal(150, Load(device)?.PageIndex);
        Assert.Equal(150, c.State.PageIndex);
    });

    // Refresh while open (the app is activated again)

    [Fact]
    public void ComingBackAfterReadingOnAnotherDeviceJumpsThere() => Run(async () =>
    {
        var server = new FakeServer();
        var mac = new Device("mac", server, wallClock: 1_000_000);
        mac.Repo.SaveLocal(Pos(40));
        await mac.Sync.PushPendingAsync();
        var c = await Open(mac);
        c.ApplyRestore(_phoneLayout);
        await c.RemoteTask!;

        // Meanwhile, on the phone.
        var phone = new Device("phone", server, wallClock: 2_000_000);
        await phone.Sync.PullAsync(DOC);
        phone.Repo.SaveLocal(Pos(90, 0.3f));
        await phone.Sync.PushPendingAsync();

        c.RefreshFromServer();
        await c.RemoteTask!;
        Assert.Equal(90, Target(c)?.PageIndex);
        Assert.Equal(new Notice.SyncedFromOtherDevice(90), c.State.Notice);
        c.ApplyRestore(_phoneLayout);
        _scheduler.RunUntilIdle();
        Assert.Equal(90, Load(mac)?.PageIndex); // no re-save of the old place
        Assert.Equal("phone", Load(mac)?.Stamp.DeviceId);
    });

    [Fact]
    public void RefreshNeverRollsBackNewerReadingDoneHere() => Run(async () =>
    {
        var server = new FakeServer();
        var phone = new Device("phone", server, wallClock: 1_000_000);
        phone.Repo.SaveLocal(Pos(90));
        await phone.Sync.PushPendingAsync();
        var mac = new Device("mac", server, wallClock: 2_000_000);
        var c = await Open(mac);
        await c.RemoteTask!;
        c.ApplyRestore(_phoneLayout);

        // Read further here; the debounce hasn't fired when the app is reactivated.
        c.OnScrolled(_phoneLayout, 120, 0.1f, 0);
        c.RefreshFromServer();
        await c.RemoteTask!;
        Assert.Equal(Phase.ReadingPhase, c.State.Phase); // the server's 90 is older than 120 read here
        Assert.True(c.State.Notice == new Notice.Resumed(90) || c.State.Notice is null);
        Assert.Equal(120, Load(mac)?.PageIndex); // flushed before pulling
    });

    [Fact]
    public void ScrollingWhileTheRefreshIsOutGetsAnOfferNotAJump() => Run(async () =>
    {
        var server = new FakeServer();
        var mac = new Device("mac", server, wallClock: 1_000_000);
        mac.Repo.SaveLocal(Pos(40));
        await mac.Sync.PushPendingAsync();
        var c = await Open(mac);
        c.ApplyRestore(_phoneLayout);
        await c.RemoteTask!;
        var phone = new Device("phone", server, wallClock: 9_000_000);
        phone.Repo.SaveLocal(Pos(300));
        await phone.Sync.PushPendingAsync();

        var gate = new Gate();
        server.GetGate = gate;
        c.RefreshFromServer();
        c.OnScrolled(_phoneLayout, 41, 0, 0);
        gate.Open();
        await c.RemoteTask!;
        Assert.Equal(Phase.ReadingPhase, c.State.Phase);
        var offer = Assert.IsType<Notice.OtherDeviceAvailable>(c.State.Notice);
        Assert.Equal(300, offer.Position.PageIndex);
    });

    [Fact]
    public void RefreshDoesNothingWhenSignedOut() => Run(async () =>
    {
        var c = await Open(new Device("mac", null));
        c.ApplyRestore(_phoneLayout);
        c.RefreshFromServer();
        Assert.Equal(Phase.ReadingPhase, c.State.Phase);
        Assert.Null(c.State.Notice);
    });

    [Fact]
    public void ARestoreForAnOldTokenIsIgnored() => Run(async () =>
    {
        var device = new Device("laptop", null);
        device.Repo.SaveLocal(Pos(30, 0.2f));
        var c = await Open(device);
        var stale = c.State.Phase.AsRestoring!.Token;
        c.ApplyRestore(_phoneLayout);
        c.GoToPage(80);
        c.OnRestoreApplied(stale, _phoneLayout, 0, 0, 0, atEnd: false);
        Assert.Equal(80, Target(c)?.PageIndex); // still restoring the newer target
        Assert.DoesNotContain(RestoreEvent.UnexpectedReset, _telemetry.Events);
    });
}

public static class ReaderControllerTestExtensions
{
    /// <summary>What the UI does for a restore: land exactly on the target and report it.</summary>
    public static void ApplyRestore(this ReaderController c, LayoutKey layout)
    {
        var r = c.State.Phase.AsRestoring!;
        c.OnRestoreApplied(r.Token, layout, r.Target?.PageIndex ?? 0, r.Target?.PageOffset ?? 0, 0, atEnd: false);
    }
}
