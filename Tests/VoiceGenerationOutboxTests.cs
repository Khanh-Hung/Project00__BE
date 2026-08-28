using System.Text.Json;
using Application.Common;
using Application.DTOs;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

/// <summary>
/// 10 Invariant Tests for PR #15: Voice Generation & Audio Artifact Reliability Contract.
/// Invariants verified:
/// 1. VoiceContext -> ProviderRequest integrity
/// 2. Outbox Pending -> Processing -> Completed
/// 3. Provider abstraction replacement
/// 4. AudioArtifact persistence
/// 5. Same ContextHash -> No duplicate provider call (Application Idempotency)
/// 6. Concurrent same job -> DB uniqueness protects artifact
/// 7. Provider transient failure -> Retry with backoff
/// 8. Provider permanent failure -> Fast-fail
/// 9. Storage failure -> AudioArtifact is not committed
/// 10. Retry after storage failure -> Recovers cleanly without orphan artifact
/// </summary>
public class VoiceGenerationOutboxTests
{
    private static (DbContextOptions<CoreDbContext> options, ServiceProvider sp) CreateTestEnvironment(
        string dbName,
        IVoiceProvider? customProvider = null,
        IVoiceStorage? customStorage = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        services.AddSingleton(options);
        services.AddScoped<CoreDbContext>();
        services.AddScoped<IVoicePromptCompiler, VoicePromptCompiler>();
        services.AddScoped<IVisualPromptCompiler, VisualPromptCompiler>();
        services.AddScoped<IImageGenerationService, MockImageService>();
        services.AddScoped<IMemoryExtractionTrigger, MockMemoryTrigger>();

        if (customProvider != null)
        {
            services.AddScoped(_ => customProvider);
        }
        else
        {
            services.AddScoped<IVoiceProvider, MockVoiceProvider>();
        }

        if (customStorage != null)
        {
            services.AddScoped(_ => customStorage);
        }
        else
        {
            services.AddScoped<IVoiceStorage, FakeVoiceStorage>();
        }

        services.AddScoped<IVoiceGenerationService, VoiceGenerationService>();

        var sp = services.BuildServiceProvider();
        return (options, sp);
    }

    [Fact]
    public void Test1_VoiceContext_To_ProviderRequest_Integrity()
    {
        var compiler = new VoicePromptCompiler();
        var voiceProfile = new CharacterVoiceProfile("vi-VN-HoaiMyNeural", "vi-VN", "Female");

        var context = new VoiceContext(
            Voice: voiceProfile,
            Mood: CharacterMood.Affectionate,
            MoodIntensity: 85,
            AffectionScore: 90,
            RelationshipStage: "Lover",
            RawText: "💭 *(Anh ấy thật ấm áp)* *[smile] mỉm cười nhẹ* \"Cảm ơn bạn đã luôn ở bên mình.\""
        );

        var request = compiler.CompileVoiceRequest(context);

        Assert.Equal("Cảm ơn bạn đã luôn ở bên mình.", request.CleanedText);
        Assert.Equal("vi-VN-HoaiMyNeural", request.VoiceId);
        Assert.Equal("vi-VN", request.Language);
        Assert.NotNull(request.Expression);
        Assert.Equal("Affectionate", request.Expression.EmotionTag);
        Assert.Equal("Whisper", request.Expression.Volume);
        Assert.True(request.Expression.Rate < 1.0);
    }

