using System.Text;

namespace HexpatSharp;

internal sealed partial class Interpreter
{
    // ---- values of patterns ----

    /// <summary>The value a pattern stands for in arithmetic: numbers for plain patterns, a string for characters, the transform's result where it has one.</summary>
    public Value ValueOf(Pattern pattern, bool applyTransform = true)
    {
        if (applyTransform && pattern.Transform is not null)
        {
            Value transformed = CallNamedFunction(pattern.Transform, [Value.FromPattern(pattern)], _global, null);
            return Resolve(transformed, null);
        }

        switch (pattern)
        {
            case ScalarPattern scalar:
                return ScalarValue(scalar);
            case EnumPattern en:
                return ScalarValue(en.Underlying);
            case BitfieldFieldPattern field:
                return BitfieldFieldValue(field);
            case PointerPattern pointer:
                return ScalarValue(pointer.Pointer);
            case PaddingPattern:
                return Value.FromInt(0);
            case ArrayPattern array when array.ScalarElement is ScalarKind.Char or ScalarKind.Char16:
                return Value.FromString(ArrayText(array));
            default:
                return Value.FromPattern(pattern);
        }
    }

    private Value ScalarValue(ScalarPattern scalar)
    {
        UInt128 raw = scalar.ReadRaw();
        int size = (int)scalar.Size;
        switch (scalar.Kind)
        {
            case ScalarKind.Unsigned:
                return Value.FromUnsigned(raw);
            case ScalarKind.Signed:
                return Value.FromInt(SignExtend((Int128)raw, size));
            case ScalarKind.Bool:
                return Value.FromBool(raw != 0);
            case ScalarKind.Char or ScalarKind.Char16:
                return Value.FromChar((int)raw);
            default:
                return size == 4
                    ? Value.FromFloat(BitConverter.UInt32BitsToSingle((uint)raw))
                    : Value.FromFloat(BitConverter.UInt64BitsToDouble((ulong)raw));
        }
    }

    private Value BitfieldFieldValue(BitfieldFieldPattern field)
    {
        UInt128 raw = field.ReadBits();
        return field.Kind switch
        {
            ScalarKind.Bool => Value.FromBool(raw != 0),
            ScalarKind.Signed => Value.FromInt(field.BitSize < 127 && ((raw >> (field.BitSize - 1)) & 1) != 0 ? (Int128)raw - ((Int128)1 << field.BitSize) : (Int128)raw),
            _ => Value.FromUnsigned(raw),
        };
    }

    private string ArrayText(ArrayPattern array)
    {
        StringBuilder text = new();
        ulong size = array.ElementSize;
        for (ulong i = 0; i < array.Count; i++)
        {
            int code = (int)array.Source.ReadUnsigned(array.Offset + i * size, (int)size, array.Endian);
            if (code == 0) break;
            text.Append((char)code);
        }
        return text.ToString();
    }

    /// <summary>Turns a pattern reference into the value it stands for; other values pass through.</summary>
    public Value Resolve(Value value, Expr? at)
    {
        _ = at;
        return value.Kind == ValueKind.Pattern ? ValueOf(value.Pattern!) : value;
    }

    // ---- conversions ----

    private Int128 ToInteger(Value value)
    {
        value = value.Kind == ValueKind.Pattern ? ValueOf(value.Pattern!) : value;
        return value.Kind switch
        {
            ValueKind.Integer or ValueKind.Boolean or ValueKind.Char => value.Integer,
            ValueKind.Float => (Int128)value.Float,
            _ => throw Fail($"expected a number but found {Describe(value)}"),
        };
    }

    private double ToDouble(Value value)
    {
        value = value.Kind == ValueKind.Pattern ? ValueOf(value.Pattern!) : value;
        return value.Kind switch
        {
            ValueKind.Float => value.Float,
            ValueKind.Integer or ValueKind.Boolean or ValueKind.Char => value.Unsigned ? (double)(UInt128)value.Integer : (double)value.Integer,
            _ => throw Fail($"expected a number but found {Describe(value)}"),
        };
    }

    private ulong ToUnsigned(Value value, Expr? at)
    {
        try
        {
            return unchecked((ulong)(long)ToInteger(value));
        }
        catch (HexpatException e) when (at is not null && e.Line == 0)
        {
            throw Fail(e.PlainMessage, at);
        }
    }

