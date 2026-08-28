using System.Linq.Expressions;
using Application.Abstractions.Data;
using Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence.Repositories;

public class IdentityGenericRepository<TEntity> : IGenericRepository<TEntity> where TEntity : BaseEntity
{
    protected readonly IdentityDbContext DbContext;

    public IdentityGenericRepository(IdentityDbContext dbContext)
    {
        DbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
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
        IQueryable<TEntity> query = DbContext.Set<TEntity>();
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
        var local = DbContext.Set<TEntity>().Local.FirstOrDefault(e => e.Id == entity.Id);
        if (local != null)
        {
            if (!ReferenceEquals(local, entity))
            {
                DbContext.Entry(local).CurrentValues.SetValues(entity);
            }
            if (DbContext.Entry(local).State != EntityState.Added)
            {
                DbContext.Entry(local).State = EntityState.Modified;
            }
            return;
        }

        var entry = DbContext.Entry(entity);
        if (entry.State == EntityState.Detached)
        {
            DbContext.Set<TEntity>().Attach(entity);
            entry.State = EntityState.Modified;
        }
        else if (entry.State != EntityState.Added)
        {
            entry.State = EntityState.Modified;
        }
    }

    public void Delete(TEntity entity)
    {
        DbContext.Set<TEntity>().Remove(entity);
    }
}
