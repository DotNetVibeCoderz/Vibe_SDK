namespace TypeSafeSdk;

/// <summary>
/// Kebijakan retry untuk kegagalan transien, sejajar dengan <c>RetryPolicy</c> pada Python SDK:
/// exponential backoff dengan jitter, daftar status HTTP yang boleh diulang, penghormatan header
/// <c>Retry-After</c>, dan budget waktu total per pemanggilan SDK.
/// </summary>
public sealed record RetryPolicy(
    int MaxRetries = 2,
    TimeSpan? BackoffInitial = null,
    TimeSpan? BackoffMax = null,
    double BackoffJitter = 0.25,
    IReadOnlySet<int>? HttpStatuses = null,
    bool RespectRetryAfter = true,
    bool RetryConnectionErrors = true,
    bool RetryTimeoutErrors = true,
    TimeSpan? Budget = null)
{
    private static readonly IReadOnlySet<int> DefaultStatuses = BuildDefaultStatuses();

    /// <summary>Jeda pertama sebelum percobaan ulang; digandakan tiap percobaan sampai <see cref="EffectiveBackoffMax"/>.</summary>
    public TimeSpan EffectiveBackoffInitial => BackoffInitial ?? TimeSpan.FromMilliseconds(500);
    /// <summary>Batas atas jeda backoff. Nol menonaktifkan backoff.</summary>
    public TimeSpan EffectiveBackoffMax => BackoffMax ?? TimeSpan.FromSeconds(5);
    /// <summary>Budget total per pemanggilan SDK termasuk percobaan pertama.</summary>
    public TimeSpan EffectiveBudget => Budget ?? TimeSpan.FromSeconds(30);
    /// <summary>Status HTTP yang dianggap transien: 408, 429, dan seluruh 5xx.</summary>
    public IReadOnlySet<int> EffectiveHttpStatuses => HttpStatuses ?? DefaultStatuses;

    /// <summary>Alias kompatibilitas untuk <see cref="EffectiveBackoffInitial"/>.</summary>
    public TimeSpan EffectiveDelay => EffectiveBackoffInitial;
    /// <summary>Alias kompatibilitas untuk <see cref="EffectiveBackoffMax"/>.</summary>
    public TimeSpan EffectiveMaxDelay => EffectiveBackoffMax;

    /// <summary>Menentukan apakah sebuah status HTTP layak diulang.</summary>
    public bool ShouldRetryStatus(int statusCode) => EffectiveHttpStatuses.Contains(statusCode);

    /// <summary>
    /// Menghitung jeda sebelum percobaan berikutnya. <paramref name="attempt"/> dimulai dari nol.
    /// Jitter mengurangi jeda secara acak agar klien tidak menyerbu server bersamaan.
    /// </summary>
    public TimeSpan NextDelay(int attempt, TimeSpan? retryAfter = null)
    {
        if (RespectRetryAfter && retryAfter is { } suggested && suggested > TimeSpan.Zero)
            return suggested > EffectiveBackoffMax ? EffectiveBackoffMax : suggested;
        if (EffectiveBackoffMax <= TimeSpan.Zero) return TimeSpan.Zero;
        var exponential = EffectiveBackoffInitial.TotalMilliseconds * Math.Pow(2, attempt);
        var capped = Math.Min(EffectiveBackoffMax.TotalMilliseconds, exponential);
        var jitter = Math.Clamp(BackoffJitter, 0, 1);
        var reduction = jitter <= 0 ? 0 : capped * jitter * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(Math.Max(0, capped - reduction));
    }

    private static HashSet<int> BuildDefaultStatuses()
    {
        var statuses = new HashSet<int> { 408, 429 };
        for (var status = 500; status <= 599; status++) statuses.Add(status);
        return statuses;
    }
}
