namespace HexpatSharp;

public sealed class EvaluationOptions
{
    /// <summary>Most patterns that may be made (<c>#pragma pattern_limit</c> changes it).</summary>
    public ulong PatternLimit { get; set; } = 0x200000;

    /// <summary>Most elements one array may have (<c>#pragma array_limit</c>).</summary>
    public ulong ArrayLimit { get; set; } = 0x200000;

    /// <summary>Deepest nesting of function calls and structs (<c>#pragma eval_depth</c>).</summary>
    public int EvalDepth { get; set; } = 0x800;

    /// <summary>Most steps of loops and function bodies, a guard against patterns that never end.</summary>
    public long StepLimit { get; set; } = 200_000_000;

    public Endian DefaultEndian { get; set; } = Endian.Little;

    /// <summary>Gives the source of a file named by <c>#include "..."</c>; the standard library is built in and never asks.</summary>
    public Func<string, string?>? ResolveInclude { get; set; }
}

public sealed class EvaluationResult
{
    /// <summary>The variables placed at the top level, in order. Kept when evaluation stops with an error.</summary>
    public List<Pattern> Patterns { get; } = [];

    public List<string> Log { get; } = [];

    /// <summary>Why evaluation stopped early, or null.</summary>
    public string? Error { get; set; }

    public int ErrorLine { get; set; }

    public Dictionary<string, string> Pragmas { get; } = [];

    internal Interpreter? Interpreter { get; set; }

    /// <summary>The patterns as a tree of JSON for a viewer (see <see cref="PatternJson"/>).</summary>
    public string ToJson(PatternJson.Options? options = null) => PatternJson.ToJson(this, options);
}

/// <summary>Reads a pattern and lays it out over data.</summary>
public static class Hexpat
{
    public static EvaluationResult Evaluate(string source, byte[] data, EvaluationOptions? options = null)
    {
        options ??= new EvaluationOptions();
        EvaluationResult result = new();
        Interpreter interpreter;
        try
        {
            Preprocessor preprocessor = new(options.ResolveInclude);
            List<Token> tokens = preprocessor.Process(source);
            Program program = new Parser(tokens).ParseProgram();
            Program prelude = new Parser(new Preprocessor().Process(Prelude.Source)).ParseProgram();
            program.Items.InsertRange(0, prelude.Items);
            foreach (var (name, value) in preprocessor.Pragmas) result.Pragmas[name] = value;
            foreach (string warning in preprocessor.Warnings) result.Log.Add("warning: " + warning);
            ApplyPragmas(preprocessor.Pragmas, options);
            interpreter = new Interpreter(data, options, result);
            result.Interpreter = interpreter;
            interpreter.Run(program);
        }
        catch (HexpatException e)
        {
            result.Error = e.Message;
            result.ErrorLine = e.Line;
        }
        return result;
    }

    private static void ApplyPragmas(Dictionary<string, string> pragmas, EvaluationOptions options)
    {
        static bool TryNumber(string text, out long value)
        {
            text = text.Trim();
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out value)
                : long.TryParse(text, out value);
        }

        if (pragmas.TryGetValue("pattern_limit", out string? pl) && TryNumber(pl, out long patternLimit) && patternLimit > 0)
            options.PatternLimit = Math.Max(options.PatternLimit, (ulong)patternLimit);
        if (pragmas.TryGetValue("array_limit", out string? al) && TryNumber(al, out long arrayLimit) && arrayLimit > 0)
            options.ArrayLimit = Math.Max(options.ArrayLimit, (ulong)arrayLimit);
        if (pragmas.TryGetValue("eval_depth", out string? ed) && TryNumber(ed, out long depth) && depth > 0)
            options.EvalDepth = (int)Math.Max(options.EvalDepth, Math.Min(depth, 0x10000));
        if (pragmas.TryGetValue("endian", out string? endian))
            options.DefaultEndian = endian.Trim().ToLowerInvariant() == "big" ? Endian.Big : Endian.Little;
    }
}

internal enum TypeKind { Builtin, Struct, Enum, Bitfield, Alias }

internal sealed class TypeDef
{
    public TypeKind Kind;
    public string Name = "";
    public string Namespace = "";
    public TypeDecl? Decl;
    public ScalarKind Scalar;
    public int Size;
    public EnumDef? EnumDef;
}

