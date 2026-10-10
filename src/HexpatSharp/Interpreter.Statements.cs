namespace HexpatSharp;

internal sealed partial class Interpreter
{
    private bool InFunction(Scope scope)
    {
        for (Scope? s = scope; s is not null; s = s.Parent)
        {
            if (s.IsFunction) return true;
            if (s.This is not null) return false;
        }
        return false;
    }

    private Flow ExecBody(List<Stmt> body, Scope scope, StructPattern? owner)
    {
        foreach (Stmt statement in body)
        {
            Flow flow = Exec(statement, scope, owner);
            if (flow != Flow.Normal) return flow;
        }
        return Flow.Normal;
    }

    private Flow Exec(Stmt statement, Scope scope, StructPattern? owner)
    {
        try
        {
            return ExecCore(statement, scope, owner);
        }
        catch (HexpatException e) when (e.Line == 0 && statement.Line > 0)
        {
            // Errors raised deep down (a read past the end of the data) get the place of the statement that caused them.
            throw new HexpatException(e.PlainMessage, statement.Line, statement.Column);
        }
    }

    private Flow ExecCore(Stmt statement, Scope scope, StructPattern? owner)
    {
        Step();
        _line = statement.Line;
        _column = statement.Column;
        switch (statement)
        {
            case VarDecl decl:
                ExecVarDecl(decl, scope, owner);
                return Flow.Normal;

            case PaddingStmt padding:
            {
                ulong size = ToUnsigned(Eval(padding.Size, scope), padding.Size);
                CountPattern(statement.Line);
                _cursor += size;
                return Flow.Normal;
            }

            case ExprStmt expression:
                Eval(expression.Expr, scope);
                return Flow.Normal;

            case BlockStmt block:
                return ExecBody(block.Body, scope, owner);

            case IfStmt branch:
                if (Truthy(Eval(branch.Condition, scope), branch.Condition))
                    return Exec(branch.Then, scope, owner);
                return branch.Otherwise is null ? Flow.Normal : Exec(branch.Otherwise, scope, owner);

            case WhileStmt loop:
                while (Truthy(Eval(loop.Condition, scope), loop.Condition))
                {
                    Step();
                    Flow flow = Exec(loop.Body, scope, owner);
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                }
                return Flow.Normal;

            case ForStmt loop:
            {
                if (loop.Init is not null) Exec(loop.Init, scope, owner);
                while (loop.Condition is null || Truthy(Eval(loop.Condition, scope), loop.Condition))
                {
                    Step();
                    Flow flow = Exec(loop.Body, scope, owner);
                    if (flow == Flow.Break) break;
                    if (flow == Flow.Return) return flow;
                    if (loop.Step is not null) Eval(loop.Step, scope);
                }
                return Flow.Normal;
            }

            case MatchStmt match:
                return ExecMatch(match, scope, owner);

            case BreakStmt:
                return Flow.Break;

            case ContinueStmt:
                return Flow.Continue;

            case ReturnStmt ret:
            {
                Frame? frame = scope.FindFrame();
                Value value = ret.Value is null ? Value.Void : Eval(ret.Value, scope);
                if (frame is not null) frame.ReturnValue = value;
                return Flow.Return;
            }

            case TypeDeclStmt typeDecl:
                RegisterType(typeDecl.Decl);
                return Flow.Normal;
        }
        throw Fail("unsupported statement", statement);
    }

    private Flow ExecMatch(MatchStmt match, Scope scope, StructPattern? owner)
    {
        List<Value> values = [.. match.Scrutinees.Select(e => Resolve(Eval(e, scope), e))];
        foreach (MatchArm arm in match.Arms)
        {
            if (arm.Patterns.Count != values.Count)
                throw Fail($"a match arm needs {values.Count} pattern(s) but has {arm.Patterns.Count}", match);

            bool matches = true;
            for (int i = 0; i < values.Count && matches; i++)
                matches = arm.Patterns[i].Any(p => MatchesPattern(values[i], p, scope));
            if (matches) return Exec(arm.Body, scope, owner);
        }
        return Flow.Normal;
    }

    private bool MatchesPattern(Value value, MatchPattern pattern, Scope scope)
    {
        if (pattern.Wildcard) return true;
        Value low = Resolve(Eval(pattern.Low!, scope), pattern.Low!);
        if (pattern.High is null) return ValuesEqual(value, low);
        Value high = Resolve(Eval(pattern.High, scope), pattern.High);
        return Compare(value, low) >= 0 && Compare(value, high) <= 0;
    }

    // ---- variables ----

    private void ExecVarDecl(VarDecl d, Scope scope, StructPattern? owner)
    {
        bool inFunction = InFunction(scope);

        if (d.Init is not null || d.IsInOut || (inFunction && d.At is null && d.ArrayKind == ArrayKind.None && !d.IsPointer))
        {
            Value value = d.Init is not null ? Eval(d.Init, scope) : DefaultValue(d.Type, scope);
            value = ConvertToDeclared(d.Type, value, scope, d);
            scope.Vars[d.Name] = value;
            if (owner is not null) owner.Locals[d.Name] = value;
            return;
        }

        PlaceDeclaration(d, scope, owner, inFunction);
    }

