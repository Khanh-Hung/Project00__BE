using System.Linq.Expressions;
using Domain.Common;

namespace Application.Abstractions.Data;

public interface IGenericRepository<TEntity> where TEntity : BaseEntity
{
    Task<TEntity?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> GetAllAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken ct = default);
    Task AddAsync(TEntity entity, CancellationToken ct = default);
    void Update(TEntity entity);
    void Delete(TEntity entity);
}
