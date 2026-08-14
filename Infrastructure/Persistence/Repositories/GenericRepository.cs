using System.Linq.Expressions;
using Application.Abstractions.Data;
using Domain.Common;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public class GenericRepository<TEntity> : IGenericRepository<TEntity> where TEntity : BaseEntity
{
    protected readonly ProjectDbContext DbContext;

    public GenericRepository(ProjectDbContext dbContext)
    {
        DbContext = dbContext;
    }

    public async Task<TEntity?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await DbContext.Set<TEntity>().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    public async Task<TEntity?> GetAsync(Expression<Func<TEntity, bool>> predicate, CancellationToken ct = default)
    {
        return await DbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, ct);
    }

    public async Task<IReadOnlyList<TEntity>> GetAllAsync(Expression<Func<TEntity, bool>>? predicate = null, CancellationToken ct = default)
    {
        IQueryable<TEntity> query = DbContext.Set<TEntity>().AsNoTracking();
        if (predicate != null)
        {
            query = query.Where(predicate);
        }
        return await query.OrderByDescending(e => e.CreatedAt).ToListAsync(ct);
    }

    public async Task AddAsync(TEntity entity, CancellationToken ct = default)
    {
        await DbContext.Set<TEntity>().AddAsync(entity, ct);
    }

    public void Update(TEntity entity)
    {
        DbContext.Set<TEntity>().Update(entity);
    }

    public void Delete(TEntity entity)
    {
        DbContext.Set<TEntity>().Remove(entity);
    }
}
