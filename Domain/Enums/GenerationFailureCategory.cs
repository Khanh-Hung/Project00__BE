namespace Domain.Enums;

/// <summary>
/// Domain-neutral categorization of failure causes for image generation jobs and attempts.
/// Distinguishes between retryable transient infrastructure issues, permanent configuration/input defects, and operational signals.
/// </summary>
public enum GenerationFailureCategory
{
    None = 0,

    // --- Retryable / Transient Infrastructure Failures ---
    ProviderTimeout = 1,
    ProviderUnavailable = 2,
    ProviderRateLimited = 3,
    TransientNetwork = 4,
    GpuFailure = 5,
    DatabaseTransient = 6,

    // --- Permanent / Non-Retryable Defects ---
    InvalidWorkflow = 10,
    InvalidInput = 11,
    ConfigurationError = 12,
    UnsupportedModel = 13,
    MalformedProviderResponse = 14,

    // --- Operational Lifecycle Signals ---
    Cancellation = 20,
    LeaseLost = 21,
    DuplicateExecution = 22,
    AlreadyCompleted = 23,

    // --- Fallback ---
    Unknown = 99
}