    private void PlaceDeclaration(VarDecl d, Scope scope, StructPattern? owner, bool inFunction)
    {
        ulong savedCursor = _cursor;
        Section savedSection = _section;
        ulong unionStart = _cursor;

        if (d.InSection is not null) _section = SectionOf(Resolve(Eval(d.InSection, scope), d.InSection), d.InSection);
        if (d.At is not null) _cursor = ToUnsigned(Eval(d.At, scope), d.At);

        ResolvedType type = ResolveType(d.Type, scope);
        Pattern pattern;
        if (d.IsPointer) pattern = PlacePointer(d, type, scope, owner);
        else if (d.ArrayKind != ArrayKind.None) pattern = PlaceArray(d, type, scope, owner);
        else pattern = PlaceType(type, d.Name, scope);

        pattern.Name = d.Name;
        ApplyAttributes(pattern, d.Attributes, scope);

        if (d.At is not null) _cursor = savedCursor;
        _section = savedSection;

        if (owner is not null)
        {
            owner.Add(pattern);
            if (owner.IsUnion && d.At is null)
            {
                ulong end = _cursor;
                _unionEnds[owner] = Math.Max(_unionEnds.GetValueOrDefault(owner), end);
                _cursor = unionStart;
            }
        }
        else if (!inFunction && pattern.Source == _main)
        {
            _result.Patterns.Add(pattern);
        }
        if (d.Name.Length > 0) scope.Vars[d.Name] = Value.FromPattern(pattern);
    }

    private readonly Dictionary<StructPattern, ulong> _unionEnds = [];

    private Value DefaultValue(TypeRef type, Scope scope)
    {
        if (type.Path.Count == 1)
        {
            switch (type.Path[0])
            {
                case "str": return Value.FromString("");
                case "float" or "double": return Value.FromFloat(0);
                case "bool": return Value.FromBool(false);
                case "char": return Value.FromChar(0);
                case "auto": return Value.FromInt(0);
            }
            if (Builtins.TryGetValue(type.Path[0], out var builtin))
                return Value.FromInt(0, builtin.Kind == ScalarKind.Unsigned);
        }
        return Value.FromInt(0);
    }

    // Converts a value to the type it was declared with, where that is a plain type (a u8 keeps only a byte).
    private Value ConvertToDeclared(TypeRef type, Value value, Scope scope, Stmt at)
    {
        if (type.Path.Count != 1) return value;
        string name = type.Path[0];
        if (name is "auto") return value;
        if (name == "str") return Value.FromString(ToText(value));

        if (!Builtins.TryGetValue(name, out var builtin)) return value;
        Value plain = Resolve(value, null);
        switch (builtin.Kind)
        {
            case ScalarKind.Float:
                return Value.FromFloat(ToDouble(plain));
            case ScalarKind.Bool:
                return Value.FromBool(Truthy(plain, null));
            case ScalarKind.Char or ScalarKind.Char16:
                return Value.FromChar((int)(ToInteger(plain) & 0xFFFF));
            case ScalarKind.Unsigned:
                return Value.FromUnsigned(Mask(ToInteger(plain), builtin.Size));
            default:
                return Value.FromInt(SignExtend(ToInteger(plain), builtin.Size));
        }
    }

    private static UInt128 Mask(Int128 value, int bytes)
    {
        UInt128 raw = (UInt128)value;
        return bytes >= 16 ? raw : raw & (((UInt128)1 << (bytes * 8)) - 1);
    }

    private static Int128 SignExtend(Int128 value, int bytes)
    {
        if (bytes >= 16) return value;
        int bits = bytes * 8;
        Int128 mask = ((Int128)1 << bits) - 1;
        Int128 masked = value & mask;
        return (masked & ((Int128)1 << (bits - 1))) != 0 ? masked - ((Int128)1 << bits) : masked;
    }

    // ---- attributes ----

    private void ApplyAttributes(Pattern pattern, List<Attribute> attributes, Scope scope)
    {
        foreach (Attribute attribute in attributes)
        {
            pattern.Attributes.Add(attribute);
            Value? first = attribute.Args.Count > 0 ? Evaluate(attribute.Args[0], scope) : null;
            if (first is not null) pattern.AttributeValues[attribute.Name] = first;
            string text() => first is null ? "" : ToText(first);

            switch (attribute.Name)
            {
                case "hidden": pattern.Hidden = true; break;
                case "inline": pattern.Inline = true; break;
                case "format" or "format_read": pattern.Format = text(); break;
                case "transform": pattern.Transform = text(); break;
                case "comment": pattern.Comment = text(); break;
                case "color": pattern.Color = text(); break;
                case "name": pattern.DisplayName = text(); break;
            }
        }
    }

    private Value Evaluate(Expr expr, Scope scope)
    {
        try
        {
            return Resolve(Eval(expr, scope), expr);
        }
        catch (HexpatException)
        {
            return Value.Void; // an attribute argument that cannot be evaluated is ignored
        }
    }

    private void ApplyDeclAttributes(Pattern pattern, TypeDecl decl, Scope scope)
        => ApplyAttributes(pattern, decl.Attributes, NamespaceScope(decl.Namespace));
}
