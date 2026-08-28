using Application.Abstractions.Data;
using Domain.Common;
using Infrastructure.Persistence.Repositories;

namespace Infrastructure.Persistence;

public class UnitOfWork : IUnitOfWork
{
    private readonly CoreDbContext _dbContext;
    private readonly Dictionary<Type, object> _repositories = new();

    public UnitOfWork(CoreDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private ICharacterMemoryRepository? _characterMemories;
    public ICharacterMemoryRepository CharacterMemories =>
        _characterMemories ??= new CharacterMemoryRepository(_dbContext);

    private ICharacterRelationshipRepository? _relationships;
    public ICharacterRelationshipRepository Relationships =>
        _relationships ??= new CharacterRelationshipRepository(_dbContext);

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
