namespace HexpatSharp;

/// <summary>
/// Handles the <c>#</c> lines: <c>#define</c> (object-like macros), <c>#ifdef</c> / <c>#ifndef</c> / <c>#else</c> / <c>#endif</c>,
/// <c>#include</c> and <c>#pragma</c>. The standard library (<c>&lt;std/...&gt;</c>) is built in, so those includes do nothing.
/// </summary>
public sealed class Preprocessor
{
    private readonly Dictionary<string, List<Token>> _defines = new(StringComparer.Ordinal);
    private readonly HashSet<string> _included = new(StringComparer.Ordinal);
    private readonly Func<string, string?>? _resolveInclude;

    public Dictionary<string, string> Pragmas { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    public Preprocessor(Func<string, string?>? resolveInclude = null) => _resolveInclude = resolveInclude;

    public List<Token> Process(string source) => Process(new Lexer(source).Tokenize(), 0);

    private List<Token> Process(List<Token> tokens, int depth)
    {
        if (depth > 32) throw new HexpatException("#include nested too deeply");

        List<Token> output = [];
        // For each open #if: whether this branch is live, and whether the enclosing one is.
        Stack<(bool Live, bool ParentLive, bool Taken)> conditions = new();
        bool Live() => conditions.Count == 0 || conditions.Peek().Live;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            if (token.Kind == TokenKind.Directive)
            {
                HandleDirective(token, output, conditions, depth);
                continue;
            }

            if (!Live()) continue;
            if (token.Kind == TokenKind.End)
            {
                if (depth == 0) output.Add(token);
                continue;
            }

            if (token.Kind == TokenKind.Ident && _defines.TryGetValue(token.Text, out List<Token>? replacement))
            {
                Expand(replacement, token, output, 0);
                continue;
            }
            output.Add(token);
        }

        if (conditions.Count > 0) throw new HexpatException("#if without #endif");
        return output;
    }

    private void Expand(List<Token> replacement, Token at, List<Token> output, int depth)
    {
        if (depth > 16) throw new HexpatException("macro expands without end", at.Line, at.Column);
        foreach (Token t in replacement)
        {
            if (t.Kind == TokenKind.Ident && _defines.TryGetValue(t.Text, out List<Token>? nested) && !ReferenceEquals(nested, replacement))
            {
                Expand(nested, at, output, depth + 1);
                continue;
            }
            output.Add(new Token
            {
                Kind = t.Kind, Text = t.Text, IntValue = t.IntValue, Unsigned = t.Unsigned, FloatValue = t.FloatValue,
                StringValue = t.StringValue, Line = at.Line, Column = at.Column, Position = at.Position,
            });
        }
    }

    private void HandleDirective(Token token, List<Token> output, Stack<(bool Live, bool ParentLive, bool Taken)> conditions, int depth)
    {
        string text = token.Text;
        int space = text.IndexOfAny([' ', '\t']);
        string name = space < 0 ? text : text[..space];
        string rest = space < 0 ? "" : text[(space + 1)..].Trim();
        bool live = conditions.Count == 0 || conditions.Peek().Live;

        switch (name)
        {
            case "ifdef" or "ifndef":
            {
                bool defined = _defines.ContainsKey(rest.Split(' ')[0]);
                bool condition = name == "ifdef" ? defined : !defined;
                conditions.Push((live && condition, live, condition));
                return;
            }
            case "if":
            {
                // Only the simple forms are understood: a number, defined(NAME), or a name defined as a number.
                bool condition = rest.StartsWith("defined", StringComparison.Ordinal)
                    ? _defines.ContainsKey(rest.Replace("defined", "").Trim(' ', '(', ')'))
                    : rest != "0" && rest.Length > 0;
                conditions.Push((live && condition, live, condition));
                return;
            }
            case "else":
            {
                if (conditions.Count == 0) throw new HexpatException("#else without #if", token.Line, token.Column);
                var (_, parentLive, taken) = conditions.Pop();
                conditions.Push((parentLive && !taken, parentLive, true));
                return;
            }
            case "endif":
                if (conditions.Count == 0) throw new HexpatException("#endif without #if", token.Line, token.Column);
                conditions.Pop();
                return;
        }

        if (!live) return;
        switch (name)
        {
            case "define":
            {
                int split = rest.IndexOfAny([' ', '\t']);
                string macro = split < 0 ? rest : rest[..split];
                string body = split < 0 ? "" : rest[(split + 1)..];
                List<Token> value = new Lexer(body).Tokenize();
                value.RemoveAll(t => t.Kind == TokenKind.End);
                _defines[macro] = value;
                break;
            }
            case "undef":
                _defines.Remove(rest);
                break;
            case "pragma":
            {
                int split = rest.IndexOfAny([' ', '\t']);
                string pragma = split < 0 ? rest : rest[..split];
                Pragmas[pragma] = split < 0 ? "" : rest[(split + 1)..].Trim();
                break;
            }
            case "include":
            {
                string path = rest.Trim('<', '>', '"', ' ');
                if (path.StartsWith("std/", StringComparison.Ordinal) || path.StartsWith("type/", StringComparison.Ordinal)) break;
                string? included = _resolveInclude?.Invoke(path);
                if (included is null)
                {
                    Warnings.Add($"{token.Line}: include '{path}' was not found and is ignored");
                    break;
                }
                if (!_included.Add(path)) break;
                output.AddRange(Process(new Lexer(included).Tokenize(), depth + 1));
                break;
            }
            case "error":
                throw new HexpatException($"#error {rest}", token.Line, token.Column);
            case "warning":
                Warnings.Add($"{token.Line}: {rest}");
                break;
        }
    }
}