internal sealed class TypeBinding(TypeRef type, Scope scope)
{
    public TypeRef Type { get; } = type;
    public Scope Scope { get; } = scope;
}

internal sealed class ResolvedType
{
    public TypeDef Def = null!;
    public TypeRef Ref = null!;
    public Scope Scope = null!;
    public EndianOverride Endian;
}

internal enum Flow { Normal, Break, Continue, Return }

internal sealed class Frame
{
    public Value ReturnValue = Value.Void;
}

internal sealed class Scope(Scope? parent)
{
    public Scope? Parent { get; } = parent;
    public Dictionary<string, Value> Vars { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, TypeBinding>? TypeParams { get; set; }
    public StructPattern? This { get; set; }
    public Pattern? ParentPattern { get; set; }
    public string Namespace { get; set; } = parent?.Namespace ?? "";
    public bool IsFunction { get; set; }
    public Frame? Frame { get; set; }

    public bool TryGetVariable(string name, out Value value, out Scope? owner)
    {
        for (Scope? s = this; s is not null; s = s.Parent)
        {
            if (s.Vars.TryGetValue(name, out Value? found))
            {
                value = found;
                owner = s;
                return true;
            }
        }
        value = Value.Void;
        owner = null;
        return false;
    }

    public Scope? FindThisScope()
    {
        for (Scope? s = this; s is not null; s = s.Parent)
            if (s.This is not null) return s;
        return null;
    }

    public Frame? FindFrame()
    {
        for (Scope? s = this; s is not null; s = s.Parent)
            if (s.Frame is not null) return s.Frame;
        return null;
    }
}

internal sealed partial class Interpreter
{
    private static readonly Dictionary<string, (ScalarKind Kind, int Size)> Builtins = new(StringComparer.Ordinal)
    {
        ["u8"] = (ScalarKind.Unsigned, 1), ["u16"] = (ScalarKind.Unsigned, 2), ["u24"] = (ScalarKind.Unsigned, 3),
        ["u32"] = (ScalarKind.Unsigned, 4), ["u48"] = (ScalarKind.Unsigned, 6), ["u64"] = (ScalarKind.Unsigned, 8),
        ["u96"] = (ScalarKind.Unsigned, 12), ["u128"] = (ScalarKind.Unsigned, 16),
        ["s8"] = (ScalarKind.Signed, 1), ["s16"] = (ScalarKind.Signed, 2), ["s24"] = (ScalarKind.Signed, 3),
        ["s32"] = (ScalarKind.Signed, 4), ["s48"] = (ScalarKind.Signed, 6), ["s64"] = (ScalarKind.Signed, 8),
        ["s96"] = (ScalarKind.Signed, 12), ["s128"] = (ScalarKind.Signed, 16),
        ["float"] = (ScalarKind.Float, 4), ["double"] = (ScalarKind.Float, 8),
        ["char"] = (ScalarKind.Char, 1), ["char16"] = (ScalarKind.Char16, 2),
        ["bool"] = (ScalarKind.Bool, 1),
    };

    private readonly EvaluationOptions _options;
    private readonly EvaluationResult _result;
    private readonly Section _main;
    private readonly Dictionary<int, Section> _sections = [];
    private readonly Dictionary<string, TypeDef> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionDecl> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Scope> _namespaceScopes = new(StringComparer.Ordinal);
    private readonly Stack<StructPattern> _building = new();
    private readonly Scope _global;
    private int _nextSectionId = 1;

    // Above zero while placing something temporary (sizeof, a function's own variables): nothing of it joins the results.
    private int _quiet;

    private ulong _cursor;
    private Section _section;
    private Endian _endian;
    private ulong _patternCount;
    private int _depth;
    private long _steps;

    public Interpreter(byte[] data, EvaluationOptions options, EvaluationResult result)
    {
        _options = options;
        _result = result;
        _main = new Section(0, "main", data);
        _sections[0] = _main;
        _section = _main;
        _endian = options.DefaultEndian;
        _global = new Scope(null);
        _namespaceScopes[""] = _global;
    }

    public void Run(Program program)
    {
        List<Stmt> code = [];
        Register(program.Items, "", code);

        foreach (Stmt statement in code)
        {
            Flow flow = Exec(statement, _global, null);
            if (flow == Flow.Return) break;
        }
    }

