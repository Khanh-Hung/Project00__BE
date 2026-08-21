using System.Globalization;
using System.Text;

namespace Application.Common;

/// <summary>
/// Incremental parser that extracts and unescapes the 'reply' field from streaming JSON in real-time.
/// Features:
/// - Arbitrary JSON field ordering (e.g. 'thought', 'mood' preceding 'reply').
/// - Real-time escape decoding (\n, \r, \t, \", \\, \/, \b, \f).
/// - Full Unicode escape decoding (\uXXXX) and UTF-16 surrogate pairs (\uD83D\uDE0A -> 😊) buffered across arbitrary chunk boundaries.
/// - Zero leakage of raw JSON syntax ({, keys, quotes) into user-facing tokens.
/// - Character-by-character resilience (supports 1-byte chunks, split escapes, and empty chunks).
/// - Seamless fallback to raw text streaming if non-JSON plain text is produced.
/// </summary>
public sealed class IncrementalJsonReplyExtractor
{
    private enum ParserState
    {
        SeekingJsonOrFallback,
        ScanningObjectForReplyKey,
        SeekingReplyColonAndQuote,
        StreamingReplyValue,
        ReplyCompleted,
        StreamingRawFallback
    }

    private readonly StringBuilder _rawBuffer = new();
    private ParserState _state = ParserState.SeekingJsonOrFallback;

    // Buffer position tracking
    private int _readIndex = 0;

    // Object scanning state
    private bool _inString = false;
    private bool _isEscapedInScan = false;
    private readonly StringBuilder _currentKeyBuffer = new();

    // Reply value streaming escape state
    private bool _isEscapingValue = false;
    private bool _inUnicodeEscape = false;
    private readonly StringBuilder _unicodeHexBuffer = new(4);
    private char? _highSurrogate = null;

    public string GetFullRawAccumulatedText() => _rawBuffer.ToString();

