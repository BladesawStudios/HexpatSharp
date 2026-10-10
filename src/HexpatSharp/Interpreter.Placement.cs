namespace HexpatSharp;

internal sealed partial class Interpreter
{
    private string DisplayTypeName(ResolvedType type)
    {
        string name = type.Def.Name;
        if (type.Ref.TemplateArgs.Count == 0) return name;
        return name + "<" + string.Join(", ", type.Ref.TemplateArgs.Select(a => a switch
        {
            TypeExpr t => t.Type.ToString(),
            LiteralExpr l => l.Value.ToString(),
            PathExpr p => p.Joined,
            _ => "?",
        })) + ">";
    }

    private Pattern PlaceType(ResolvedType type, string name, Scope scope)
    {
        Endian savedEndian = _endian;
        if (type.Endian != EndianOverride.None) _endian = type.Endian == EndianOverride.Big ? Endian.Big : Endian.Little;
        try
        {
            switch (type.Def.Kind)
            {
                case TypeKind.Builtin:
                    return PlaceScalar(type.Def.Name, type.Def.Scalar, type.Def.Size, name);

                case TypeKind.Alias:
                {
                    UsingDecl alias = (UsingDecl)type.Def.Decl!;
                    Scope aliasScope = NamespaceScope(alias.Namespace);
                    if (alias.TemplateParams.Count > 0)
                    {
                        aliasScope = new Scope(aliasScope);
                        BindTemplateArguments(alias.TemplateParams, type.Ref.TemplateArgs, aliasScope, type.Scope, alias.Line);
                    }
                    ResolvedType target = ResolveType(alias.Target!, aliasScope);
                    if (target.Endian == EndianOverride.None) target.Endian = type.Endian;
                    Pattern p = PlaceType(target, name, aliasScope);
                    p.TypeName = alias.Name;
                    return p;
                }

                case TypeKind.Struct:
                    return PlaceStruct(type, name, scope);

                case TypeKind.Enum:
                    return PlaceEnum(type, name);

                case TypeKind.Bitfield:
                    return PlaceBitfield(type, name, scope);
            }
            throw Fail($"cannot place a value of type '{type.Def.Name}'", type.Ref.Line, type.Ref.Column);
        }
        finally
        {
            _endian = savedEndian;
        }
    }

    private ScalarPattern PlaceScalar(string typeName, ScalarKind kind, int size, string name)
    {
        // Past the end of the data is allowed (a section may be absent and marked by an offset of -1); only reading it fails.
        CountPattern();

        ScalarPattern p = new()
        {
            Name = name, TypeName = typeName, Kind = kind, Offset = _cursor, Size = (ulong)size,
            Endian = _endian, Source = _section, Parent = _building.Count > 0 ? _building.Peek() : null,
        };
        _cursor += (ulong)size;
        return p;
    }

    // ---- templates ----

    private void BindTemplateArguments(List<TemplateParam> parameters, List<Expr> arguments, Scope target, Scope callerScope, int line)
    {
        target.TypeParams ??= new Dictionary<string, TypeBinding>(StringComparer.Ordinal);
        for (int i = 0; i < parameters.Count; i++)
        {
            TemplateParam param = parameters[i];
            if (i >= arguments.Count) throw Fail($"template argument '{param.Name}' is missing", line);
            Expr argument = arguments[i];

            if (param.IsValue)
            {
                Value value = Resolve(Eval(argument is TypeExpr te ? new PathExpr(te.Type.Path) : argument, callerScope), argument);
                target.Vars[param.Name] = value;
            }
            else
            {
                TypeRef reference = argument switch
                {
                    TypeExpr t => t.Type,
                    PathExpr p => new TypeRef { Path = p.Parts, Line = p.Line, Column = p.Column },
                    _ => throw Fail($"template argument '{param.Name}' must be a type", argument),
                };
                target.TypeParams[param.Name] = new TypeBinding(reference, callerScope);
            }
        }
    }

    // ---- structs and unions ----

