namespace Domain.Common.DateTimes;

public static class Clock
{
    private static IDateTimeProvider _provider = new SystemDateTimeProvider();

    public static void SetProvider(IDateTimeProvider provider)
    {
        _provider = provider;
    }

    public static DateTime Now => _provider.UtcNow;
}
