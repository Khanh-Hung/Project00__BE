using System;
using Application.Abstractions.Time;

namespace Infrastructure.Services.Time;

/// <summary>
/// Production implementation of ISystemClock returning real UTC time.
/// </summary>
public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTime UtcDateTime => UtcNow.UtcDateTime;
}
