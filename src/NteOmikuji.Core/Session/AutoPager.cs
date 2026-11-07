namespace NteOmikuji.Core.Session;

public enum AutoPageOutcome
{
    ReachedEnd,
    LostFocus,
    Interrupted,
    ClickRefused,
    ReachedLimit,
    Cancelled,
}

/// <summary>What turns a page in the game, kept apart so the paging rules can be tested without a game.</summary>
public interface IPageInput
{
    /// <summary>Why the next click must not happen, or null when it is safe.</summary>
    AutoPageOutcome? Blocker();

    /// <summary>False when Windows refused to deliver the click.</summary>
    Task<bool> ClickNextAsync(CancellationToken token);
}

/// <param name="ReplyWait">How long a click may wait for its page to arrive.</param>
/// <param name="Settle">The pause after a page arrives, so the game's screen is ready for the next click.</param>
public sealed record AutoPageTiming(TimeSpan ReplyWait, TimeSpan Settle)
{
    public static readonly AutoPageTiming Default = new(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(200));
}

/// <summary>
/// Clicks through the history until no more pages arrive. The game fetches more than one page at a time, so one
/// click without a reply is normal, and only several in a row mean the end.
/// </summary>
public sealed class AutoPager(IPageInput input, Func<int> replies, AutoPageTiming? timing = null)
{
    public const int MaxClicks = 1000;
    private const int ClicksWithoutReplyAtEnd = 3;

    private readonly AutoPageTiming _timing = timing ?? AutoPageTiming.Default;

    /// <param name="progress">Called with the number of clicks after each one.</param>
    public async Task<AutoPageOutcome> RunAsync(Action<int>? progress = null, CancellationToken token = default)
    {
        try
        {
            var unanswered = 0;
            for (var clicks = 1; clicks <= MaxClicks; clicks++)
            {
                token.ThrowIfCancellationRequested();
                if (input.Blocker() is { } blocker)
                {
                    return blocker;
                }
                var before = replies();
                if (!await input.ClickNextAsync(token))
                {
                    return AutoPageOutcome.ClickRefused;
                }
                progress?.Invoke(clicks);
                if (await WaitForReplyAsync(before, token))
                {
                    unanswered = 0;
                    await Task.Delay(_timing.Settle, token);
                }
                else if (++unanswered >= ClicksWithoutReplyAtEnd)
                {
                    return AutoPageOutcome.ReachedEnd;
                }
            }
            return AutoPageOutcome.ReachedLimit;
        }
        catch (OperationCanceledException)
        {
            return AutoPageOutcome.Cancelled;
        }
    }

    private async Task<bool> WaitForReplyAsync(int before, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + _timing.ReplyWait;
        while (replies() == before)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(15), token);
        }
        return true;
    }
}
