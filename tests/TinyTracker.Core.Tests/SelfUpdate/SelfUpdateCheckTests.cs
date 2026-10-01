using Microsoft.Extensions.Time.Testing;
using TinyTracker.Core.Logging;
using TinyTracker.Core.Scheduling;
using TinyTracker.Core.SelfUpdate;
using Xunit;

namespace TinyTracker.Core.Tests.SelfUpdate;

// Tiny Tracker looks for its own update at start, with Check now, and once a day (spec §6.5).
public sealed class SelfUpdateCheckTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    // Long enough for a wrong look to show.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(200);
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private static readonly SelfRelease Release = new(new SelfVersion(0, 2, 0), Start.AddDays(-3));

    private readonly TempFolder _folder = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeReleases _releases = new();
    private readonly List<SelfRelease?> _offers = [];
    private readonly List<CheckTrigger> _triggers = [];
    private readonly FileLog _log;
    private readonly CheckScheduler _scheduler;
    private readonly SelfUpdateCheck _check;
    private volatile bool _checksFail;

    public SelfUpdateCheckTests()
    {
        _log = new FileLog(_folder.PathOf("app.log"), _time);
        _scheduler = new CheckScheduler(_time, TimeSpan.FromHours(6));
        // As the check runner does, each check ends at once; a failed one brings a retry.
        _scheduler.CheckDue += (_, ticket) =>
        {
            lock (_triggers) _triggers.Add(ticket.Trigger);
            _scheduler.Finished(ticket, succeeded: !_checksFail);
        };
        _check = new SelfUpdateCheck(_scheduler, _releases, release =>
        {
            lock (_offers) _offers.Add(release);
        }, _time, _log);
    }

    public async ValueTask DisposeAsync()
    {
        _check.Dispose();
        await _check.Stopped.WaitAsync(Wait);
        _scheduler.Dispose();
        _log.Close();
        _folder.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int Offers
    {
        get
        {
            lock (_offers) return _offers.Count;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting.");
            await Task.Delay(10, Ct);
        }
    }

    private async Task Started()
    {
        _time.Advance(CheckScheduler.StartupDelay);
        await Until(() => Offers == 1);
    }

    // The check came due with that trigger, and GitHub wasn't asked again.
    private async Task NoLookFor(CheckTrigger trigger)
    {
        await Task.Delay(Settle, Ct);
        lock (_triggers) Assert.Equal(trigger, _triggers[^1]);
        Assert.Equal(1, _releases.Calls);
    }

    [Fact]
    public async Task AtStart_ItLooks_AndOffersTheLatest()
    {
        await Started();
        lock (_offers) Assert.Equal([Release], _offers);
    }

    [Fact]
    public async Task NoReleaseYet_OffersNone()
    {
        _releases.Answer = _ => Task.FromResult<SelfRelease?>(null);
        _time.Advance(CheckScheduler.StartupDelay);
        await Until(() => Offers == 1);
        lock (_offers) Assert.Equal([null], _offers);
    }

    [Fact]
    public async Task CheckNow_Looks_EvenRightAfterAnAnswer()
    {
        await Started();
        _scheduler.CheckNow();
        await Until(() => Offers == 2);
    }

    // The unauthenticated API allows 60 calls an hour, so scheduled checks look once a day.
    [Fact]
    public async Task ScheduledChecks_LookOnceADay()
    {
        await Started();
        for (var i = 0; i < 3; i++) _time.Advance(TimeSpan.FromHours(6));
        await Task.Delay(Settle, Ct);
        Assert.Equal((1, 1), (_releases.Calls, Offers));
        _time.Advance(TimeSpan.FromHours(6));
        await Until(() => Offers == 2);
    }

    // Within a day of an answer, only the start and Check now look again (spec §6.5).
    [Fact]
    public async Task FlyoutOpened_DoesntLookAgain_WithinADay()
    {
        await Started();
        _time.Advance(CheckScheduler.StaleAfter + TimeSpan.FromMinutes(1));
        _scheduler.FlyoutOpened();
        await NoLookFor(CheckTrigger.FlyoutOpened);
    }

    // Offline past the next check, then back after a sleep.
    [Fact]
    public async Task Resumed_DoesntLookAgain_WithinADay()
    {
        await Started();
        _scheduler.SetConditions(online: false, batterySaver: false);
        _time.Advance(TimeSpan.FromHours(7));
        _scheduler.Resumed();
        _scheduler.SetConditions(online: true, batterySaver: false);
        _time.Advance(CheckScheduler.StartupDelay);
        await NoLookFor(CheckTrigger.Resumed);
    }

    [Fact]
    public async Task Retry_DoesntLookAgain_WithinADay()
    {
        await Started();
        _checksFail = true;
        _time.Advance(TimeSpan.FromHours(6));
        _time.Advance(CheckScheduler.RetryDelays[0]);
        await NoLookFor(CheckTrigger.Retry);
    }

    [Fact]
    public async Task NoAnswer_IsTriedAgainAtTheNextCheck_WithNoNotice()
    {
        _releases.Answer = _ => Task.FromException<SelfRelease?>(new HttpRequestException("offline"));
        _time.Advance(CheckScheduler.StartupDelay);
        await Until(() => _releases.Calls == 1);
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, Offers);
        _releases.Answer = _ => Task.FromResult<SelfRelease?>(Release);
        _time.Advance(TimeSpan.FromHours(6));
        await Until(() => Offers == 1);
    }

    [Fact]
    public async Task GitHubThatHangs_IsGivenUpOnAfterAMinute()
    {
        var asked = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releases.Answer = async ct =>
        {
            asked.TrySetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Release;
        };
        _time.Advance(CheckScheduler.StartupDelay);
        var token = await asked.Task.WaitAsync(Wait, Ct);
        _time.Advance(SelfUpdateCheck.Timeout);
        await Until(() => token.IsCancellationRequested);
        Assert.Equal(0, Offers);
    }

    [Fact]
    public async Task OneLookAtATime()
    {
        var answer = new TaskCompletionSource<SelfRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releases.Answer = _ => answer.Task;
        _time.Advance(CheckScheduler.StartupDelay);
        await Until(() => _releases.Calls == 1);
        _scheduler.CheckNow();
        await Task.Delay(Settle, Ct);
        Assert.Equal(1, _releases.Calls);
        answer.SetResult(Release);
        await Until(() => Offers == 1);
    }

    [Fact]
    public async Task AfterQuit_ItNeverLooks()
    {
        _check.Dispose();
        _scheduler.CheckNow();
        await Task.Delay(Settle, Ct);
        Assert.Equal(0, _releases.Calls);
    }

    private sealed class FakeReleases : ISelfReleases
    {
        private int _calls;

        public Func<CancellationToken, Task<SelfRelease?>> Answer { get; set; } = _ => Task.FromResult<SelfRelease?>(Release);

        public int Calls => Volatile.Read(ref _calls);

        public Task<SelfRelease?> LatestAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Answer(ct);
        }
    }
}
