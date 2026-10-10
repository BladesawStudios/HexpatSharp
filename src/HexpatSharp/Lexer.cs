using System.Globalization;
using System.Text;

namespace HexpatSharp;

public enum TokenKind { Ident, Integer, Float, String, Char, Punct, Directive, End }

public sealed class Token
{
    public TokenKind Kind;
    public string Text = "";
    public int Line;
    public int Column;
    /// <summary>Offset of the token in the source, used to tell whether two tokens touch (for <c>&gt;&gt;</c> and <c>&gt;=</c>).</summary>
    public int Position;
    public Int128 IntValue;
    public bool Unsigned;
    public double FloatValue;
    public string StringValue = "";

    public bool Is(string text) => (Kind == TokenKind.Punct || Kind == TokenKind.Ident) && Text == text;

    public override string ToString() => $"{Kind} '{Text}' ({Line}:{Column})";
}

/// <summary>An error in the pattern's source or while it runs, with the place it happened.</summary>
public sealed class HexpatException(string message, int line = 0, int column = 0) : Exception(line > 0 ? (column > 0 ? $"{line}:{column}: {message}" : $"{line}: {message}") : message)
{
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string PlainMessage { get; } = message;
}

/// <summary>Turns pattern source into tokens. Preprocessor lines become single <see cref="TokenKind.Directive"/> tokens.</summary>
public sealed class Lexer(string source)
{
    private static readonly string[] Operators =
    [
        "<<=", "...", "::", "&&", "||", "^^", "!=", "==", "<=", "<<", "+=", "-=", "*=", "/=", "%=", "|=", "&=", "^=", "->", "++", "--",
    ];

    private int _pos;
    private int _line = 1;
    private int _lineStart;
    private bool _atLineStart = true;

    private char Peek(int offset = 0) => _pos + offset < source.Length ? source[_pos + offset] : '\0';

    private void Advance()
    {
        if (source[_pos] == '\n')
        {
            _line++;
            _lineStart = _pos + 1;
            _atLineStart = true;
        }
        _pos++;
    }

    private void SkipTrivia()
    {
        while (_pos < source.Length)
        {
            char c = Peek();
            if (c == '\n') { Advance(); }
            else if (c is ' ' or '\t' or '\r') { _pos++; }
            else if (c == '/' && Peek(1) == '/')
            {
                while (_pos < source.Length && Peek() != '\n') _pos++;
            }
            else if (c == '/' && Peek(1) == '*')
            {
                _pos += 2;
                while (_pos < source.Length && !(Peek() == '*' && Peek(1) == '/')) Advance();
                _pos = Math.Min(_pos + 2, source.Length);
            }
            else break;
        }
    }

