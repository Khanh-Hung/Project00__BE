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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Project.Tests;

public class SemanticMemoryRetrievalTests
{
    [Fact]
    public void CosineSimilarityCalculator_Calculates_Vectors_Accurately()
    {
        var v1 = new float[] { 1.0f, 0.0f, 0.0f };
        var v2 = new float[] { 1.0f, 0.0f, 0.0f };
        var v3 = new float[] { 0.0f, 1.0f, 0.0f };

        var simSame = CosineSimilarityCalculator.Calculate(v1, v2);
        var simOrthogonal = CosineSimilarityCalculator.Calculate(v1, v3);

        Assert.Equal(1.0f, simSame, 3);
        Assert.Equal(0.0f, simOrthogonal, 3);
    }

    [Fact]
    public async Task MemoryService_Ranks_Semantically_Related_Memories_Highest()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var uow = new UnitOfWork(context);

        var embeddingService = new EmbeddingService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<EmbeddingService>.Instance
        );

        var mem1Content = "Người dùng rất sợ sấm sét và thường thức trắng đêm khi trời mưa bão lớn";
        var mem1 = CharacterMemory.Create(charId, userId, mem1Content, MemoryType.Fact, importance: 3);
        mem1.SetEmbedding(await embeddingService.GenerateEmbeddingAsync(mem1Content));

        var mem2Content = "Người dùng thích ăn kem matcha và dạo phố ngắm hoàng hôn";
        var mem2 = CharacterMemory.Create(charId, userId, mem2Content, MemoryType.Preference, importance: 3);
        mem2.SetEmbedding(await embeddingService.GenerateEmbeddingAsync(mem2Content));

        await context.CharacterMemories.AddRangeAsync(mem1, mem2);
        await context.SaveChangesAsync();

        var validator = new MemoryCandidateValidator();
        var memoryService = new MemoryService(uow, validator, embeddingService, NullLogger<MemoryService>.Instance);

        // Query with semantic context about storm / thunder
        var relevantMemories = await memoryService.GetRelevantMemoriesAsync(
            userId: userId,
            characterId: charId,
            maxCount: 2,
            queryText: "Bên ngoài trời đang nổi sấm chớp đùng đùng, mưa gió bão bùng sợ quá..."
        );

        Assert.NotEmpty(relevantMemories);
        // The storm/thunder fear memory must be ranked first due to semantic vector similarity!
        Assert.Equal(mem1.Id, relevantMemories[0].Id);
    }

    [Fact]
    public async Task MemoryService_Generates_Embeddings_On_StoreCandidatesAsync()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var userId = Guid.NewGuid();
        var charId = Guid.NewGuid();

        await using var context = new ProjectDbContext(options);
        var uow = new UnitOfWork(context);

        var embeddingService = new EmbeddingService(
            new HttpClient(),
            new ConfigurationBuilder().Build(),
            NullLogger<EmbeddingService>.Instance
        );

        var validator = new MemoryCandidateValidator();
        var memoryService = new MemoryService(uow, validator, embeddingService, NullLogger<MemoryService>.Instance);

        var candidates = new List<MemoryCandidate>
        {
            new MemoryCandidate(
                content: "Người dùng có ước mơ trở thành một nhà văn viết tiểu thuyết giả tưởng",
                type: MemoryType.Fact,
                importance: 4,
                confidence: 0.95m
            )
        };

        var result = await memoryService.StoreCandidatesAsync(userId, charId, null, candidates);

        Assert.Equal(1, result.PersistedCount);

        var stored = await context.CharacterMemories.FirstOrDefaultAsync(m => m.UserId == userId);
        Assert.NotNull(stored);
        Assert.NotNull(stored.EmbeddingJson);

        var embedding = stored.GetEmbedding();
        Assert.NotNull(embedding);
        Assert.True(embedding.Length > 0);
    }
}