    private bool Truthy(Value value, Expr? at)
    {
        _ = at;
        value = value.Kind == ValueKind.Pattern ? ValueOf(value.Pattern!) : value;
        return value.Kind switch
        {
            ValueKind.Float => value.Float != 0,
            ValueKind.String => value.Text.Length > 0,
            ValueKind.Void => false,
            _ => value.Integer != 0,
        };
    }

    private static string Describe(Value value) => value.Kind switch
    {
        ValueKind.Pattern => $"a {value.Pattern!.TypeName}",
        ValueKind.Void => "nothing",
        ValueKind.String => "a string",
        ValueKind.Section => "a section",
        ValueKind.List => "a list",
        _ => "a value",
    };

    private string ToText(Value value)
    {
        value = value.Kind == ValueKind.Pattern ? ValueOf(value.Pattern!) : value;
        return value.Kind switch
        {
            ValueKind.String => value.Text,
            ValueKind.Char => ((char)(int)value.Integer).ToString(),
            ValueKind.Pattern => value.Pattern!.TypeName,
            _ => value.ToString(),
        };
    }

    // ---- evaluation ----

    public Value Eval(Expr expr, Scope scope)
    {
        switch (expr)
        {
            case LiteralExpr literal:
                return literal.Value;
            case CursorExpr:
                return Value.FromUnsigned(_cursor);
            case PathExpr path:
                return EvalPath(path, scope);
            case MemberExpr member:
                return EvalMember(member, scope);
            case IndexExpr index:
                return EvalIndex(index, scope);
            case CallExpr call:
                return EvalCall(call, scope);
            case UnaryExpr unary:
                return EvalUnary(unary, scope);
            case BinaryExpr binary:
                return EvalBinary(binary, scope);
            case TernaryExpr ternary:
                return Truthy(Eval(ternary.Condition, scope), ternary.Condition) ? Eval(ternary.Then, scope) : Eval(ternary.Otherwise, scope);
            case AssignExpr assign:
                return EvalAssign(assign, scope);
            case IntrinsicExpr intrinsic:
                return EvalIntrinsic(intrinsic, scope);
            case TypeExpr typeExpr:
                throw Fail($"'{typeExpr.Type}' is a type, not a value", expr);
        }
        throw Fail("unsupported expression", expr);
    }

    private Value EvalPath(PathExpr path, Scope scope)
    {
        if (path.Parts.Count == 1)
        {
            string name = path.Parts[0];
            switch (name)
            {
                case "this":
                    return Value.FromPattern(scope.FindThisScope()?.This ?? throw Fail("'this' can only be used inside a struct", path));
                case "parent":
                {
                    Scope? owner = scope.FindThisScope();
                    Pattern? parent = owner?.ParentPattern;
                    return parent is null ? throw Fail("this struct has no parent", path) : Value.FromPattern(parent);
                }
            }

            if (scope.TryGetVariable(name, out Value value, out _)) return value;
            throw Fail($"unknown name '{name}'", path);
        }

        // Enum::Member, or a constant of the standard library.
        List<string> typePath = path.Parts[..^1];
        string member = path.Parts[^1];
        TypeRef reference = new() { Path = typePath, Line = path.Line, Column = path.Column };
        if (TryResolveType(reference, scope, out ResolvedType? resolved) && resolved!.Def.Kind == TypeKind.Enum)
        {
            foreach (var (entryName, low, _) in GetEnum(resolved.Def).Entries)
                if (entryName == member) return Value.FromInt(low);
            throw Fail($"'{reference}' has no member '{member}'", path);
        }

        if (StdLib.TryConstant(path.Joined, out Value constant)) return constant;
        throw Fail($"unknown name '{path.Joined}'", path);
    }

    private bool TryResolveType(TypeRef type, Scope scope, out ResolvedType? resolved)
    {
        try
        {
            resolved = ResolveType(type, scope);
            return true;
        }
        catch (HexpatException)
        {
            resolved = null;
            return false;
        }
    }