    [Fact]
    public async Task Test2_Outbox_Pending_To_Processing_To_Completed()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, sp) = CreateTestEnvironment(dbName);

        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var voicePayload = new VoiceGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: userId,
            VoiceProfile: new CharacterVoiceProfile("vi-VN-HoaiMyNeural"),
            Mood: CharacterMood.Happy,
            MoodIntensity: 75,
            AffectionScore: 40,
            RelationshipStage: "Acquaintance",
            RawText: "Chào mừng bạn đến với vương quốc!",
            SessionId: sessionId
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(voicePayload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        var processedCount = await processor.ProcessDueMessagesAsync();
        Assert.Equal(1, processedCount);

        await using (var db = new CoreDbContext(options))
        {
            var committedMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(committedMsg);
            Assert.Equal(OutboxStatus.Completed, committedMsg.Status);
            Assert.NotNull(committedMsg.ProcessedAt);

            var artifact = await db.AudioArtifacts.FirstOrDefaultAsync(a => a.TurnId == turnId);
            Assert.NotNull(artifact);
            Assert.Equal("Chào mừng bạn đến với vương quốc!", artifact.CleanedText);
            Assert.StartsWith("/uploads/audio/", artifact.AudioUrl);
        }
    }

    [Fact]
    public async Task Test3_Provider_Abstraction_Replacement()
    {
        var dbName = Guid.NewGuid().ToString();
        int customProviderCallCount = 0;

        var customProvider = new MockVoiceProvider(async (req, ct) =>
        {
            customProviderCallCount++;
            return await Task.FromResult(new VoiceProviderResult(
                AudioBytes: new byte[] { 0x01, 0x02, 0x03 },
                ContentType: "audio/ogg",
                Duration: TimeSpan.FromSeconds(5)
            ));
        });

        var (options, sp) = CreateTestEnvironment(dbName, customProvider: customProvider);

        var turnId = Guid.NewGuid();
        var payload = new VoiceGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            VoiceProfile: new CharacterVoiceProfile("custom_voice_id"),
            Mood: CharacterMood.Excited,
            MoodIntensity: 90,
            AffectionScore: 50,
            RelationshipStage: "Friend",
            RawText: "Tuyệt vời quá đi!",
            SessionId: Guid.NewGuid()
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        await processor.ProcessDueMessagesAsync();

        Assert.Equal(1, customProviderCallCount);

        await using (var db = new CoreDbContext(options))
        {
            var artifact = await db.AudioArtifacts.FirstOrDefaultAsync(a => a.TurnId == turnId);
            Assert.NotNull(artifact);
            Assert.Equal("audio/ogg", artifact.AudioFormat);
            Assert.Equal(TimeSpan.FromSeconds(5), artifact.Duration);
        }
    }

    [Fact]
    public async Task Test4_AudioArtifact_Persistence()
    {
        var dbName = Guid.NewGuid().ToString();
        var (options, sp) = CreateTestEnvironment(dbName);

        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var payload = new VoiceGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: userId,
            VoiceProfile: new CharacterVoiceProfile("vi-VN-NamMinhNeural", "vi-VN", "Male"),
            Mood: CharacterMood.Curious,
            MoodIntensity: 60,
            AffectionScore: 20,
            RelationshipStage: "Stranger",
            RawText: "Ngươi từ đâu tới?",
            SessionId: sessionId
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        await processor.ProcessDueMessagesAsync();

        await using (var db = new CoreDbContext(options))
        {
            var artifact = await db.AudioArtifacts.FirstOrDefaultAsync(a => a.TurnId == turnId);
            Assert.NotNull(artifact);
            Assert.Equal(sessionId, artifact.SessionId);
            Assert.Equal(charId, artifact.CharacterId);
            Assert.Equal(turnId, artifact.TurnId);
            Assert.Equal(userId, artifact.UserId);
            Assert.Equal("vi-VN-NamMinhNeural", artifact.VoiceId);
            Assert.Equal("Ngươi từ đâu tới?", artifact.CleanedText);
            Assert.NotEmpty(artifact.ContextHash);
            Assert.NotEmpty(artifact.AudioUrl);
        }
    }

    [Fact]
    public async Task Test5_Same_ContextHash_No_Duplicate_Provider_Call()
    {
        var dbName = Guid.NewGuid().ToString();
        var provider = new MockVoiceProvider();
        var (options, sp) = CreateTestEnvironment(dbName, customProvider: provider);

        var turnId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var charId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var payload = new VoiceGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: charId,
            UserId: userId,
            VoiceProfile: new CharacterVoiceProfile("vi-VN-HoaiMyNeural"),
            Mood: CharacterMood.Happy,
            MoodIntensity: 70,
            AffectionScore: 30,
            RelationshipStage: "Acquaintance",
            RawText: "Chúc một ngày tốt lành!",
            SessionId: sessionId
        );

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        // Run 1: Normal Generation
        var outboxMsg1 = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg1);
            await db.SaveChangesAsync();
        }
        await processor.ProcessDueMessagesAsync();

        Assert.Equal(1, provider.CallCount);
        await using (var db = new CoreDbContext(options))
        {
            Assert.Equal(1, await db.AudioArtifacts.CountAsync());
        }

        // Run 2: Replay same payload (same ContextHash)
        var outboxMsg2 = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg2);
            await db.SaveChangesAsync();
        }
        await processor.ProcessDueMessagesAsync();

        // INVARIANT: Provider must NOT be called again, 0 duplicate artifacts!
        Assert.Equal(1, provider.CallCount);
        await using (var db = new CoreDbContext(options))
        {
            Assert.Equal(1, await db.AudioArtifacts.CountAsync());
            var msg2 = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg2.Id);
            Assert.NotNull(msg2);
            Assert.Equal(OutboxStatus.Completed, msg2.Status);
        }
    }

    [Fact]
    public async Task Test6_Concurrent_Same_Job_DB_Uniqueness_Protects_Artifact()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var db = new CoreDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();

            var artifact1 = new AudioArtifact(
                sessionId: Guid.NewGuid(),
                characterId: Guid.NewGuid(),
                turnId: Guid.NewGuid(),
                userId: Guid.NewGuid(),
                voiceId: "voice1",
                cleanedText: "Hello",
                contextHash: "unique_hash_123",
                audioUrl: "/uploads/audio/1.mp3"
            );
            await db.AudioArtifacts.AddAsync(artifact1);
            await db.SaveChangesAsync();
        }

        // Attempt concurrent duplicate insert with exact same ContextHash
        await using (var db = new CoreDbContext(options))
        {
            var artifact2 = new AudioArtifact(
                sessionId: Guid.NewGuid(),
                characterId: Guid.NewGuid(),
                turnId: Guid.NewGuid(),
                userId: Guid.NewGuid(),
                voiceId: "voice1",
                cleanedText: "Hello duplicate",
                contextHash: "unique_hash_123", // Collision
                audioUrl: "/uploads/audio/2.mp3"
            );
            await db.AudioArtifacts.AddAsync(artifact2);

            // INVARIANT: Relational database MUST reject duplicate ContextHash with DbUpdateException
            var ex = await Assert.ThrowsAsync<DbUpdateException>(async () => await db.SaveChangesAsync());
            Assert.NotNull(ex);
        }

        // Verify exactly 1 artifact exists in DB
        await using (var db = new CoreDbContext(options))
        {
            var count = await db.AudioArtifacts.CountAsync(a => a.ContextHash == "unique_hash_123");
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task Test7_Provider_Transient_Failure_Retries()
    {
        var dbName = Guid.NewGuid().ToString();
        int attempts = 0;

        var transientProvider = new MockVoiceProvider(async (req, ct) =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new VoiceTransientException("TTS service rate limit 429 - retry later");
            }
            return await Task.FromResult(new VoiceProviderResult(new byte[] { 0x01 }, "audio/mpeg"));
        });

        var (options, sp) = CreateTestEnvironment(dbName, customProvider: transientProvider);

        var payload = new VoiceGenerationOutboxPayload(
            TurnId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            VoiceProfile: new CharacterVoiceProfile("voice1"),
            Mood: CharacterMood.Neutral,
            MoodIntensity: 50,
            AffectionScore: 0,
            RelationshipStage: "Stranger",
            RawText: "Hello",
            SessionId: Guid.NewGuid()
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        // Attempt 1: Transient Error
        await processor.ProcessDueMessagesAsync();

        await using (var db = new CoreDbContext(options))
        {
            var msgAfterFail = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msgAfterFail);
            Assert.Equal(OutboxStatus.Pending, msgAfterFail.Status);
            Assert.Equal(1, msgAfterFail.RetryCount);
            Assert.NotNull(msgAfterFail.NextRetryAt);
        }

        // Attempt 2: Next retry succeeds
        var now = DateTime.UtcNow.AddMinutes(5);
        await processor.ProcessDueMessagesAsync(now);

        await using (var db = new CoreDbContext(options))
        {
            var msgAfterSuccess = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msgAfterSuccess);
            Assert.Equal(OutboxStatus.Completed, msgAfterSuccess.Status);
            Assert.Equal(1, await db.AudioArtifacts.CountAsync());
        }
    }

    [Fact]
    public async Task Test8_Provider_Permanent_Failure_FastFails()
    {
        var dbName = Guid.NewGuid().ToString();
        var nonTransientProvider = new MockVoiceProvider((req, ct) =>
            throw new VoiceNonTransientException("Invalid voice ID 'unknown_voice'"));

        var (options, sp) = CreateTestEnvironment(dbName, customProvider: nonTransientProvider);

        var payload = new VoiceGenerationOutboxPayload(
            TurnId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            VoiceProfile: new CharacterVoiceProfile("invalid_id"),
            Mood: CharacterMood.Neutral,
            MoodIntensity: 50,
            AffectionScore: 0,
            RelationshipStage: "Stranger",
            RawText: "Hello",
            SessionId: Guid.NewGuid()
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        await processor.ProcessDueMessagesAsync();

        await using (var db = new CoreDbContext(options))
        {
            var msg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg);
            Assert.Equal(OutboxStatus.Failed, msg.Status);
            Assert.Equal(0, await db.AudioArtifacts.CountAsync());
        }
    }

    [Fact]
    public async Task Test9_Storage_Failure_Prevents_Artifact_Commit()
    {
        var dbName = Guid.NewGuid().ToString();
        var failingStorage = new FailingVoiceStorage();

        var (options, sp) = CreateTestEnvironment(dbName, customStorage: failingStorage);

        var payload = new VoiceGenerationOutboxPayload(
            TurnId: Guid.NewGuid(),
            CharacterId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            VoiceProfile: new CharacterVoiceProfile("voice1"),
            Mood: CharacterMood.Happy,
            MoodIntensity: 50,
            AffectionScore: 0,
            RelationshipStage: "Stranger",
            RawText: "Hello",
            SessionId: Guid.NewGuid()
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        await processor.ProcessDueMessagesAsync();

        // INVARIANT: When storage fails, NO AudioArtifact should be committed in DB
        await using (var db = new CoreDbContext(options))
        {
            Assert.Equal(0, await db.AudioArtifacts.CountAsync());

            var msg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(msg);
            Assert.Equal(OutboxStatus.Pending, msg.Status); // Scheduled for retry
            Assert.Equal(1, msg.RetryCount);
        }
    }

    [Fact]
    public async Task Test10_Retry_After_Storage_Failure_Recovers_Without_Orphan()
    {
        var dbName = Guid.NewGuid().ToString();
        var flappyStorage = new FlappyVoiceStorage();

        var (options, sp) = CreateTestEnvironment(dbName, customStorage: flappyStorage);

        var turnId = Guid.NewGuid();
        var payload = new VoiceGenerationOutboxPayload(
            TurnId: turnId,
            CharacterId: Guid.NewGuid(),
            UserId: Guid.NewGuid(),
            VoiceProfile: new CharacterVoiceProfile("voice1"),
            Mood: CharacterMood.Happy,
            MoodIntensity: 50,
            AffectionScore: 0,
            RelationshipStage: "Stranger",
            RawText: "Hello world",
            SessionId: Guid.NewGuid()
        );

        var outboxMsg = new OutboxMessage(OutboxEventTypes.VoiceGeneration, JsonSerializer.Serialize(payload));
        await using (var db = new CoreDbContext(options))
        {
            await db.OutboxMessages.AddAsync(outboxMsg);
            await db.SaveChangesAsync();
        }

        var processor = new OutboxProcessorBackgroundService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxProcessorBackgroundService>.Instance
        );

        // Attempt 1: Storage throws exception
        await processor.ProcessDueMessagesAsync();
        await using (var db = new CoreDbContext(options))
        {
            Assert.Equal(0, await db.AudioArtifacts.CountAsync());
        }

        // Attempt 2: Storage recovers
        flappyStorage.ShouldFail = false;
        var now = DateTime.UtcNow.AddMinutes(5);
        await processor.ProcessDueMessagesAsync(now);

        // INVARIANT: Exactly 1 AudioArtifact exists, Outbox is Completed
        await using (var db = new CoreDbContext(options))
        {
            Assert.Equal(1, await db.AudioArtifacts.CountAsync());
            var artifact = await db.AudioArtifacts.FirstOrDefaultAsync(a => a.TurnId == turnId);
            Assert.NotNull(artifact);
            Assert.Equal("/uploads/audio/recovered.mp3", artifact.AudioUrl);

            var completedMsg = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == outboxMsg.Id);
            Assert.NotNull(completedMsg);
            Assert.Equal(OutboxStatus.Completed, completedMsg.Status);
        }
    }

    // --- Helper Test Fakes ---

    private sealed class FakeVoiceStorage : IVoiceStorage
    {
        public Task<string> SaveAudioAsync(byte[] audioBytes, string fileName, string contentType = "audio/mpeg", CancellationToken ct = default) =>
            Task.FromResult($"/uploads/audio/{fileName}");

        public Task<bool> DeleteAudioAsync(string audioUrl, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class FailingVoiceStorage : IVoiceStorage
    {
        public Task<string> SaveAudioAsync(byte[] audioBytes, string fileName, string contentType = "audio/mpeg", CancellationToken ct = default) =>
            throw new IOException("Disk quota exceeded / storage unavailable");

        public Task<bool> DeleteAudioAsync(string audioUrl, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class FlappyVoiceStorage : IVoiceStorage
    {
        public bool ShouldFail { get; set; } = true;

        public Task<string> SaveAudioAsync(byte[] audioBytes, string fileName, string contentType = "audio/mpeg", CancellationToken ct = default)
        {
            if (ShouldFail)
            {
                throw new IOException("Transient network partition in storage");
            }
            return Task.FromResult("/uploads/audio/recovered.mp3");
        }

        public Task<bool> DeleteAudioAsync(string audioUrl, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class MockImageService : IImageGenerationService
    {
        public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
            Task.FromResult("https://example.com/mock.jpg");

        public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
            Task.FromResult("https://example.com/mock.jpg");
    }

    private sealed class MockMemoryTrigger : IMemoryExtractionTrigger
    {
        public bool NotifyMessageSent(MemoryExtractionJob job) => true;
    }
}