    private Pattern PlaceStruct(ResolvedType type, string name, Scope callerScope)
    {
        StructDecl decl = (StructDecl)type.Def.Decl!;
        if (decl.Body.Count == 0 && decl.Bases.Count == 0 && decl.Attributes.Count == 0 && IsForwardOnly(decl))
            throw Fail($"'{decl.Name}' was declared but never defined", decl.Line);

        if (++_depth > _options.EvalDepth)
            throw Fail($"nested too deeply ({_options.EvalDepth}); raise it with '#pragma eval_depth'", decl.Line);
        CountPattern(decl.Line);

        StructPattern pattern = new()
        {
            Name = name, TypeName = DisplayTypeName(type), IsUnion = decl.IsUnion, Offset = _cursor, Endian = _endian,
            Source = _section, Parent = _building.Count > 0 ? _building.Peek() : null, Building = true,
        };
        Scope scope = new(NamespaceScope(decl.Namespace)) { This = pattern, ParentPattern = pattern.Parent, Namespace = decl.Namespace };
        BindTemplateArguments(decl.TemplateParams, type.Ref.TemplateArgs, scope, type.Scope, decl.Line);

        ulong start = _cursor;
        _building.Push(pattern);
        try
        {
            ExecStructBody(decl, scope, pattern, type);
        }
        catch (HexpatException)
        {
            // Keep what was placed before the error: the unfinished struct joins its parent, or the results.
            pattern.Size = _cursor > start ? _cursor - start : 0;
            pattern.Building = false;
            KeepPartial(pattern);
            throw;
        }
        finally
        {
            _building.Pop();
            _depth--;
        }

        ulong end = decl.IsUnion ? Math.Max(_unionEnds.GetValueOrDefault(pattern), start) : _cursor;
        _unionEnds.Remove(pattern);
        pattern.Size = end >= start ? end - start : 0;
        pattern.Building = false;
        if (decl.IsUnion) _cursor = start + pattern.Size;

        ApplyDeclAttributes(pattern, decl, scope);
        return pattern;
    }

    private static bool IsForwardOnly(StructDecl decl) => false;

    // Evaluation stopped inside this pattern; attach it where it would have gone so the tree shows how far it got.
    private void KeepPartial(Pattern pattern)
    {
        if (_quiet > 0) return;
        if (pattern.Parent is StructPattern parent)
        {
            if (!parent.Members.Contains(pattern)) parent.Add(pattern);
        }
        else if (pattern.Source == _main && !_result.Patterns.Contains(pattern))
        {
            _result.Patterns.Add(pattern);
        }
    }

    private void ExecStructBody(StructDecl decl, Scope scope, StructPattern pattern, ResolvedType type)
    {
        foreach (TypeRef baseRef in decl.Bases)
        {
            ResolvedType baseType = ResolveType(baseRef, NamespaceScope(decl.Namespace));
            if (baseType.Def.Kind != TypeKind.Struct)
                throw Fail($"'{baseRef}' cannot be inherited from", baseRef.Line, baseRef.Column);
            StructDecl baseDecl = (StructDecl)baseType.Def.Decl!;
            Scope baseScope = new(scope) { Namespace = baseDecl.Namespace };
            BindTemplateArguments(baseDecl.TemplateParams, baseRef.TemplateArgs, baseScope, NamespaceScope(decl.Namespace), decl.Line);
            ExecStructBody(baseDecl, baseScope, pattern, baseType);

            // The base's members are the struct's own: its body reads them by name.
            foreach (var (memberName, memberValue) in baseScope.Vars)
                if (baseDecl.TemplateParams.All(t => t.Name != memberName)) scope.Vars[memberName] = memberValue;
        }

        ExecBody(decl.Body, scope, pattern);
    }

    // ---- enums ----

    private EnumDef GetEnum(TypeDef def)
    {
        if (def.EnumDef is not null) return def.EnumDef;

        EnumDecl decl = (EnumDecl)def.Decl!;
        EnumDef result = new() { Name = decl.Name };
        Scope scope = new(NamespaceScope(decl.Namespace));
        Int128 next = 0;
        foreach (EnumEntry entry in decl.Entries)
        {
            Int128 low = entry.Value is null ? next : ToInteger(Resolve(Eval(entry.Value, scope), entry.Value));
            Int128 high = entry.RangeEnd is null ? low : ToInteger(Resolve(Eval(entry.RangeEnd, scope), entry.RangeEnd));
            result.Entries.Add((entry.Name, low, high));
            scope.Vars[entry.Name] = Value.FromInt(low);
            next = high + 1;
        }
        def.EnumDef = result;
        return result;
    }

