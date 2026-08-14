namespace Domain.Common;

public abstract class Entity
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; protected set; } = DateTime.UtcNow;
    public string? CreatedBy { get; protected set; }
    public DateTime? UpdatedAt { get; protected set; }
    public string? UpdatedBy { get; protected set; }
    public bool IsSoftDeleted { get; protected set; }
    public DateTime? DeletedAt { get; protected set; }
    public string? DeletedBy { get; protected set; }

    protected Entity(Guid? id = null)
    {
        if (id.HasValue && id.Value != Guid.Empty)
        {
            Id = id.Value;
        }
    }

    public void Touch()
    {
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetCreated(DateTime createdAt, string? createdBy = null)
    {
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }

    public void SetUpdated(DateTime updatedAt, string? updatedBy = null)
    {
        UpdatedAt = updatedAt;
        UpdatedBy = updatedBy;
    }

    public void SetDeleted(DateTime deletedAt, string? deletedBy = null)
    {
        IsSoftDeleted = true;
        DeletedAt = deletedAt;
        DeletedBy = deletedBy;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not Entity other) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id;
    }

    public override int GetHashCode() => Id.GetHashCode();
}

public abstract class BaseEntity : Entity
{
    protected BaseEntity(Guid? id = null) : base(id) { }
}
