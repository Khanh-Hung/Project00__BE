using System.Reflection;
using Application.Abstractions.Auth;
using Domain.Common;
using Domain.Common.DateTimes;
using Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Persistence;

public class CoreDbContext : DbContext
{
    private readonly ICurrentUserProvider? _currentUserProvider;

    public CoreDbContext(
        DbContextOptions<CoreDbContext> options,
        ICurrentUserProvider? currentUserProvider = null) : base(options)
    {
        _currentUserProvider = currentUserProvider;
    }

    public DbSet<Character> Characters { get; set; }
    public DbSet<ChatSession> ChatSessions { get; set; }
    public DbSet<ChatMessage> ChatMessages { get; set; }
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
    public DbSet<SceneSpecification> SceneSpecifications { get; set; }
    public DbSet<SceneVisualStateRecord> SceneVisualStates { get; set; }
    public DbSet<CharacterActivity> CharacterActivities { get; set; }
    public DbSet<CharacterGoal> CharacterGoals { get; set; }
    public DbSet<CharacterGoalMilestone> CharacterGoalMilestones { get; set; }
    public DbSet<GoalActivityContribution> GoalActivityContributions { get; set; }
    public DbSet<CharacterWorldEvent> CharacterWorldEvents { get; set; }
    public DbSet<CharacterWorldEventReaction> CharacterWorldEventReactions { get; set; }
    public DbSet<CharacterAutonomyTick> CharacterAutonomyTicks { get; set; }
    public DbSet<CharacterState> CharacterStates { get; set; }
    public DbSet<CharacterStateTransition> CharacterStateTransitions { get; set; }
    public DbSet<CharacterRelationshipTransition> CharacterRelationshipTransitions { get; set; }
    public DbSet<CharacterPersonality> CharacterPersonalities { get; set; }
    public DbSet<PersonalityAdaptationEvidence> CharacterPersonalityAdaptationEvidences { get; set; }
    public DbSet<CharacterPersonalityAdaptation> CharacterPersonalityAdaptations { get; set; }
    public DbSet<CharacterLifeActivity> CharacterLifeActivities { get; set; }

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
        modelBuilder.Ignore<User>(); // Managed exclusively by IdentityDbContext
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly(), type => type != typeof(Configurations.UserConfiguration));

        // Provider-aware index filter adjustment: PostgreSQL uses boolean literals ("IsCanonical" = true), SQLite uses ("IsCanonical" = 1)
        var isSqlite = Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (isSqlite)
        {
            modelBuilder.Entity<CharacterVisualReference>()
                .HasIndex(x => x.CharacterId)
                .HasFilter("\"IsCanonical\" = 1 AND \"Status\" = 'Active'")
                .IsUnique();
        }
    }

    /// <summary>
    /// Provider-aware initialization of SQLite overlap-prevention triggers on CharacterLifeActivities.
    /// In PostgreSQL, interval exclusion constraints (GiST) handle this natively at the database level.
    /// </summary>
    public void EnsureLifeSimulationTriggersCreated()
    {
        var isSqlite = Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
        if (!isSqlite) return;

        Database.ExecuteSqlRaw(@"
            CREATE TRIGGER IF NOT EXISTS check_activity_overlap_insert
            BEFORE INSERT ON CharacterLifeActivities
            FOR EACH ROW
            WHEN NEW.Status IN ('Scheduled', 'Active')
            BEGIN
                SELECT RAISE(ABORT, 'Schedule overlap conflict')
                WHERE EXISTS (
                    SELECT 1 FROM CharacterLifeActivities
                    WHERE CharacterId = NEW.CharacterId
                      AND Status IN ('Scheduled', 'Active')
                      AND StartAtUtc < NEW.PlannedEndAtUtc
                      AND PlannedEndAtUtc > NEW.StartAtUtc
                );
            END;

            CREATE TRIGGER IF NOT EXISTS check_activity_overlap_update
            BEFORE UPDATE OF StartAtUtc, PlannedEndAtUtc, Status ON CharacterLifeActivities
            FOR EACH ROW
            WHEN NEW.Status IN ('Scheduled', 'Active')
            BEGIN
                SELECT RAISE(ABORT, 'Schedule overlap conflict')
                WHERE EXISTS (
                    SELECT 1 FROM CharacterLifeActivities
                    WHERE CharacterId = NEW.CharacterId
                      AND Id != NEW.Id
                      AND Status IN ('Scheduled', 'Active')
                      AND StartAtUtc < NEW.PlannedEndAtUtc
                      AND PlannedEndAtUtc > NEW.StartAtUtc
                );
            END;
        ");
    }
}
