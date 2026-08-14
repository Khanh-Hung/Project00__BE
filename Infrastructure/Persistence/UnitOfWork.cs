using Application.Abstractions.Data;
using Domain.Common;
using Infrastructure.Persistence.Repositories;

namespace Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly ProjectDbContext _dbContext;
    private readonly Dictionary<Type, object> _repositories = new();

    public UnitOfWork(ProjectDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public IGenericRepository<TEntity> GetRepository<TEntity>() where TEntity : BaseEntity
    {
        var type = typeof(TEntity);
        if (!_repositories.ContainsKey(type))
        {
            var repositoryInstance = new GenericRepository<TEntity>(_dbContext);
            _repositories.Add(type, repositoryInstance);
        }
        return (IGenericRepository<TEntity>)_repositories[type];
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        return await _dbContext.SaveChangesAsync(ct);
    }
}
