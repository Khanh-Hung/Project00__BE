using Domain.Common;

namespace Application.Abstractions.Data;

public interface IUnitOfWork
{
    IGenericRepository<TEntity> GetRepository<TEntity>() where TEntity : BaseEntity;
    ICharacterMemoryRepository CharacterMemories { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