    public List<Token> Tokenize()
    {
        List<Token> tokens = [];
        while (true)
        {
            SkipTrivia();
            int line = _line, column = _pos - _lineStart + 1, position = _pos;
            if (_pos >= source.Length)
            {
                tokens.Add(new Token { Kind = TokenKind.End, Line = line, Column = column, Position = position });
                return tokens;
            }

            char c = Peek();
            Token token;
            if (c == '#' && _atLineStart) token = ReadDirective();
            else if (char.IsLetter(c) || c == '_') token = ReadIdentifier();
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1)))) token = ReadNumber();
            else if (c == '"') token = ReadString();
            else if (c == '\'') token = ReadChar();
            else token = ReadPunct();

            token.Line = line;
            token.Column = column;
            token.Position = position;
            tokens.Add(token);
            _atLineStart = false;
        }
    }

    private Token ReadDirective()
    {
        _pos++; // '#'
        StringBuilder text = new();
        while (_pos < source.Length && Peek() != '\n')
        {
            if (Peek() == '\\' && (Peek(1) == '\n' || (Peek(1) == '\r' && Peek(2) == '\n')))
            {
                _pos += Peek(1) == '\r' ? 3 : 2;
                _line++;
                _lineStart = _pos;
                text.Append(' ');
                continue;
            }
            if (Peek() == '/' && Peek(1) == '/')
            {
                while (_pos < source.Length && Peek() != '\n') _pos++;
                break;
            }
            if (Peek() == '/' && Peek(1) == '*')
            {
                _pos += 2;
                while (_pos < source.Length && !(Peek() == '*' && Peek(1) == '/')) Advance();
                _pos = Math.Min(_pos + 2, source.Length);
                text.Append(' ');
                continue;
            }
            text.Append(Peek());
            _pos++;
        }
        return new Token { Kind = TokenKind.Directive, Text = text.ToString().Trim() };
    }

    private Token ReadIdentifier()
    {
        int start = _pos;
        while (char.IsLetterOrDigit(Peek()) || Peek() == '_') _pos++;
        return new Token { Kind = TokenKind.Ident, Text = source[start.._pos] };
    }

    private Token ReadNumber()
    {
        int start = _pos;
        bool isFloat = false;
        int radix = 10;
        if (Peek() == '0' && Peek(1) is 'x' or 'X') { radix = 16; _pos += 2; }
        else if (Peek() == '0' && Peek(1) is 'b' or 'B' && Peek(2) is '0' or '1') { radix = 2; _pos += 2; }
        else if (Peek() == '0' && Peek(1) is 'o' or 'O') { radix = 8; _pos += 2; }

        int digitsStart = _pos;
        if (radix == 10)
        {
            while (char.IsAsciiDigit(Peek()) || Peek() == '_') _pos++;
            if (Peek() == '.' && Peek(1) != '.')
            {
                isFloat = true;
                _pos++;
                while (char.IsAsciiDigit(Peek()) || Peek() == '_') _pos++;
            }
            if (Peek() is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) || (Peek(1) is '+' or '-' && char.IsAsciiDigit(Peek(2)))))
            {
                isFloat = true;
                _pos += 2;
                while (char.IsAsciiDigit(Peek())) _pos++;
            }
        }
        else
        {
            while (Uri.IsHexDigit(Peek()) && (radix == 16 || Peek() is >= '0' and <= '9') || Peek() == '_') _pos++;
        }

        string digits = source[digitsStart.._pos].Replace("_", "");
        Token token = new() { Kind = isFloat ? TokenKind.Float : TokenKind.Integer };

        // Suffixes: u / l / ul / ll for integers, f / d for floats.
        int suffixStart = _pos;
        while (Peek() is 'u' or 'U' or 'l' or 'L' or 'f' or 'F' or 'd' or 'D') _pos++;
        string suffix = source[suffixStart.._pos].ToLowerInvariant();
        if (suffix.Contains('f') || suffix.Contains('d')) { isFloat = radix == 10; token.Kind = TokenKind.Float; }
        token.Unsigned = suffix.Contains('u');
        token.Text = source[start.._pos];

        if (token.Kind == TokenKind.Float)
            token.FloatValue = double.Parse(digits, NumberStyles.Float, CultureInfo.InvariantCulture);
        else
        {
            Int128 value = 0;
            foreach (char ch in digits)
            {
                int digit = Convert.ToInt32(ch.ToString(), 16);
                if (digit >= radix) throw new HexpatException($"invalid digit '{ch}' in number", _line, _pos - _lineStart + 1);
                value = value * radix + digit;
            }
            token.IntValue = value;
            token.Unsigned |= value > long.MaxValue;
        }
        return token;
    }

    private string ReadEscaped(char delimiter)
    {
        StringBuilder text = new();
        _pos++; // opening quote
        while (true)
        {
            if (_pos >= source.Length) throw new HexpatException("unterminated string or character literal", _line, _pos - _lineStart + 1);
            char c = Peek();
            if (c == delimiter) { _pos++; return text.ToString(); }
            if (c == '\\')
            {
                char e = Peek(1);
                _pos += 2;
                switch (e)
                {
                    case 'n': text.Append('\n'); break;
                    case 't': text.Append('\t'); break;
                    case 'r': text.Append('\r'); break;
                    case '0': text.Append('\0'); break;
                    case 'a': text.Append('\a'); break;
                    case 'b': text.Append('\b'); break;
                    case 'f': text.Append('\f'); break;
                    case 'v': text.Append('\v'); break;
                    case 'x':
                    {
                        int start = _pos;
                        while (_pos - start < 2 && Uri.IsHexDigit(Peek())) _pos++;
                        text.Append((char)Convert.ToInt32(source[start.._pos], 16));
                        break;
                    }
                    default: text.Append(e); break;
                }
                continue;
            }
            if (c == '\n') _line++;
            text.Append(c);
            _pos++;
        }
    }

    private Token ReadString()
    {
        string value = ReadEscaped('"');
        return new Token { Kind = TokenKind.String, Text = value, StringValue = value };
    }

    private Token ReadChar()
    {
        string value = ReadEscaped('\'');
        if (value.Length != 1) throw new HexpatException("a character literal must be one character", _line, _pos - _lineStart + 1);
        return new Token { Kind = TokenKind.Char, Text = value, IntValue = value[0] };
    }

    private Token ReadPunct()
    {
        foreach (string op in Operators)
        {
            if (string.CompareOrdinal(source, _pos, op, 0, op.Length) == 0)
            {
                _pos += op.Length;
                return new Token { Kind = TokenKind.Punct, Text = op };
            }
        }
        return new Token { Kind = TokenKind.Punct, Text = source[_pos++].ToString() };
    }
}