    private Value EvalMember(MemberExpr member, Scope scope)
    {
        Value target = Eval(member.Target, scope);
        if (target.Kind != ValueKind.Pattern) throw Fail($"'{member.Name}' cannot be read from {Describe(target)}", member);

        switch (target.Pattern)
        {
            case StructPattern s:
            {
                if (s.Find(member.Name) is { } found) return Value.FromPattern(found);
                if (s.Locals.TryGetValue(member.Name, out Value? local)) return local;
                throw Fail($"'{s.TypeName}' has no member '{member.Name}'", member);
            }
            case BitfieldPattern bf:
                return bf.ByName.TryGetValue(member.Name, out BitfieldFieldPattern? field)
                    ? Value.FromPattern(field)
                    : throw Fail($"'{bf.TypeName}' has no field '{member.Name}'", member);
            case PointerPattern { Pointee: not null } pointer when member.Name == "address":
                return Value.FromUnsigned(pointer.Address);
            case PointerPattern { Pointee: StructPattern pointee }:
                return pointee.Find(member.Name) is { } viaPointer
                    ? Value.FromPattern(viaPointer)
                    : throw Fail($"no member '{member.Name}'", member);
            case EnumPattern or ScalarPattern:
                throw Fail($"'{target.Pattern!.TypeName}' has no members", member);
        }
        throw Fail($"'{member.Name}' cannot be read from this", member);
    }

    private Value EvalIndex(IndexExpr index, Scope scope)
    {
        Value target = Eval(index.Target, scope);
        Int128 position = ToInteger(Eval(index.Index, scope));

        if (target.Kind == ValueKind.Pattern)
        {
            if (target.Pattern is ArrayPattern array)
            {
                if (position < 0 || (ulong)position >= array.Count)
                    throw Fail($"index {position} is outside the array of {array.Count}", index);
                return array.IsScalar
                    ? Value.FromPattern(array.ScalarAt((ulong)position))
                    : Value.FromPattern(array.Elements[(int)position]);
            }
            target = ValueOf(target.Pattern!);
        }

        switch (target.Kind)
        {
            case ValueKind.String:
                if (position < 0 || position >= target.Text.Length) throw Fail($"index {position} is outside the string", index);
                return Value.FromChar(target.Text[(int)position]);
            case ValueKind.List:
                if (position < 0 || position >= target.Items!.Count) throw Fail($"index {position} is outside the list", index);
                return target.Items[(int)position];
        }
        throw Fail($"{Describe(target)} cannot be indexed", index);
    }

    private Value EvalUnary(UnaryExpr unary, Scope scope)
    {
        Value operand = Resolve(Eval(unary.Operand, scope), unary.Operand);
        switch (unary.Op)
        {
            case "!":
                return Value.FromBool(!Truthy(operand, unary));
            case "-":
                if (operand.Kind == ValueKind.Float) return Value.FromFloat(-operand.Float);
                return Value.FromInt(-ToInteger(operand));
            case "+":
                return operand;
            case "~":
                return Value.FromInt(~ToInteger(operand), operand.Unsigned);
        }
        throw Fail($"unknown operator '{unary.Op}'", unary);
    }

    private Value EvalBinary(BinaryExpr binary, Scope scope)
    {
        if (binary.Op == "&&")
        {
            if (!Truthy(Eval(binary.Left, scope), binary.Left)) return Value.FromBool(false);
            return Value.FromBool(Truthy(Eval(binary.Right, scope), binary.Right));
        }
        if (binary.Op == "||")
        {
            if (Truthy(Eval(binary.Left, scope), binary.Left)) return Value.FromBool(true);
            return Value.FromBool(Truthy(Eval(binary.Right, scope), binary.Right));
        }

        Value left = Resolve(Eval(binary.Left, scope), binary.Left);
        Value right = Resolve(Eval(binary.Right, scope), binary.Right);
        try
        {
            return BinaryOp(binary.Op, left, right);
        }
        catch (HexpatException e) when (e.Line == 0)
        {
            throw Fail(e.PlainMessage, binary);
        }
    }

    private bool ValuesEqual(Value a, Value b)
    {
        a = Resolve(a, null);
        b = Resolve(b, null);
        if (a.Kind == ValueKind.String || b.Kind == ValueKind.String) return ToText(a) == ToText(b);
        return Compare(a, b) == 0;
    }

