using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PhotoReview.Localization.Generator;

/// <summary>One <c>"key": "text"</c> pair of a flat catalog, with the key's position in the file.</summary>
internal sealed class JsonEntry
{
    public JsonEntry(string key, string value, int keyStart, int keyLength)
    {
        Key = key;
        Value = value;
        KeyStart = keyStart;
        KeyLength = keyLength;
    }

    public string Key { get; }

    public string Value { get; }

    /// <summary>Offset of the key's opening quote.</summary>
    public int KeyStart { get; }

    /// <summary>Length of the key token including its quotes.</summary>
    public int KeyLength { get; }
}

/// <summary>Thrown (and caught by the generator) when the catalog is not valid JSON.</summary>
internal sealed class FlatJsonException : Exception
{
    public FlatJsonException()
    {
    }

    public FlatJsonException(string message)
        : base(message)
    {
    }

    public FlatJsonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FlatJsonException(string message, int position)
        : base(message)
    {
        Position = position;
    }

    public int Position { get; }
}

/// <summary>
/// Minimal reader for a flat JSON object of string values (the catalog format of ADR 0006).
/// Keys starting with <c>_</c> (e.g. <c>_meta</c>, comments) may hold any JSON value and are skipped.
/// Accepts <c>//</c> and <c>/* */</c> comments and trailing commas, like the runtime parser
/// (System.Text.Json with <c>CommentHandling.Skip</c>, <c>AllowTrailingCommas</c>).
/// </summary>
internal sealed class FlatJsonReader
{
    private const int MaxDepth = 64;

    private readonly string _text;
    private int _pos;

    private FlatJsonReader(string text)
    {
        _text = text;
    }

    /// <summary>Parses <paramref name="text"/>; throws <see cref="FlatJsonException"/> on invalid input.</summary>
    public static List<JsonEntry> Read(string text)
    {
        var reader = new FlatJsonReader(text);
        return reader.ReadRoot();
    }

    private List<JsonEntry> ReadRoot()
    {
        var entries = new List<JsonEntry>();
        if (_text.Length > 0 && _text[0] == '﻿') _pos = 1;
        SkipTrivia();
        Expect('{');
        SkipTrivia();
        if (TryConsume('}'))
        {
            EnsureEnd();
            return entries;
        }

        while (true)
        {
            SkipTrivia();
            if (TryConsume('}')) break; // trailing comma
            var keyStart = _pos;
            var key = ReadString();
            var keyLength = _pos - keyStart;
            SkipTrivia();
            Expect(':');
            SkipTrivia();
            if (key.Length > 0 && key[0] == '_')
            {
                SkipValue(0);
            }
            else
            {
                if (Peek() != '"') throw Error("value of '" + key + "' must be a string");
                entries.Add(new JsonEntry(key, ReadString(), keyStart, keyLength));
            }
            SkipTrivia();
            if (TryConsume(',')) continue;
            if (TryConsume('}')) break;
            throw Error("expected ',' or '}'");
        }

        EnsureEnd();
        return entries;
    }

    private void EnsureEnd()
    {
        SkipTrivia();
        if (_pos < _text.Length) throw Error("unexpected content after the root object");
    }

    private void SkipValue(int depth)
    {
        if (depth > MaxDepth) throw Error("nesting too deep");
        var c = Peek();
        switch (c)
        {
            case '"':
                ReadString();
                return;
            case '{':
                _pos++;
                SkipContainer('}', depth, isObject: true);
                return;
            case '[':
                _pos++;
                SkipContainer(']', depth, isObject: false);
                return;
            case 't':
                ExpectWord("true");
                return;
            case 'f':
                ExpectWord("false");
                return;
            case 'n':
                ExpectWord("null");
                return;
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    SkipNumber();
                    return;
                }
                throw Error("expected a JSON value");
        }
    }

    private void SkipContainer(char close, int depth, bool isObject)
    {
        SkipTrivia();
        if (TryConsume(close)) return;
        while (true)
        {
            SkipTrivia();
            if (TryConsume(close)) return; // trailing comma
            if (isObject)
            {
                ReadString();
                SkipTrivia();
                Expect(':');
                SkipTrivia();
            }
            SkipValue(depth + 1);
            SkipTrivia();
            if (TryConsume(',')) continue;
            if (TryConsume(close)) return;
            throw Error("expected ',' or '" + close + "'");
        }
    }

    private void SkipNumber()
    {
        var start = _pos;
        if (Peek() == '-') _pos++;
        var digits = 0;
        while (IsDigit(Peek()))
        {
            _pos++;
            digits++;
        }
        if (digits == 0) throw Error("invalid number", start);
        if (Peek() == '.')
        {
            _pos++;
            if (!IsDigit(Peek())) throw Error("invalid number", start);
            while (IsDigit(Peek())) _pos++;
        }
        if (Peek() == 'e' || Peek() == 'E')
        {
            _pos++;
            if (Peek() == '+' || Peek() == '-') _pos++;
            if (!IsDigit(Peek())) throw Error("invalid number", start);
            while (IsDigit(Peek())) _pos++;
        }
    }

    private void ExpectWord(string word)
    {
        if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0) throw Error("expected a JSON value");
        _pos += word.Length;
    }

    private string ReadString()
    {
        var start = _pos;
        Expect('"');
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length) throw Error("unterminated string", start);
            var c = _text[_pos++];
            if (c == '"') return sb.ToString();
            if (c < ' ') throw Error("control character in string (escape it)", _pos - 1);
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (_pos >= _text.Length) throw Error("unterminated string", start);
            var e = _text[_pos++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (_pos + 4 > _text.Length
                        || !int.TryParse(_text.Substring(_pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                    {
                        throw Error("invalid \\u escape", _pos - 2);
                    }
                    sb.Append((char)code);
                    _pos += 4;
                    break;
                default:
                    throw Error("invalid escape '\\" + e + "'", _pos - 2);
            }
        }
    }

    private void SkipTrivia()
    {
        while (_pos < _text.Length)
        {
            var c = _text[_pos];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
            {
                while (_pos < _text.Length && _text[_pos] != '\n') _pos++;
            }
            else if (c == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '*')
            {
                var end = _text.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                if (end < 0) throw Error("unterminated comment");
                _pos = end + 2;
            }
            else
            {
                return;
            }
        }
    }

    private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

    private bool TryConsume(char c)
    {
        if (Peek() != c) return false;
        _pos++;
        return true;
    }

    private void Expect(char c)
    {
        if (_pos >= _text.Length) throw Error("unexpected end of file, expected '" + c + "'");
        if (_text[_pos] != c) throw Error("expected '" + c + "'");
        _pos++;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private FlatJsonException Error(string message) => Error(message, _pos);

    private static FlatJsonException Error(string message, int position) => new(message, position);
}