    public IEnumerable<string> PushChunk(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk)) yield break;

        _rawBuffer.Append(chunk);
        var raw = _rawBuffer.ToString();

        while (_readIndex < raw.Length)
        {
            switch (_state)
            {
                case ParserState.SeekingJsonOrFallback:
                {
                    // Skip leading whitespace and markdown codeblock markers (e.g. ```json)
                    while (_readIndex < raw.Length)
                    {
                        char c = raw[_readIndex];
                        if (char.IsWhiteSpace(c))
                        {
                            _readIndex++;
                            continue;
                        }

                        // Check for ``` prefix
                        if (c == '`')
                        {
                            var fenceEnd = raw.IndexOf('\n', _readIndex);
                            if (fenceEnd != -1)
                            {
                                _readIndex = fenceEnd + 1;
                                continue;
                            }
                            // Incomplete code fence in chunk, wait for more data
                            yield break;
                        }

                        if (c == '{')
                        {
                            _readIndex++; // consume '{'
                            _state = ParserState.ScanningObjectForReplyKey;
                            _inString = false;
                            _isEscapedInScan = false;
                            _currentKeyBuffer.Clear();
                            break;
                        }

                        // Non-JSON plain text detected immediately
                        _state = ParserState.StreamingRawFallback;
                        _readIndex = 0; // stream from the beginning
                        break;
                    }

                    if (_state == ParserState.SeekingJsonOrFallback)
                    {
                        yield break;
                    }
                    break;
                }

                case ParserState.ScanningObjectForReplyKey:
                {
                    bool foundReplyKey = false;
                    while (_readIndex < raw.Length)
                    {
                        char c = raw[_readIndex++];

                        if (_inString)
                        {
                            if (_isEscapedInScan)
                            {
                                _isEscapedInScan = false;
                                _currentKeyBuffer.Append(c);
                            }
                            else if (c == '\\')
                            {
                                _isEscapedInScan = true;
                            }
                            else if (c == '"')
                            {
                                _inString = false;
                                if (_currentKeyBuffer.ToString().Equals("reply", StringComparison.OrdinalIgnoreCase))
                                {
                                    _state = ParserState.SeekingReplyColonAndQuote;
                                    foundReplyKey = true;
                                    _currentKeyBuffer.Clear();
                                    break;
                                }
                                _currentKeyBuffer.Clear();
                            }
                            else
                            {
                                _currentKeyBuffer.Append(c);
                            }
                        }
                        else
                        {
                            if (c == '"')
                            {
                                _inString = true;
                                _isEscapedInScan = false;
                                _currentKeyBuffer.Clear();
                            }
                            else if (c == '}')
                            {
                                // Object closed without finding a reply field
                                _state = ParserState.ReplyCompleted;
                                break;
                            }
                        }
                    }

                    if (!foundReplyKey && _state == ParserState.ScanningObjectForReplyKey)
                    {
                        yield break;
                    }
                    break;
                }

                case ParserState.SeekingReplyColonAndQuote:
                {
                    bool foundQuote = false;
                    while (_readIndex < raw.Length)
                    {
                        char c = raw[_readIndex++];
                        if (char.IsWhiteSpace(c) || c == ':') continue;
                        if (c == '"')
                        {
                            _state = ParserState.StreamingReplyValue;
                            _isEscapingValue = false;
                            _inUnicodeEscape = false;
                            _unicodeHexBuffer.Clear();
                            _highSurrogate = null;
                            foundQuote = true;
                            break;
                        }

                        // Malformed structure after reply key
                        _state = ParserState.ReplyCompleted;
                        break;
                    }

                    if (!foundQuote && _state == ParserState.SeekingReplyColonAndQuote)
                    {
                        yield break;
                    }
                    break;
                }

                case ParserState.StreamingReplyValue:
                {
                    var tokenBuilder = new StringBuilder();

                    while (_readIndex < raw.Length)
                    {
                        char c = raw[_readIndex++];

                        if (_inUnicodeEscape)
                        {
                            _unicodeHexBuffer.Append(c);
                            if (_unicodeHexBuffer.Length == 4)
                            {
                                var hexStr = _unicodeHexBuffer.ToString();
                                _unicodeHexBuffer.Clear();
                                _inUnicodeEscape = false;
                                _isEscapingValue = false;

                                if (int.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
                                {
                                    // Check for UTF-16 surrogate pairs
                                    if (codePoint >= 0xD800 && codePoint <= 0xDBFF)
                                    {
                                        // High surrogate, buffer and wait for low surrogate
                                        if (_highSurrogate.HasValue)
                                        {
                                            tokenBuilder.Append(_highSurrogate.Value);
                                        }
                                        _highSurrogate = (char)codePoint;
                                    }
                                    else if (codePoint >= 0xDC00 && codePoint <= 0xDFFF)
                                    {
                                        // Low surrogate
                                        if (_highSurrogate.HasValue)
                                        {
                                            tokenBuilder.Append(_highSurrogate.Value);
                                            tokenBuilder.Append((char)codePoint);
                                            _highSurrogate = null;
                                        }
                                        else
                                        {
                                            tokenBuilder.Append((char)codePoint);
                                        }
                                    }
                                    else
                                    {
                                        if (_highSurrogate.HasValue)
                                        {
                                            tokenBuilder.Append(_highSurrogate.Value);
                                            _highSurrogate = null;
                                        }
                                        tokenBuilder.Append((char)codePoint);
                                    }
                                }
                                else
                                {
                                    // Invalid hex fallback
                                    tokenBuilder.Append("\\u").Append(hexStr);
                                }
                            }
                            continue;
                        }

                        if (_isEscapingValue)
                        {
                            switch (c)
                            {
                                case 'n': tokenBuilder.Append('\n'); _isEscapingValue = false; break;
                                case 'r': tokenBuilder.Append('\r'); _isEscapingValue = false; break;
                                case 't': tokenBuilder.Append('\t'); _isEscapingValue = false; break;
                                case '"': tokenBuilder.Append('"'); _isEscapingValue = false; break;
                                case '\\': tokenBuilder.Append('\\'); _isEscapingValue = false; break;
                                case '/': tokenBuilder.Append('/'); _isEscapingValue = false; break;
                                case 'b': tokenBuilder.Append('\b'); _isEscapingValue = false; break;
                                case 'f': tokenBuilder.Append('\f'); _isEscapingValue = false; break;
                                case 'u':
                                    _inUnicodeEscape = true;
                                    _unicodeHexBuffer.Clear();
                                    break;
                                default:
                                    tokenBuilder.Append(c);
                                    _isEscapingValue = false;
                                    break;
                            }
                            continue;
                        }

                        if (c == '\\')
                        {
                            _isEscapingValue = true;
                            continue;
                        }

                        if (c == '"')
                        {
                            if (_highSurrogate.HasValue)
                            {
                                tokenBuilder.Append(_highSurrogate.Value);
                                _highSurrogate = null;
                            }
                            _state = ParserState.ReplyCompleted;
                            break;
                        }

                        if (_highSurrogate.HasValue)
                        {
                            tokenBuilder.Append(_highSurrogate.Value);
                            _highSurrogate = null;
                        }
                        tokenBuilder.Append(c);
                    }

                    if (tokenBuilder.Length > 0)
                    {
                        yield return tokenBuilder.ToString();
                    }
                    break;
                }

                case ParserState.ReplyCompleted:
                {
                    // Ingest all subsequent characters into _rawBuffer for metadata parsing, but yield no further tokens
                    _readIndex = raw.Length;
                    yield break;
                }

                case ParserState.StreamingRawFallback:
                {
                    var tail = raw.Substring(_readIndex);
                    _readIndex = raw.Length;
                    if (!string.IsNullOrEmpty(tail))
                    {
                        yield return tail;
                    }
                    yield break;
                }
            }
        }
    }
}
