using System.Reflection;
using Application.Abstractions.Auth;
using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public class ProjectDbContext : DbContext
{
    private readonly ICurrentUserProvider? _currentUserProvider;

    public ProjectDbContext(
        DbContextOptions<ProjectDbContext> options,
        ICurrentUserProvider? currentUserProvider = null) : base(options)
    {
        _currentUserProvider = currentUserProvider;
    }

    public DbSet<Character> Characters { get; set; }
    public DbSet<ChatSession> ChatSessions { get; set; }
    public DbSet<ChatMessage> ChatMessages { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<CharacterMemory> CharacterMemories { get; set; }
    public DbSet<CharacterRelationship> CharacterRelationships { get; set; }
    public DbSet<CharacterTurn> CharacterTurns { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<LorebookEntry> LorebookEntries { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<SceneImage> SceneImages { get; set; }
    public DbSet<AudioArtifact> AudioArtifacts { get; set; }
    public DbSet<ImageGenerationJob> ImageGenerationJobs { get; set; }
    public DbSet<ImageGenerationAttempt> ImageGenerationAttempts { get; set; }
    public DbSet<VisualSessionState> VisualSessionStates { get; set; }
    public DbSet<CharacterVisualProfile> CharacterVisualProfiles { get; set; }
    public DbSet<CharacterVisualReference> CharacterVisualReferences { get; set; }
    public DbSet<CharacterVisualMemory> CharacterVisualMemories { get; set; }

    private string NormalizeUserId()
    {
        var currentUserId = _currentUserProvider?.CurrentUserId;
        if (Guid.TryParse(currentUserId, out var guid)) return guid.ToString();
        return "system";
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = ChangeTracker.Entries<Entity>();
        var userId = NormalizeUserId();

        foreach (var entry in entries)
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.Id == Guid.Empty)
                    {
                        entry.Entity.Id = Guid.CreateVersion7();
                    }
                    if (entry.Entity.CreatedAt == default)
                    {
                        entry.Entity.SetCreated(Clock.Now, userId);
                    }
                    break;
                case EntityState.Modified:
                    if (!entry.Property(nameof(Entity.UpdatedAt)).IsModified &&
                        !entry.Property(nameof(Entity.UpdatedBy)).IsModified)
                    {
                        entry.Entity.SetUpdated(Clock.Now, userId);
                    }
                    break;
                case EntityState.Deleted:
                    entry.State = EntityState.Modified;
                    entry.Entity.SetDeleted(Clock.Now, userId);
                    break;
            }
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Enum>()
            .HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