    private Pattern PlaceEnum(ResolvedType type, string name)
    {
        EnumDecl decl = (EnumDecl)type.Def.Decl!;
        EnumDef def = GetEnum(type.Def);
        ResolvedType baseType = ResolveType(decl.BaseType, NamespaceScope(decl.Namespace));
        if (baseType.Def.Kind != TypeKind.Builtin)
            throw Fail($"the base type of enum '{decl.Name}' must be a number", decl.Line);

        ScalarPattern underlying = PlaceScalar(baseType.Def.Name, baseType.Def.Scalar, baseType.Def.Size, name);
        return new EnumPattern
        {
            Name = name, TypeName = decl.Name, Def = def, Underlying = underlying, Offset = underlying.Offset, Size = underlying.Size,
            Endian = underlying.Endian, Source = underlying.Source, Parent = underlying.Parent,
        };
    }

    // ---- bitfields ----

    private Pattern PlaceBitfield(ResolvedType type, string name, Scope callerScope)
    {
        BitfieldDecl decl = (BitfieldDecl)type.Def.Decl!;
        CountPattern(decl.Line);

        BitfieldPattern pattern = new()
        {
            Name = name, TypeName = DisplayTypeName(type), Offset = _cursor, Endian = _endian, Source = _section,
            Parent = _building.Count > 0 ? _building.Peek() : null,
        };
        Scope scope = new(NamespaceScope(decl.Namespace)) { ParentPattern = pattern.Parent, Namespace = decl.Namespace };
        BindTemplateArguments(decl.TemplateParams, type.Ref.TemplateArgs, scope, type.Scope, decl.Line);

        // Bits are numbered from the least significant end on little endian data and from the most significant end on big endian
        // data, unless bitfield_order(order, totalBits) says otherwise (and optionally how wide the whole thing is).
        pattern.MostSignificantFirst = pattern.Endian == Endian.Big;
        int? declaredBits = null;
        foreach (Attribute attribute in decl.Attributes)
        {
            if (attribute.Name != "bitfield_order") continue;
            Value order = attribute.Args.Count > 0 ? Evaluate(attribute.Args[0], scope) : Value.Void;
            pattern.MostSignificantFirst = order.Kind == ValueKind.Integer && order.Integer == 1;
            if (attribute.Args.Count > 1 && Evaluate(attribute.Args[1], scope) is { Kind: ValueKind.Integer } bits)
                declaredBits = (int)bits.Integer;
        }

        int position = 0;
        LayoutBitfield(decl.Items, scope, pattern, ref position);
        int totalBits = declaredBits ?? position;
        pattern.Size = (ulong)((totalBits + 7) / 8);
        _cursor = pattern.Offset + pattern.Size;

        ApplyDeclAttributes(pattern, decl, scope);
        return pattern;
    }

    private void LayoutBitfield(List<BitfieldItem> items, Scope scope, BitfieldPattern pattern, ref int position)
    {
        foreach (BitfieldItem item in items)
        {
            switch (item)
            {
                case BitfieldIf branch:
                {
                    bool taken = Truthy(Resolve(Eval(branch.Condition, scope), branch.Condition), branch.Condition);
                    LayoutBitfield(taken ? branch.Then : branch.Otherwise, scope, pattern, ref position);
                    break;
                }

                case BitfieldPadding padding:
                    position += (int)ToInteger(Resolve(Eval(padding.Bits, scope), padding.Bits));
                    break;

                case BitfieldField field:
                {
                    int bits = (int)ToInteger(Resolve(Eval(field.Bits, scope), field.Bits));
                    BitfieldFieldPattern p = new()
                    {
                        Name = field.Name, TypeName = field.Type?.ToString() ?? $"u{bits}", Owner = pattern, BitOffset = position, BitSize = bits,
                        Kind = ScalarKind.Unsigned, Endian = pattern.Endian, Source = pattern.Source, Parent = pattern.Parent,
                        Offset = pattern.Offset + (ulong)(position / 8),
                        Size = (ulong)((position + bits + 7) / 8 - position / 8),
                    };

                    if (field.Type is not null)
                    {
                        ResolvedType typed = ResolveType(field.Type, scope);
                        if (typed.Def.Kind == TypeKind.Enum) p.Enum = GetEnum(typed.Def);
                        else if (typed.Def.Kind == TypeKind.Builtin) p.Kind = typed.Def.Scalar;
                        p.TypeName = DisplayTypeName(typed);
                    }

                    // The bitfield is as wide as what is laid out so far, for the values that later conditions read.
                    pattern.Size = (ulong)((position + bits + 7) / 8);
                    ApplyAttributes(p, field.Attributes, scope);
                    pattern.Fields.Add(p);
                    pattern.ByName[field.Name] = p;
                    position += bits;
                    if (_section.InRange(pattern.Offset, pattern.Size))
                        scope.Vars[field.Name] = BitfieldFieldValue(p);
                    break;
                }
            }
        }
    }

