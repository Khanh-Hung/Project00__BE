using System;
using Application.Abstractions.Time;
using Domain.Common.DateTimes;

namespace Infrastructure.Services.Time;

/// <summary>
/// Production implementation of ISystemClock returning real UTC time.
/// Also implements IDateTimeProvider to unify wall-clock abstractions across the solution.
/// </summary>
public sealed class SystemClock : ISystemClock, IDateTimeProvider
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTime UtcDateTime => UtcNow.UtcDateTime;
    DateTime IDateTimeProvider.UtcNow => UtcDateTime;
}
