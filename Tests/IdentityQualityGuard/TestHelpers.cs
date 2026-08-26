using Application.DTOs;
using Application.Enums;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Tests.IdentityQualityGuard;

internal sealed class FakePromptCompiler : IVisualPromptCompiler
{
    private readonly string _pos;
    private readonly string _neg;
    public FakePromptCompiler(string pos, string neg) { _pos = pos; _neg = neg; }
    public string CompileAvatarPrompt(Character character) => _pos;
    public string CompileScenePrompt(Character character, SceneContext scene, CharacterRelationship? relationship = null, Slot2Context context = Slot2Context.SameScene) => _pos;
    public string CompileScenePrompt(VisualSnapshot snapshot) => _pos;
    public string CompileNegativePrompt(VisualSnapshot snapshot, string? customNegative = null) => _neg;
    public string CompileNegativePrompt(CharacterVisualIdentity? identity, string? customNegative = null) => _neg;
}

internal sealed class FakeImageService : IImageGenerationService
{
    public int CallCount { get; private set; } = 0;
    public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
        Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{++CallCount}.png");
    public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
        Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{++CallCount}.png");
    public Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        CallCount++;
        return Task.FromResult(new ImageGenerationResult(
            ImageUrl: $"https://cdn.project00.ai/images/gen_attempt_{CallCount}.png",
            Provider: "ComfyUI",
            ProviderJobId: $"job_{CallCount}",
            DurationMs: 1000,
            Seed: request.Seed ?? 100000L
        ));
    }
}

internal sealed class SequenceEvaluator : IIdentityQualityEvaluator
{
    private readonly IReadOnlyList<IdentityEvaluationResult> _results;
    public int CallCount { get; private set; } = 0;
    public SequenceEvaluator(IReadOnlyList<IdentityEvaluationResult> results) { _results = results; }
    public Task<IdentityEvaluationResult> EvaluateAsync(string imageLocation, VisualSnapshot snapshot, CancellationToken ct = default)
    {
        int idx = Math.Min(CallCount, _results.Count - 1);
        CallCount++;
        return Task.FromResult(_results[idx]);
    }
}

internal sealed class ConcurrentTrackingImageService : IImageGenerationService
{
    private int _callCount = 0;
    public int CallCount => _callCount;

    public Task<string> GenerateImageAsync(string prompt, int width = 512, int height = 512, CancellationToken ct = default) =>
        Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{Interlocked.Increment(ref _callCount)}.png");

    public Task<string> GenerateImageAsync(ImageGenerationRequest request, CancellationToken ct = default) =>
        Task.FromResult($"https://cdn.project00.ai/images/gen_attempt_{Interlocked.Increment(ref _callCount)}.png");

    public async Task<ImageGenerationResult> GenerateImageWithResultAsync(ImageGenerationRequest request, CancellationToken ct = default)
    {
        int current = Interlocked.Increment(ref _callCount);
        await Task.Delay(20, ct);
        return new ImageGenerationResult(
            ImageUrl: $"https://cdn.project00.ai/images/gen_attempt_{current}.png",
            Provider: "ComfyUI",
            ProviderJobId: $"job_{current}",
            DurationMs: 1000,
            Seed: request.Seed ?? 100000L
        );
    }
}

internal sealed class TestClipEvaluator : IIdentityQualityEvaluator
{
    public Task<IdentityEvaluationResult> EvaluateAsync(string imageLocation, VisualSnapshot snapshot, CancellationToken ct = default) =>
        Task.FromResult(IdentityEvaluationResult.Pass(0.88f, 0.92f, 0.90f));
}