    // ---- arrays ----

    private Pattern PlaceArray(VarDecl d, ResolvedType elementType, Scope scope, StructPattern? owner)
    {
        Endian savedEndian = _endian;
        if (elementType.Endian != EndianOverride.None) _endian = elementType.Endian == EndianOverride.Big ? Endian.Big : Endian.Little;
        try
        {
            return PlaceArrayCore(d, elementType, scope);
        }
        finally
        {
            _endian = savedEndian;
        }
    }

    private bool IsScalarType(ResolvedType type, out ScalarKind kind, out int size, out string name)
    {
        // Follow aliases to a plain number type, so "using Id = u32; Id ids[100000];" does not make 100000 patterns.
        ResolvedType current = type;
        while (current.Def.Kind == TypeKind.Alias)
        {
            UsingDecl alias = (UsingDecl)current.Def.Decl!;
            if (alias.TemplateParams.Count > 0) break;
            current = ResolveType(alias.Target!, NamespaceScope(alias.Namespace));
        }

        if (current.Def.Kind == TypeKind.Builtin)
        {
            kind = current.Def.Scalar;
            size = current.Def.Size;
            name = type.Def.Kind == TypeKind.Alias ? type.Def.Name : current.Def.Name;
            return true;
        }
        kind = default;
        size = 0;
        name = "";
        return false;
    }

    private Pattern PlaceArrayCore(VarDecl d, ResolvedType elementType, Scope scope)
    {
        ulong start = _cursor;
        string typeName = DisplayTypeName(elementType);
        ArrayPattern array = new()
        {
            Name = d.Name, TypeName = typeName, ElementTypeName = typeName, Offset = start, Endian = _endian, Source = _section,
            Parent = _building.Count > 0 ? _building.Peek() : null,
        };

        if (IsScalarType(elementType, out ScalarKind kind, out int size, out string scalarName))
        {
            array.ScalarElement = kind;
            array.ElementSize = (ulong)size;
            array.ElementTypeName = scalarName;
            array.TypeName = scalarName;

            ulong count;
            switch (d.ArrayKind)
            {
                case ArrayKind.Sized:
                    count = ToUnsigned(Eval(d.ArraySize!, scope), d.ArraySize!);
                    break;
                case ArrayKind.While:
                {
                    count = 0;
                    while (Truthy(Resolve(Eval(d.ArrayCondition!, scope), d.ArrayCondition), d.ArrayCondition!))
                    {
                        Step();
                        _cursor += (ulong)size;
                        if (++count > _options.ArrayLimit) throw Fail($"array is longer than {_options.ArrayLimit}; raise it with '#pragma array_limit'", d.Line);
                    }
                    _cursor = start;
                    break;
                }
                default: // unsized: until an element that is all zero (for chars, the end of the string), which is included
                {
                    count = 0;
                    ulong at = start;
                    while (true)
                    {
                        if (!_section.InRange(at, (ulong)size)) break;
                        bool zero = true;
                        for (int i = 0; i < size; i++) zero &= _section.Data[(int)at + i] == 0;
                        at += (ulong)size;
                        count++;
                        if (zero) break;
                        if (count > _options.ArrayLimit) throw Fail($"array is longer than {_options.ArrayLimit}; raise it with '#pragma array_limit'", d.Line);
                    }
                    break;
                }
            }

            if (count > _options.ArrayLimit)
                throw Fail($"array of {count} is longer than {_options.ArrayLimit}; raise it with '#pragma array_limit'", d.Line);
            ulong total = count * (ulong)size;

            CountPattern(d.Line);
            array.Count = count;
            array.Size = total;
            _cursor = start + total;
            return array;
        }

        // Arrays of structs and the like: each element is a pattern.
        ulong limit = _options.ArrayLimit;
        try
        {
            PlaceElements(d, elementType, scope, array, limit);
        }
        catch (HexpatException)
        {
            array.Count = (ulong)array.Elements.Count;
            array.Size = _cursor > start ? _cursor - start : 0;
            KeepPartial(array);
            throw;
        }

        array.Count = (ulong)array.Elements.Count;
        array.Size = _cursor - start;
        return array;
    }

