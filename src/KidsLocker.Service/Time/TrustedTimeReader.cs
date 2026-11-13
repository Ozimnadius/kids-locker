namespace KidsLocker.Service.Time;

/// <summary>Короткоживущие клиенты сначала читают часы службы, затем обращаются в интернет.</summary>
internal static class TrustedTimeReader
{
    public static async Task<DateTimeOffset?> GetUtcAsync(CancellationToken ct = default, bool startup = false)
    {
        var service = await TimePipe.ReadServiceAsync(ct);
        if (service.Utc is not null) return service.Utc;
        using var http = InternetTimeSource.CreateHttpClient();
        var source = new InternetTimeSource(http);
        if (!startup) return await source.GetUtcAsync(ct);
        var clock = new TrustedClock(source);
        await clock.InitializeAsync(ct);
        return clock.UtcNow;
    }
}
