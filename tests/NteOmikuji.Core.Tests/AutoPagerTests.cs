using NteOmikuji.Core.Protocol;
using NteOmikuji.Core.Session;
using Xunit;

namespace NteOmikuji.Core.Tests;

public class AutoPagerTests
{
    private static readonly AutoPageTiming Fast = new(TimeSpan.FromMilliseconds(5), TimeSpan.Zero);

    /// <param name="answers">Whether each click brings a reply. Clicks past the end bring none.</param>
    private sealed class FakeGame(params bool[] answers) : IPageInput
    {
        public int Clicks { get; private set; }
        public int Replies { get; private set; }
        public AutoPageOutcome? BlockAfter { get; init; }
        public int BlockAtClick { get; init; } = int.MaxValue;
        public bool Refuse { get; init; }

        public AutoPageOutcome? Blocker() => Clicks >= BlockAtClick ? BlockAfter : null;

        public Task<bool> ClickNextAsync(CancellationToken token)
        {
            if (Refuse)
            {
                return Task.FromResult(false);
            }
            if (Clicks < answers.Length && answers[Clicks])
            {
                Replies++;
            }
            Clicks++;
            return Task.FromResult(true);
        }
    }

    private static Task<AutoPageOutcome> Run(FakeGame game, CancellationToken token = default) =>
        new AutoPager(game, () => game.Replies, Fast).RunAsync(token: token);

    [Fact]
    public async Task ThreeClicksWithoutAPageMeanTheEnd()
    {
        var game = new FakeGame(true, true);

        Assert.Equal(AutoPageOutcome.ReachedEnd, await Run(game));
        Assert.Equal(5, game.Clicks);
    }

    [Fact]
    public async Task AClickThatFetchesNothingDoesNotEndIt()
    {
        // The game fetches two pages at a time, so every other click brings no reply.
        var game = new FakeGame(true, false, true, false, true, false, true);

        Assert.Equal(AutoPageOutcome.ReachedEnd, await Run(game));
        Assert.Equal(10, game.Clicks);
    }

    [Theory]
    [InlineData(AutoPageOutcome.LostFocus)]
    [InlineData(AutoPageOutcome.Interrupted)]
    public async Task ABlockerStopsBeforeTheNextClick(AutoPageOutcome blocker)
    {
        var game = new FakeGame(true, true, true, true) { BlockAtClick = 2, BlockAfter = blocker };

        Assert.Equal(blocker, await Run(game));
        Assert.Equal(2, game.Clicks);
    }

    [Fact]
    public async Task ARefusedClickStopsAtOnce()
    {
        var game = new FakeGame(true) { Refuse = true };

        Assert.Equal(AutoPageOutcome.ClickRefused, await Run(game));
        Assert.Equal(0, game.Clicks);
    }

    [Fact]
    public async Task CancellingStopsWithoutClicking()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var game = new FakeGame(true);

        Assert.Equal(AutoPageOutcome.Cancelled, await Run(game, cancelled.Token));
        Assert.Equal(0, game.Clicks);
    }

    [Fact]
    public async Task TheClickCountHasALimit()
    {
        var game = new FakeGame(Enumerable.Repeat(true, AutoPager.MaxClicks + 50).ToArray());

        Assert.Equal(AutoPageOutcome.ReachedLimit, await Run(game));
        Assert.Equal(AutoPager.MaxClicks, game.Clicks);
    }

    [Fact]
    public void ARequestTellsWhichHistoryIsOpen()
    {
        var session = new HistorySession("10.0.0.2", RewardCatalog.Empty);
        Assert.Null(session.LastRequested);

        var request = new byte[45];
        BitConverter.TryWriteBytes(request.AsSpan(31), 4u);
        BitConverter.TryWriteBytes(request.AsSpan(35), 4220u);
        BitConverter.TryWriteBytes(request.AsSpan(40), 8u);
        session.Process(new IpPacket("10.0.0.2", "1.2.3.4", 50000, 30031, request, false));

        Assert.Equal(HistoryKind.LimitedCharacter, session.LastRequested);
        Assert.Equal(0, session.RepliesSeen(HistoryKind.LimitedCharacter));
    }
}