    private void PlaceElements(VarDecl d, ResolvedType elementType, Scope scope, ArrayPattern array, ulong limit)
    {
        switch (d.ArrayKind)
        {
            case ArrayKind.Sized:
            {
                ulong count = ToUnsigned(Eval(d.ArraySize!, scope), d.ArraySize!);
                if (count > limit) throw Fail($"array of {count} is longer than {limit}; raise it with '#pragma array_limit'", d.Line);
                for (ulong i = 0; i < count; i++)
                {
                    Step();
                    array.Elements.Add(PlaceType(elementType, $"[{i}]", scope));
                }
                break;
            }
            case ArrayKind.While:
                while (Truthy(Resolve(Eval(d.ArrayCondition!, scope), d.ArrayCondition), d.ArrayCondition!))
                {
                    Step();
                    if ((ulong)array.Elements.Count >= limit) throw Fail($"array is longer than {limit}; raise it with '#pragma array_limit'", d.Line);
                    array.Elements.Add(PlaceType(elementType, $"[{array.Elements.Count}]", scope));
                }
                break;
            default:
                while (true)
                {
                    Step();
                    if ((ulong)array.Elements.Count >= limit) throw Fail($"array is longer than {limit}; raise it with '#pragma array_limit'", d.Line);
                    ulong at = _cursor;
                    Pattern element = PlaceType(elementType, $"[{array.Elements.Count}]", scope);
                    array.Elements.Add(element);
                    bool zero = true;
                    for (ulong i = at; i < _cursor && zero; i++) zero &= _section.Data[(int)i] == 0;
                    if (zero) break;
                }
                break;
        }
    }

    // ---- pointers ----

    private Pattern PlacePointer(VarDecl d, ResolvedType pointee, Scope scope, StructPattern? owner)
    {
        ResolvedType sizeType = ResolveType(d.PointerSize!, scope);
        if (sizeType.Def.Kind != TypeKind.Builtin)
            throw Fail("the size type of a pointer must be a number", d.Line);

        PointerPattern pattern = new() { Name = d.Name, TypeName = DisplayTypeName(pointee) + "*", Offset = _cursor, Endian = _endian, Source = _section };
        Endian savedEndian = _endian;
        if (sizeType.Endian != EndianOverride.None) _endian = sizeType.Endian == EndianOverride.Big ? Endian.Big : Endian.Little;
        try
        {
            pattern.Pointer = PlaceScalar(sizeType.Def.Name, sizeType.Def.Scalar, sizeType.Def.Size, d.Name);
        }
        finally
        {
            _endian = savedEndian;
        }
        pattern.Size = pattern.Pointer.Size;
        pattern.Parent = pattern.Pointer.Parent;

        ulong afterPointer = _cursor;
        UInt128 raw = pattern.Pointer.ReadRaw();
        ulong baseOffset = 0;
        foreach (Attribute attribute in d.Attributes)
            if (attribute.Name == "pointer_base" && attribute.Args.Count > 0)
                baseOffset = PointerBase(attribute, pattern.Pointer, scope);
        pattern.Address = baseOffset + (ulong)raw;

        _cursor = pattern.Address;
        try
        {
            VarDecl pointeeDecl = new()
            {
                Type = d.Type, Name = d.Name, ArrayKind = d.ArrayKind, ArraySize = d.ArraySize, ArrayCondition = d.ArrayCondition, Line = d.Line,
            };
            pattern.Pointee = pointeeDecl.ArrayKind != ArrayKind.None
                ? PlaceArray(pointeeDecl, pointee, scope, owner)
                : PlaceType(pointee, d.Name, scope);
        }
        finally
        {
            _cursor = afterPointer;
        }
        return pattern;
    }

    private ulong PointerBase(Attribute attribute, ScalarPattern pointer, Scope scope)
    {
        Value arg = Evaluate(attribute.Args[0], scope);
        if (arg.Kind == ValueKind.String)
        {
            Value result = CallNamedFunction(arg.Text, [Value.FromPattern(pointer)], scope, attribute.Args[0]);
            return (ulong)ToInteger(Resolve(result, null));
        }
        return (ulong)ToInteger(arg);
    }

    // ---- sections ----

    private Section SectionOf(Value value, Expr at)
    {
        if (value.Kind == ValueKind.Section && _sections.TryGetValue(value.SectionId, out Section? section)) return section;
        if (value.Kind == ValueKind.Integer && _sections.TryGetValue((int)value.Integer, out section)) return section;
        throw Fail("expected a section", at);
    }
}
