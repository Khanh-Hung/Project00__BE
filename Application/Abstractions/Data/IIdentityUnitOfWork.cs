using Domain.Common;
using Domain.Entities;

namespace Application.Abstractions.Data;

/// <summary>
/// Unit of Work for Identity & User authentication database operations.
/// </summary>
public interface IIdentityUnitOfWork
{
    IGenericRepository<TEntity> GetRepository<TEntity>() where TEntity : BaseEntity;
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
