using Application.DTOs;
using Application.Interfaces;
using Domain.ValueObjects;
using Microsoft.Extensions.Options;

namespace Application.Services;

public sealed class MemoryCandidateValidator : IMemoryCandidateValidator
{
    private readonly MemoryExtractionOptions _options;

    public MemoryCandidateValidator(IOptions<MemoryExtractionOptions>? options = null)
    {
        _options = options?.Value ?? new MemoryExtractionOptions();
    }

    public bool Validate(MemoryCandidate candidate, out string? failureReason)
    {
        if (candidate == null)
        {
            failureReason = "Memory candidate cannot be null.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(candidate.Content))
        {
            failureReason = "Memory candidate content cannot be empty or whitespace.";
            return false;
        }

        var trimmed = candidate.Content.Trim();
        if (trimmed.Length < 1 || trimmed.Length > MemoryCandidate.MaxContentLength)
        {
            failureReason = $"Memory candidate content length must be between 1 and {MemoryCandidate.MaxContentLength} characters.";
            return false;
        }

        if (candidate.Importance is < 1 or > 5)
        {
            failureReason = "Memory candidate importance must be between 1 and 5.";
            return false;
        }

        if (candidate.Confidence is < 0.0m or > 1.0m)
        {
            failureReason = "Memory candidate confidence must be between 0.0 and 1.0.";
            return false;
        }

        // Flexible Confidence Policy
        // 1. Below 0.50 is always rejected as weak/unreliable signal
        if (candidate.Confidence < 0.50m)
        {
            failureReason = $"Confidence ({candidate.Confidence:P0}) is below minimum acceptable threshold (50%).";
            return false;
        }

        // 2. Between 0.50 and configured MinConfidence (e.g. 0.60), require strong Importance (>= 3)
        if (candidate.Confidence < _options.MinConfidence && candidate.Importance < 3)
        {
            failureReason = $"Confidence ({candidate.Confidence:P0}) is below {_options.MinConfidence:P0} and importance ({candidate.Importance}) is insufficient.";
            return false;
        }

        failureReason = null;
        return true;
    }
}