    private int Compare(Value a, Value b)
    {
        a = Resolve(a, null);
        b = Resolve(b, null);
        if (a.Kind == ValueKind.String || b.Kind == ValueKind.String) return string.CompareOrdinal(ToText(a), ToText(b));
        if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float) return ToDouble(a).CompareTo(ToDouble(b));
        if (a.Unsigned || b.Unsigned) return ((UInt128)ToInteger(a)).CompareTo((UInt128)ToInteger(b));
        return ToInteger(a).CompareTo(ToInteger(b));
    }

    private Value BinaryOp(string op, Value left, Value right)
    {
        // Strings: concatenation and comparison.
        if (left.Kind == ValueKind.String || right.Kind == ValueKind.String)
        {
            switch (op)
            {
                case "+": return Value.FromString(ToText(left) + ToText(right));
                case "==": return Value.FromBool(ToText(left) == ToText(right));
                case "!=": return Value.FromBool(ToText(left) != ToText(right));
                case "<": return Value.FromBool(string.CompareOrdinal(ToText(left), ToText(right)) < 0);
                case ">": return Value.FromBool(string.CompareOrdinal(ToText(left), ToText(right)) > 0);
                case "<=": return Value.FromBool(string.CompareOrdinal(ToText(left), ToText(right)) <= 0);
                case ">=": return Value.FromBool(string.CompareOrdinal(ToText(left), ToText(right)) >= 0);
            }
            throw Fail($"'{op}' cannot be used on strings");
        }

        switch (op)
        {
            case "==": return Value.FromBool(Compare(left, right) == 0);
            case "!=": return Value.FromBool(Compare(left, right) != 0);
            case "<": return Value.FromBool(Compare(left, right) < 0);
            case ">": return Value.FromBool(Compare(left, right) > 0);
            case "<=": return Value.FromBool(Compare(left, right) <= 0);
            case ">=": return Value.FromBool(Compare(left, right) >= 0);
            case "^^": return Value.FromBool(Truthy(left, null) != Truthy(right, null));
        }

        if (left.Kind == ValueKind.Float || right.Kind == ValueKind.Float)
        {
            double a = ToDouble(left), b = ToDouble(right);
            switch (op)
            {
                case "+": return Value.FromFloat(a + b);
                case "-": return Value.FromFloat(a - b);
                case "*": return Value.FromFloat(a * b);
                case "/": return Value.FromFloat(a / b);
                case "%": return Value.FromFloat(a % b);
            }
            throw Fail($"'{op}' cannot be used on floating point numbers");
        }

        Int128 x = ToInteger(left), y = ToInteger(right);
        bool unsigned = left.Unsigned || right.Unsigned;
        switch (op)
        {
            case "+": return Value.FromInt(unchecked(x + y), unsigned);
            case "-": return Value.FromInt(unchecked(x - y), unsigned);
            case "*": return Value.FromInt(unchecked(x * y), unsigned);
            case "/":
                if (y == 0) throw Fail("division by zero");
                return unsigned ? Value.FromUnsigned((UInt128)x / (UInt128)y) : Value.FromInt(x / y);
            case "%":
                if (y == 0) throw Fail("division by zero");
                return unsigned ? Value.FromUnsigned((UInt128)x % (UInt128)y) : Value.FromInt(x % y);
            case "&": return Value.FromInt(x & y, unsigned);
            case "|": return Value.FromInt(x | y, unsigned);
            case "^": return Value.FromInt(x ^ y, unsigned);
            case "<<": return y >= 128 ? Value.FromInt(0, unsigned) : Value.FromInt(x << (int)y, unsigned);
            case ">>":
                if (y >= 128) return Value.FromInt(0, unsigned);
                return unsigned ? Value.FromUnsigned((UInt128)x >> (int)y) : Value.FromInt(x >> (int)y);
        }
        throw Fail($"unknown operator '{op}'");
    }

    private Value EvalAssign(AssignExpr assign, Scope scope)
    {
        Value value = Resolve(Eval(assign.Value, scope), assign.Value);
        if (assign.Op != "=")
        {
            Value current = Resolve(Eval(assign.Target, scope), assign.Target);
            value = BinaryOp(assign.Op[..^1], current, value);
        }

        switch (assign.Target)
        {
            case CursorExpr:
                _cursor = unchecked((ulong)(long)ToInteger(value));
                return value;

            case PathExpr { Parts.Count: 1 } path:
            {
                if (!scope.TryGetVariable(path.Parts[0], out Value existing, out Scope? owner))
                    throw Fail($"unknown name '{path.Parts[0]}'", path);
                if (existing.Kind == ValueKind.Pattern) throw Fail($"'{path.Parts[0]}' is part of the data and cannot be assigned", path);
                owner!.Vars[path.Parts[0]] = KeepKind(existing, value);
                if (owner.This is { } self && self.Locals.ContainsKey(path.Parts[0])) self.Locals[path.Parts[0]] = owner.Vars[path.Parts[0]];
                return value;
            }

            case MemberExpr member:
            {
                Value target = Eval(member.Target, scope);
                if (target.Pattern is StructPattern s && s.Locals.ContainsKey(member.Name))
                {
                    s.Locals[member.Name] = KeepKind(s.Locals[member.Name], value);
                    return value;
                }
                throw Fail($"'{member.Name}' cannot be assigned", member);
            }
        }
        throw Fail("cannot assign to this", assign);
    }

    // A variable keeps the number type it was made with (a u8 stays within a byte on assignment is not enforced, but strings stay strings).
    private static Value KeepKind(Value existing, Value value) => existing.Kind == ValueKind.String && value.Kind != ValueKind.String ? Value.FromString(value.ToString()) : value;

    // ---- sizeof / addressof / typenameof ----

    private Value EvalIntrinsic(IntrinsicExpr e, Scope scope)
    {
        // A type name wins unless there is a variable of that name.
        TypeRef? type = e.Type;
        if (type is null && e.Operand is PathExpr path && !(path.Parts.Count == 1 && scope.TryGetVariable(path.Parts[0], out _, out _) || path.Parts is ["this"] or ["parent"]))
        {
            TypeRef candidate = new() { Path = path.Parts, Line = path.Line, Column = path.Column };
            if (TryResolveType(candidate, scope, out _)) type = candidate;
        }
        if (type is null && e.Operand is TypeExpr typeExpr) type = typeExpr.Type;

        if (type is not null)
        {
            ResolvedType resolved = ResolveType(type, scope);
            switch (e.Name)
            {
                case "sizeof": return Value.FromUnsigned(SizeOfType(resolved, scope));
                case "typenameof": return Value.FromString(DisplayTypeName(resolved));
                default: throw Fail("addressof needs a value, not a type", e);
            }
        }

        Value operand = Eval(e.Operand!, scope);
        Pattern? pattern = operand.Pattern;
        switch (e.Name)
        {
            case "sizeof":
                if (pattern is null) throw Fail("sizeof needs a value that is part of the data", e);
                return Value.FromUnsigned(SizeOfPattern(pattern));
            case "addressof":
                if (pattern is null) throw Fail("addressof needs a value that is part of the data", e);
                return Value.FromUnsigned(pattern.Offset);
            default:
                return Value.FromString(pattern?.TypeName ?? Describe(operand));
        }
    }

    private ulong SizeOfPattern(Pattern pattern)
        => pattern is StructPattern { Building: true } building ? _cursor - building.Offset : pattern.Size;

    private ulong SizeOfType(ResolvedType type, Scope scope)
    {
        switch (type.Def.Kind)
        {
            case TypeKind.Builtin:
                return (ulong)type.Def.Size;
            case TypeKind.Alias:
            {
                UsingDecl alias = (UsingDecl)type.Def.Decl!;
                return SizeOfType(ResolveType(alias.Target!, NamespaceScope(alias.Namespace)), scope);
            }
            case TypeKind.Enum:
            {
                EnumDecl decl = (EnumDecl)type.Def.Decl!;
                return SizeOfType(ResolveType(decl.BaseType, NamespaceScope(decl.Namespace)), scope);
            }
        }

        // A struct or bitfield has no size until it is laid out: place it somewhere harmless and measure.
        ulong savedCursor = _cursor;
        Section savedSection = _section;
        ulong savedCount = _patternCount;
        _quiet++;
        try
        {
            _section = new Section(-1, "sizeof", new byte[1 << 16]);
            _cursor = 0;
            return PlaceType(type, "sizeof", scope).Size;
        }
        finally
        {
            _quiet--;
            _cursor = savedCursor;
            _section = savedSection;
            _patternCount = savedCount;
        }
    }

    // ---- calls ----

    private Value EvalCall(CallExpr call, Scope scope)
    {
        List<Value> args = [];
        foreach (Expr arg in call.Args)
        {
            Value v = Eval(arg, scope);
            // A variadic list handed on as one argument spreads out again.
            if (v.Kind == ValueKind.List && arg is PathExpr) args.AddRange(v.Items!);
            else args.Add(v);
        }

        switch (call.Callee)
        {
            case PathExpr path:
            {
                string joined = path.Joined;
                if (TryFindFunction(joined, scope, out FunctionDecl? fn)) return CallFunction(fn!, args, call);
                if (StdLib.Has(joined)) return CallBuiltin(joined, args, scope, call);

                TypeRef cast = new() { Path = path.Parts, Line = path.Line, Column = path.Column };
                if (TryResolveType(cast, scope, out ResolvedType? resolved) && resolved!.Def.Kind == TypeKind.Builtin && args.Count == 1)
                    return ConvertToDeclared(cast, args[0], scope, new ExprStmt(call));
                throw Fail($"unknown function '{joined}'", call);
            }

            case TypeExpr typeExpr:
                if (args.Count == 1 && typeExpr.Type.Path.Count == 1)
                    return ConvertToDeclared(typeExpr.Type, args[0], scope, new ExprStmt(call));
                throw Fail($"'{typeExpr.Type}' cannot be called", call);

            case MemberExpr member:
            {
                Value target = Eval(member.Target, scope);
                return CallMethod(target, member.Name, args, call);
            }
        }
        throw Fail("this cannot be called", call);
    }

    private Value CallMethod(Value target, string name, List<Value> args, Expr at)
    {
        target = Resolve(target, at);
        if (target.Kind == ValueKind.String)
        {
            switch (name)
            {
                case "length" or "size": return Value.FromUnsigned((ulong)target.Text.Length);
                case "starts_with": return Value.FromBool(target.Text.StartsWith(ToText(args[0]), StringComparison.Ordinal));
                case "ends_with": return Value.FromBool(target.Text.EndsWith(ToText(args[0]), StringComparison.Ordinal));
                case "contains": return Value.FromBool(target.Text.Contains(ToText(args[0]), StringComparison.Ordinal));
            }
        }
        throw Fail($"no function '{name}' on {Describe(target)}", at);
    }

    private bool TryFindFunction(string joined, Scope scope, out FunctionDecl? function)
    {
        string ns = scope.Namespace;
        while (true)
        {
            if (_functions.TryGetValue(Qualify(ns, joined), out function)) return true;
            if (ns.Length == 0) break;
            int cut = ns.LastIndexOf("::", StringComparison.Ordinal);
            ns = cut < 0 ? "" : ns[..cut];
        }
        function = null;
        return false;
    }

    /// <summary>Calls a function by the name an attribute gave it (<c>[[format("name")]]</c>).</summary>
    public Value CallNamedFunction(string name, List<Value> args, Scope scope, Expr? at)
    {
        if (TryFindFunction(name, scope, out FunctionDecl? fn)) return CallFunction(fn!, args, at);
        if (TryFindFunction(name, _global, out fn)) return CallFunction(fn!, args, at);
        if (StdLib.Has(name)) return CallBuiltin(name, args, scope, at);
        throw Fail($"unknown function '{name}'", at ?? new LiteralExpr(Value.Void));
    }

    private Value CallFunction(FunctionDecl fn, List<Value> args, Expr? at)
    {
        if (++_depth > _options.EvalDepth) throw Fail($"functions nested too deeply ({_options.EvalDepth})", at ?? new LiteralExpr(Value.Void));
        _quiet++;
        try
        {
            Scope scope = new(NamespaceScope(fn.Namespace)) { IsFunction = true, Frame = new Frame(), Namespace = fn.Namespace };
            int next = 0;
            foreach (FunctionParam param in fn.Params)
            {
                if (param.IsVariadic)
                {
                    scope.Vars[param.Name] = Value.FromList(args.Skip(next).ToList());
                    next = args.Count;
                    continue;
                }

                Value value;
                if (next < args.Count) value = args[next++];
                else if (param.Default is not null) value = Eval(param.Default, scope);
                else throw Fail($"'{fn.Name}' needs {fn.Params.Count(p => p.Default is null && !p.IsVariadic)} argument(s) but was given {args.Count}", at ?? new LiteralExpr(Value.Void));

                scope.Vars[param.Name] = ConvertToDeclared(param.Type, value, scope, new ExprStmt(at ?? new LiteralExpr(Value.Void)));
            }

            ExecBody(fn.Body, scope, null);
            return scope.Frame!.ReturnValue;
        }
        finally
        {
            _quiet--;
            _depth--;
        }
    }
}
