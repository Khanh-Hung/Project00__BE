using System.Threading.Channels;
using Application.Abstractions.Data;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Services;

public sealed class MemoryExtractionBackgroundService : BackgroundService, IMemoryExtractionTrigger
{
    private readonly Channel<MemoryExtractionJob> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MemoryExtractionOptions _options;
    private readonly ILogger<MemoryExtractionBackgroundService> _logger;

    public MemoryExtractionBackgroundService(
        IServiceScopeFactory scopeFactory,
        ILogger<MemoryExtractionBackgroundService> logger,
        IOptions<MemoryExtractionOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new MemoryExtractionOptions();

        var channelOptions = new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait
        };
        _queue = Channel.CreateBounded<MemoryExtractionJob>(channelOptions);
    }

    public bool NotifyMessageSent(MemoryExtractionJob job)
    {
        if (job == null || job.UserId == Guid.Empty || job.CharacterId == Guid.Empty)
        {
            return false;
        }

        // Trigger policy: Only trigger extraction when user message count reaches batch threshold (e.g. every N messages)
        if (job.UserMessageCount > 0 && job.UserMessageCount % _options.BatchSize != 0)
        {
            return false;
        }

        var written = _queue.Writer.TryWrite(job);
        if (!written)
        {
            _logger.LogWarning(
                "Memory extraction queue is full (capacity: {Capacity}). Extraction job for User {UserId} / Character {CharacterId} was not enqueued.",
                _options.QueueCapacity, job.UserId, job.CharacterId);
        }

        return written;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MemoryExtractionBackgroundService started with BatchSize={BatchSize}, Capacity={Capacity}",
            _options.BatchSize, _options.QueueCapacity);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _queue.Reader.ReadAsync(stoppingToken);
                await ProcessExtractionJobAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in memory extraction worker loop");
            }
        }

        _logger.LogInformation("MemoryExtractionBackgroundService stopped.");
    }

    private async Task ProcessExtractionJobAsync(MemoryExtractionJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var llmService = scope.ServiceProvider.GetRequiredService<ILLMService>();

        var characterRepo = unitOfWork.GetRepository<Character>();
        var character = await characterRepo.GetByIdAsync(job.CharacterId, ct);
        if (character == null)
        {
            _logger.LogWarning("Character {CharacterId} not found for extraction job", job.CharacterId);
            return;
        }

        if (job.RecentMessages == null || job.RecentMessages.Count < 2)
        {
            return;
        }

        try
        {
            var candidates = await llmService.ExtractMemoryCandidatesAsync(
                character,
                job.RecentMessages,
                ct);

            if (candidates.Count > 0)
            {
                var stored = await memoryService.StoreCandidatesAsync(
                    job.UserId,
                    job.CharacterId,
                    job.SessionId,
                    candidates,
                    ct);

                _logger.LogInformation(
                    "Extracted and stored {StoredCount} memories for User {UserId} and Character {CharacterName}",
                    stored, job.UserId, character.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Memory extraction failed for User {UserId} / Character {CharacterId}. Chat execution remains unaffected.",
                job.UserId, job.CharacterId);
        }
    }
}
