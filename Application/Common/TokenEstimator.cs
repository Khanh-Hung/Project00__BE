namespace Application.Common;

/// <summary>
/// Lightweight, high-performance token estimator for mixed English and Vietnamese multilingual text.
/// Uses conservative heuristic (~3.2 characters per token) to prevent context window overflow without heavy tokenizer dependencies.
/// </summary>
public static class TokenEstimator
{
    public const double CharactersPerToken = 3.2;

    public static int Estimate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return (int)Math.Ceiling(text.Length / CharactersPerToken);
    }
}
