using System.Text;

namespace Application.Common;

/// <summary>
/// Incremental parser that extracts and unescapes the 'reply' field from streaming JSON
/// in real-time, preventing raw JSON syntax from leaking into the user-facing SSE token stream.
/// Seamlessly falls back to raw streaming if the LLM produces non-JSON plain text.
/// </summary>
public sealed class IncrementalJsonReplyExtractor
{
    private enum ParserState
    {
        SearchingReplyKey,
        SearchingReplyQuote,
        StreamingReplyValue,
        ReplyCompleted,
        StreamingRawFallback
    }

    private readonly StringBuilder _rawBuffer = new();
    private ParserState _state = ParserState.SearchingReplyKey;
    private bool _isEscaped = false;
    private int _processedIndex = 0;

    public string GetFullRawAccumulatedText() => _rawBuffer.ToString();

    public IEnumerable<string> PushChunk(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) yield break;

        _rawBuffer.Append(chunk);
        var currentRaw = _rawBuffer.ToString();

        while (_processedIndex < currentRaw.Length)
        {
            switch (_state)
            {
                case ParserState.SearchingReplyKey:
                    // Check if non-JSON output early on
                    if (_processedIndex > 80 && !currentRaw.Contains("\"reply\""))
                    {
                        _state = ParserState.StreamingRawFallback;
                        yield return currentRaw.Substring(_processedIndex);
                        _processedIndex = currentRaw.Length;
                        yield break;
                    }

                    var keyIndex = currentRaw.IndexOf("\"reply\"", _processedIndex, StringComparison.OrdinalIgnoreCase);
                    if (keyIndex != -1)
                    {
                        _processedIndex = keyIndex + 7; // length of "reply"
                        _state = ParserState.SearchingReplyQuote;
                    }
                    else
                    {
                        _processedIndex = Math.Max(0, currentRaw.Length - 10);
                        yield break;
                    }
                    break;

                case ParserState.SearchingReplyQuote:
                    var foundQuote = false;
                    while (_processedIndex < currentRaw.Length)
                    {
                        char c = currentRaw[_processedIndex++];
                        if (c == ':') continue;
                        if (char.IsWhiteSpace(c)) continue;
                        if (c == '"')
                        {
                            _state = ParserState.StreamingReplyValue;
                            foundQuote = true;
                            break;
                        }
                    }

                    if (!foundQuote)
                    {
                        yield break;
                    }
                    break;

                case ParserState.StreamingReplyValue:
                    var tokenChunkBuilder = new StringBuilder();
                    while (_processedIndex < currentRaw.Length)
                    {
                        char c = currentRaw[_processedIndex++];
                        if (_isEscaped)
                        {
                            switch (c)
                            {
                                case 'n': tokenChunkBuilder.Append('\n'); break;
                                case 'r': tokenChunkBuilder.Append('\r'); break;
                                case 't': tokenChunkBuilder.Append('\t'); break;
                                case '"': tokenChunkBuilder.Append('"'); break;
                                case '\\': tokenChunkBuilder.Append('\\'); break;
                                default: tokenChunkBuilder.Append(c); break;
                            }
                            _isEscaped = false;
                        }
                        else if (c == '\\')
                        {
                            _isEscaped = true;
                        }
                        else if (c == '"')
                        {
                            _state = ParserState.ReplyCompleted;
                            break;
                        }
                        else
                        {
                            tokenChunkBuilder.Append(c);
                        }
                    }

                    if (tokenChunkBuilder.Length > 0)
                    {
                        yield return tokenChunkBuilder.ToString();
                    }
                    break;

                case ParserState.ReplyCompleted:
                    _processedIndex = currentRaw.Length;
                    yield break;

                case ParserState.StreamingRawFallback:
                    var rawTail = currentRaw.Substring(_processedIndex);
                    _processedIndex = currentRaw.Length;
                    yield return rawTail;
                    yield break;
            }
        }
    }
}
