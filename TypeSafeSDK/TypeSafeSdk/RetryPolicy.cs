namespace TypeSafeSdk;

/// <summary>Retry policy untuk error transient 429 dan 529.</summary>
public sealed record RetryPolicy(int MaxRetries = 2, TimeSpan? Delay = null, TimeSpan? MaxDelay = null)
{
    public TimeSpan EffectiveDelay => Delay ?? TimeSpan.FromMilliseconds(250);
    public TimeSpan EffectiveMaxDelay => MaxDelay ?? TimeSpan.FromSeconds(5);
}