    // ---- registry ----

    private void Register(List<object> items, string ns, List<Stmt> code)
    {
        foreach (object item in items)
        {
            switch (item)
            {
                case TypeDecl decl:
                    RegisterType(decl);
                    break;
                case FunctionDecl fn:
                    _functions[Qualify(fn.Namespace, fn.Name)] = fn;
                    break;
                case NamespaceDecl nsDecl:
                    Register(nsDecl.Items, ns.Length > 0 ? ns + "::" + nsDecl.Name : nsDecl.Name, code);
                    break;
                case Stmt statement:
                    code.Add(statement);
                    break;
            }
        }
    }

    private static string Qualify(string ns, string name) => ns.Length > 0 ? ns + "::" + name : name;

    private void RegisterType(TypeDecl decl)
    {
        string key = Qualify(decl.Namespace, decl.Name);
        TypeDef def = new() { Name = decl.Name, Namespace = decl.Namespace, Decl = decl };
        switch (decl)
        {
            case StructDecl:
                def.Kind = TypeKind.Struct;
                break;
            case EnumDecl:
                def.Kind = TypeKind.Enum;
                break;
            case BitfieldDecl:
                def.Kind = TypeKind.Bitfield;
                break;
            case UsingDecl u:
                if (u.Target is null) return; // a forward declaration: the real one is registered by its own declaration
                def.Kind = TypeKind.Alias;
                break;
        }

        // A struct declared with no body after a forward alias is fine; later definitions win.
        _types[key] = def;
    }

    private Scope NamespaceScope(string ns)
    {
        if (_namespaceScopes.TryGetValue(ns, out Scope? scope)) return scope;
        scope = new Scope(_global) { Namespace = ns };
        _namespaceScopes[ns] = scope;
        return scope;
    }

    // ---- type resolution ----

    private ResolvedType ResolveType(TypeRef type, Scope scope)
    {
        if (type.Path.Count == 1)
        {
            string name = type.Path[0];
            for (Scope? s = scope; s is not null; s = s.Parent)
            {
                if (s.TypeParams is not null && s.TypeParams.TryGetValue(name, out TypeBinding? binding))
                {
                    ResolvedType inner = ResolveType(binding.Type, binding.Scope);
                    if (type.Endian != EndianOverride.None) inner.Endian = type.Endian;
                    return inner;
                }
            }

            if (Builtins.TryGetValue(name, out var builtin))
            {
                return new ResolvedType
                {
                    Def = new TypeDef { Kind = TypeKind.Builtin, Name = name, Scalar = builtin.Kind, Size = builtin.Size },
                    Ref = type,
                    Scope = scope,
                    Endian = type.Endian,
                };
            }
        }

        string joined = type.Joined;
        string ns = scope.Namespace;
        while (true)
        {
            string candidate = Qualify(ns, joined);
            if (_types.TryGetValue(candidate, out TypeDef? def))
                return new ResolvedType { Def = def, Ref = type, Scope = scope, Endian = type.Endian };
            if (ns.Length == 0) break;
            int cut = ns.LastIndexOf("::", StringComparison.Ordinal);
            ns = cut < 0 ? "" : ns[..cut];
        }

        throw new HexpatException($"unknown type '{joined}'", type.Line, type.Column);
    }

    private bool TypeExists(TypeRef type, Scope scope)
    {
        try
        {
            ResolveType(type, scope);
            return true;
        }
        catch (HexpatException)
        {
            return false;
        }
    }

    // Where evaluation is, for errors raised without a place of their own.
    private int _line;
    private int _column;

    private HexpatException Fail(string message, int line = 0, int column = 0)
        => line > 0 ? new HexpatException(message, line, column) : new HexpatException(message, _line, _column);

    private HexpatException Fail(string message, Expr at) => new(message, at.Line, at.Column);

    private HexpatException Fail(string message, Stmt at) => new(message, at.Line, at.Column);

    private void Step()
    {
        if (++_steps > _options.StepLimit) throw Fail("evaluation took too many steps (is there a loop that never ends?)");
    }

    private void CountPattern(int line = 0)
    {
        if (++_patternCount > _options.PatternLimit)
            throw Fail($"more than {_options.PatternLimit} patterns were made; raise it with '#pragma pattern_limit'", line);
    }
}
