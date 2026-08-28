namespace Domain.Enums;

/// <summary>
/// Lifecycle status of a character visual reference.
/// </summary>
public enum VisualReferenceStatus
{
    /// <summary>
    /// Active reference available for conditioning and resolution.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Newly registered reference pending quality/identity validation.
    /// </summary>
    PendingValidation = 1,

    /// <summary>
    /// Archived reference preserved for historical audit but excluded from active generation resolution.
    /// </summary>
    Archived = 2,

    /// <summary>
    /// Superseded or deprecated reference replaced by a newer canonical reference.
    /// </summary>
    Deprecated = 3
}
