namespace HexpatSharp;

/// <summary>Recursive-descent parser for the pattern language.</summary>
public sealed class Parser(List<Token> tokens)
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "struct", "union", "enum", "bitfield", "using", "namespace", "fn", "if", "else", "while", "for", "match", "break",
        "continue", "return", "in", "out", "sizeof", "addressof", "typenameof", "true", "false", "padding", "ref", "try", "catch",
    };

    private int _pos;
    private bool _noGreater;
    private bool _noBitOr;
    private string _namespace = "";

    // ---- token helpers ----

    private Token Cur => tokens[_pos];

    private Token PeekAt(int offset) => tokens[Math.Min(_pos + offset, tokens.Count - 1)];

    private Token Next() => tokens[_pos < tokens.Count - 1 ? _pos++ : _pos];

    private bool At(string text) => Cur.Kind is TokenKind.Punct or TokenKind.Ident && Cur.Text == text;

    private bool AtPunct(string text) => Cur.Kind == TokenKind.Punct && Cur.Text == text;

    private bool AtIdent(string text) => Cur.Kind == TokenKind.Ident && Cur.Text == text;

    private bool Eat(string text)
    {
        if (!At(text)) return false;
        Next();
        return true;
    }

    private Exception Error(string message, Token? at = null)
    {
        Token t = at ?? Cur;
        return new HexpatException(message, t.Line, t.Column);
    }

    private Token Expect(string text)
    {
        if (!At(text)) throw Error($"expected '{text}' but found '{Describe(Cur)}'");
        return Next();
    }

    private static string Describe(Token t) => t.Kind == TokenKind.End ? "the end of the file" : t.Text;

    private string ExpectIdent()
    {
        if (Cur.Kind != TokenKind.Ident) throw Error($"expected a name but found '{Describe(Cur)}'");
        return Next().Text;
    }

    // ---- program ----

    public Program ParseProgram()
    {
        Program program = new();
        while (Cur.Kind != TokenKind.End)
        {
            if (Eat(";")) continue;
            program.Items.AddRange(ParseTopLevel());
        }
        return program;
    }

    private List<object> ParseTopLevel()
    {
        if (AtIdent("namespace")) return [ParseNamespace()];
        if (AtIdent("fn")) return [ParseFunction()];
        if (IsTypeDeclStart())
        {
            TypeDecl decl = ParseTypeDecl();
            return [decl];
        }
        Stmt? statement = ParseStatement();
        return statement is null ? [] : [statement];
    }

    private bool IsTypeDeclStart() => Cur.Kind == TokenKind.Ident && Cur.Text is "struct" or "union" or "enum" or "bitfield" or "using";

    private NamespaceDecl ParseNamespace()
    {
        Expect("namespace");
        string name = ExpectIdent();
        while (Eat("::")) name += "::" + ExpectIdent();
        Expect("{");

        string saved = _namespace;
        _namespace = saved.Length > 0 ? saved + "::" + name : name;
        NamespaceDecl ns = new() { Name = name };
        while (!AtPunct("}") && Cur.Kind != TokenKind.End)
        {
            if (Eat(";")) continue;
            ns.Items.AddRange(ParseTopLevel());
        }
        Expect("}");
        _namespace = saved;
        Eat(";");
        return ns;
    }

    // ---- declarations ----

    private TypeDecl ParseTypeDecl()
    {
        Token start = Cur;
        TypeDecl decl = start.Text switch
        {
            "struct" or "union" => ParseStruct(),
            "enum" => ParseEnum(),
            "bitfield" => ParseBitfield(),
            _ => ParseUsing(),
        };
        decl.Line = start.Line;
        decl.Namespace = _namespace;
        return decl;
    }

    private List<TemplateParam> ParseTemplateParams()
    {
        List<TemplateParam> result = [];
        if (!AtPunct("<")) return result;
        Next();
        while (!AtPunct(">"))
        {
            if (Cur.Kind == TokenKind.End) throw Error("unterminated template parameter list");
            TemplateParam param = new();
            if (AtIdent("auto"))
            {
                Next();
                param.IsValue = true;
                param.Name = ExpectIdent();
            }
            else
            {
                string first = ExpectIdent();
                if (Cur.Kind == TokenKind.Ident && !AtIdent("auto"))
                {
                    param.IsValue = true; // a typed value parameter: u32 N
                    param.Name = Next().Text;
                }
                else param.Name = first;
            }
            result.Add(param);
            if (!Eat(",")) break;
        }
        Expect(">");
        return result;
    }

    private StructDecl ParseStruct()
    {
        StructDecl decl = new() { IsUnion = Next().Text == "union" };
        decl.Name = ExpectIdent();
        decl.TemplateParams = ParseTemplateParams();

        if (Eat(":"))
        {
            do
            {
                decl.Bases.Add(ParseTypeRef() ?? throw Error("expected a base type"));
            }
            while (Eat(","));
        }

        if (AtPunct(";"))
        {
            // A forward declaration written like a struct.
            Next();
            return decl;
        }

        Expect("{");
        decl.Body = ParseBlockBody();
        decl.Attributes = ParseAttributes();
        Eat(";");
        return decl;
    }

    private List<Stmt> ParseBlockBody()
    {
        List<Stmt> body = [];
        while (!AtPunct("}"))
        {
            if (Cur.Kind == TokenKind.End) throw Error("missing '}'");
            if (Eat(";")) continue;
            Stmt? statement = ParseStatement();
            if (statement is not null) body.Add(statement);
        }
        Expect("}");
        return body;
    }

    private EnumDecl ParseEnum()
    {
        Expect("enum");
        EnumDecl decl = new() { Name = ExpectIdent() };
        Expect(":");
        decl.BaseType = ParseTypeRef() ?? throw Error("expected the enum's underlying type");
        Expect("{");
        while (!AtPunct("}"))
        {
            if (Cur.Kind == TokenKind.End) throw Error("missing '}' of enum");
            EnumEntry entry = new() { Name = ExpectIdent() };
            if (Eat("="))
            {
                entry.Value = ParseExpression();
                if (Eat("...")) entry.RangeEnd = ParseExpression();
            }
            decl.Entries.Add(entry);
            if (!Eat(",")) break;
        }
        Expect("}");
        decl.Attributes = ParseAttributes();
        Eat(";");
        return decl;
    }

    private BitfieldDecl ParseBitfield()
    {
        Expect("bitfield");
        BitfieldDecl decl = new() { Name = ExpectIdent() };
        decl.TemplateParams = ParseTemplateParams();
        Expect("{");
        decl.Items = ParseBitfieldItems();
        decl.Attributes = ParseAttributes();
        Eat(";");
        return decl;
    }

    private List<BitfieldItem> ParseBitfieldItems()
    {
        List<BitfieldItem> items = [];
        while (!AtPunct("}"))
        {
            if (Cur.Kind == TokenKind.End) throw Error("missing '}' of bitfield");
            if (Eat(";")) continue;

            int line = Cur.Line;
            if (AtIdent("if"))
            {
                Next();
                Expect("(");
                BitfieldIf branch = new() { Line = line, Condition = ParseExpression() };
                Expect(")");
                Expect("{");
                branch.Then = ParseBitfieldItems();
                if (AtIdent("else"))
                {
                    Next();
                    if (AtIdent("if"))
                    {
                        // An else-if is an if inside the else.
                        int save = _pos;
                        branch.Otherwise = ParseSingleBitfieldIf();
                        _ = save;
                    }
                    else
                    {
                        Expect("{");
                        branch.Otherwise = ParseBitfieldItems();
                    }
                }
                items.Add(branch);
                continue;
            }

            if (AtIdent("padding") && PeekAt(1).Is(":"))
            {
                Next();
                Next();
                items.Add(new BitfieldPadding { Line = line, Bits = ParseExpression() });
                Eat(";");
                continue;
            }

            BitfieldField field = new() { Line = line };
            if (Cur.Kind == TokenKind.Ident && PeekAt(1).Is(":"))
            {
                field.Name = Next().Text;
            }
            else
            {
                field.Type = ParseTypeRef() ?? throw Error("expected a bitfield member");
                field.Name = ExpectIdent();
            }
            Expect(":");
            field.Bits = ParseExpression();
            field.Attributes = ParseAttributes();
            Eat(";");
            items.Add(field);
        }
        Expect("}");
        return items;
    }

    private List<BitfieldItem> ParseSingleBitfieldIf()
    {
        // Parses "if (...) { ... } [else ...]" as one item, and returns it as the contents of an else.
        int line = Cur.Line;
        Expect("if");
        Expect("(");
        BitfieldIf branch = new() { Line = line, Condition = ParseExpression() };
        Expect(")");
        Expect("{");
        branch.Then = ParseBitfieldItems();
        if (AtIdent("else"))
        {
            Next();
            if (AtIdent("if")) branch.Otherwise = ParseSingleBitfieldIf();
            else
            {
                Expect("{");
                branch.Otherwise = ParseBitfieldItems();
            }
        }
        return [branch];
    }

    private UsingDecl ParseUsing()
    {
        Expect("using");
        UsingDecl decl = new() { Name = ExpectIdent() };
        decl.TemplateParams = ParseTemplateParams();
        if (Eat("="))
            decl.Target = ParseTypeRef() ?? throw Error("expected the type being aliased");
        decl.Attributes = ParseAttributes();
        Eat(";");
        return decl;
    }

    private FunctionDecl ParseFunction()
    {
        Token start = Expect("fn");
        FunctionDecl fn = new() { Name = ExpectIdent(), Namespace = _namespace, Line = start.Line };
        Expect("(");
        while (!AtPunct(")"))
        {
            FunctionParam param = new();
            if (AtIdent("ref")) Next();
            TypeRef type = ParseTypeRef() ?? throw Error("expected a parameter type");
            param.Type = type;
            if (Eat("..."))
            {
                param.IsVariadic = true;
            }
            param.Name = ExpectIdent();
            if (Eat("=")) param.Default = ParseExpression();
            fn.Params.Add(param);
            if (!Eat(",")) break;
        }
        Expect(")");
        Expect("{");
        fn.Body = ParseBlockBody();
        Eat(";");
        return fn;
    }

    // ---- attributes ----

    private bool AtAttributeStart() => AtPunct("[") && PeekAt(1).Is("[") && PeekAt(1).Kind == TokenKind.Punct;

    private List<Attribute> ParseAttributes()
    {
        List<Attribute> attributes = [];
        while (AtAttributeStart())
        {
            Next();
            Next();
            while (true)
            {
                string name = ExpectIdent();
                while (Eat("::")) name += "::" + ExpectIdent();
                List<Expr> args = [];
                if (Eat("("))
                {
                    while (!AtPunct(")"))
                    {
                        args.Add(ParseExpression());
                        if (!Eat(",")) break;
                    }
                    Expect(")");
                }
                attributes.Add(new Attribute(name, args));
                if (!Eat(",")) break;
            }
            Expect("]");
            Expect("]");
        }
        return attributes;
    }

    // ---- types ----

    /// <summary>A type name with optional endianness prefix and template arguments, or null (position unchanged) if the tokens aren't one.</summary>
    private TypeRef? ParseTypeRef()
    {
        int save = _pos;
        TypeRef type = new() { Line = Cur.Line, Column = Cur.Column };

        if (Cur.Kind == TokenKind.Ident && Cur.Text is "be" or "le" && PeekAt(1).Kind == TokenKind.Ident && !Reserved.Contains(PeekAt(1).Text))
        {
            type.Endian = Cur.Text == "be" ? EndianOverride.Big : EndianOverride.Little;
            Next();
        }

        if (Cur.Kind != TokenKind.Ident || Reserved.Contains(Cur.Text) && Cur.Text != "padding")
        {
            _pos = save;
            return null;
        }

        type.Path.Add(Next().Text);
        while (AtPunct("::") && PeekAt(1).Kind == TokenKind.Ident)
        {
            Next();
            type.Path.Add(Next().Text);
        }

        if (AtPunct("<"))
        {
            int beforeArgs = _pos;
            if (!TryParseTemplateArgs(type.TemplateArgs))
            {
                _pos = beforeArgs;
                type.TemplateArgs.Clear();
            }
        }
        return type;
    }

    private bool TryParseTemplateArgs(List<Expr> args)
    {
        Expect("<");
        bool savedGreater = _noGreater;
        _noGreater = true;
        try
        {
            while (!AtPunct(">"))
            {
                if (Cur.Kind == TokenKind.End || AtPunct(";") || AtPunct("{") || AtPunct("}")) return false;
                int save = _pos;
                TypeRef? type = ParseTypeRef();
                if (type is not null && (AtPunct(",") || AtPunct(">")))
                    args.Add(new TypeExpr(type));
                else
                {
                    _pos = save;
                    try
                    {
                        args.Add(ParseExpression());
                    }
                    catch (HexpatException)
                    {
                        return false;
                    }
                }
                if (!Eat(",")) break;
            }
            if (!AtPunct(">")) return false;
            Next();
            return true;
        }
        finally
        {
            _noGreater = savedGreater;
        }
    }

    // ---- statements ----

    private Stmt? ParseStatement()
    {
        Token start = Cur;
        Stmt? statement = ParseStatementCore();
        if (statement is not null)
        {
            statement.Line = start.Line;
            statement.Column = start.Column;
        }
        return statement;
    }

    private Stmt? ParseStatementCore()
    {
        if (AtPunct(";"))
        {
            Next();
            return null;
        }

        if (AtPunct("{"))
        {
            Next();
            return new BlockStmt(ParseBlockBody());
        }

        if (Cur.Kind == TokenKind.Ident)
        {
            switch (Cur.Text)
            {
                case "if": return ParseIf();
                case "while": return ParseWhile();
                case "for": return ParseFor();
                case "match": return ParseMatch();
                case "break":
                    Next();
                    Eat(";");
                    return new BreakStmt();
                case "continue":
                    Next();
                    Eat(";");
                    return new ContinueStmt();
                case "return":
                {
                    Next();
                    Expr? value = AtPunct(";") ? null : ParseExpression();
                    Eat(";");
                    return new ReturnStmt(value);
                }
                case "padding" when PeekAt(1).Is("["):
                {
                    Next();
                    Next();
                    Expr size = ParseExpression();
                    Expect("]");
                    Eat(";");
                    return new PaddingStmt(size);
                }
                case "struct" or "union" or "enum" or "bitfield" or "using":
                    return new TypeDeclStmt(ParseTypeDecl());
                case "fn":
                    throw Error("functions can only be declared outside of structs");
                case "try":
                case "catch":
                    throw Error($"'{Cur.Text}' is not supported");
            }
        }

        int save = _pos;
        Stmt? declaration = TryParseDeclaration();
        if (declaration is not null) return declaration;
        _pos = save;

        Expr expression = ParseExpressionOrAssignment();
        Eat(";");
        return new ExprStmt(expression);
    }

    private Stmt ParseBody()
    {
        Stmt? body = ParseStatement();
        return body ?? new BlockStmt([]);
    }

    private IfStmt ParseIf()
    {
        Expect("if");
        Expect("(");
        Expr condition = ParseExpression();
        Expect(")");
        Stmt then = ParseBody();
        Stmt? otherwise = null;
        if (AtIdent("else"))
        {
            Next();
            otherwise = ParseBody();
        }
        return new IfStmt(condition, then, otherwise);
    }

    private WhileStmt ParseWhile()
    {
        Expect("while");
        Expect("(");
        Expr condition = ParseExpression();
        Expect(")");
        return new WhileStmt(condition, ParseBody());
    }

    private ForStmt ParseFor()
    {
        Expect("for");
        Expect("(");
        Stmt? init = null;
        if (!AtPunct(",") && !AtPunct(";"))
        {
            int save = _pos;
            init = TryParseDeclaration(inFor: true);
            if (init is null)
            {
                _pos = save;
                init = new ExprStmt(ParseExpressionOrAssignment());
            }
        }
        if (!Eat(",")) Expect(";");
        Expr? condition = AtPunct(",") || AtPunct(";") ? null : ParseExpression();
        if (!Eat(",")) Expect(";");
        Expr? step = AtPunct(")") ? null : ParseExpressionOrAssignment();
        Expect(")");
        return new ForStmt(init, condition, step, ParseBody());
    }

    private MatchStmt ParseMatch()
    {
        Expect("match");
        Expect("(");
        List<Expr> scrutinees = [];
        do
        {
            scrutinees.Add(ParseExpression());
        }
        while (Eat(","));
        Expect(")");
        Expect("{");

        List<MatchArm> arms = [];
        while (!AtPunct("}"))
        {
            if (Cur.Kind == TokenKind.End) throw Error("missing '}' of match");
            MatchArm arm = new();
            Expect("(");
            do
            {
                List<MatchPattern> alternatives = [];
                do
                {
                    alternatives.Add(ParseMatchPattern());
                }
                while (Eat("|"));
                arm.Patterns.Add(alternatives);
            }
            while (Eat(","));
            Expect(")");
            Expect(":");
            arm.Body = ParseBody();
            arms.Add(arm);
        }
        Expect("}");
        Eat(";");
        return new MatchStmt(scrutinees, arms);
    }

    private MatchPattern ParseMatchPattern()
    {
        if (AtIdent("_"))
        {
            Next();
            return new MatchPattern { Wildcard = true };
        }

        bool saved = _noBitOr;
        _noBitOr = true;
        try
        {
            Expr low = ParseExpression();
            Expr? high = null;
            if (Eat("...")) high = ParseExpression();
            return new MatchPattern { Low = low, High = high };
        }
        finally
        {
            _noBitOr = saved;
        }
    }

    // ---- variable declarations ----

    /// <summary>
    /// <c>Type name[...] @ offset = value [[attributes]];</c> and pointers <c>Type *name : size</c>. Null (the caller restores the
    /// position) when the tokens are an expression instead.
    /// </summary>
    private Stmt? TryParseDeclaration(bool inFor = false)
    {
        int save = _pos;
        if (AtIdent("const")) Next();
        TypeRef? type = ParseTypeRef();
        if (type is null) return null;

        VarDecl first = new() { Type = type };
        if (AtPunct("*") && PeekAt(1).Kind == TokenKind.Ident)
        {
            Next();
            first.IsPointer = true;
            first.Name = Next().Text;
            Expect(":");
            first.PointerSize = ParseTypeRef() ?? throw Error("expected the pointer's size type");
        }
        else if (Cur.Kind == TokenKind.Ident && !Reserved.Contains(Cur.Text))
        {
            first.Name = Next().Text;
        }
        else if (AtPunct(";") && !inFor)
        {
            first.Name = ""; // a type placed without a name, e.g. std::mem::AlignTo<4>;
        }
        else
        {
            _pos = save;
            return null;
        }

        List<VarDecl> declarations = [first];
        ParseDeclaratorTail(first, inFor);
        while (AtPunct(",") && !inFor && PeekAt(1).Kind == TokenKind.Ident && PeekAt(2).Kind == TokenKind.Punct)
        {
            Next();
            VarDecl extra = new() { Type = type, Name = Next().Text };
            ParseDeclaratorTail(extra, inFor);
            declarations.Add(extra);
        }

        if (!inFor) Eat(";");
        if (declarations.Count == 1) return first;
        return new BlockStmt([.. declarations.Cast<Stmt>()]) { Line = first.Line };
    }

    private void ParseDeclaratorTail(VarDecl decl, bool inFor)
    {
        if (AtPunct("[") && !AtAttributeStart())
        {
            Next();
            if (AtPunct("]"))
            {
                decl.ArrayKind = ArrayKind.Unsized;
            }
            else if (AtIdent("while"))
            {
                Next();
                Expect("(");
                decl.ArrayKind = ArrayKind.While;
                decl.ArrayCondition = ParseExpression();
                Expect(")");
            }
            else
            {
                decl.ArrayKind = ArrayKind.Sized;
                decl.ArraySize = ParseExpression();
            }
            Expect("]");
        }

        if (Eat("@"))
        {
            decl.At = ParseExpression();
            if (AtIdent("in"))
            {
                Next();
                decl.InSection = ParseExpression();
            }
        }

        if (AtIdent("in") || AtIdent("out"))
        {
            Next();
            decl.IsInOut = true;
        }

        if (Eat("="))
            decl.Init = ParseExpression();

        decl.Attributes = ParseAttributes();
    }

    // ---- expressions ----

    private Expr ParseExpressionOrAssignment()
    {
        Expr left = ParseExpression();
        string? op = PeekAssignmentOperator(out int tokenCount);
        if (op is null) return left;

        Token at = Cur;
        for (int i = 0; i < tokenCount; i++) Next();
        Expr value = ParseExpression();
        return new AssignExpr(op, left, value) { Line = at.Line, Column = at.Column };
    }

    private string? PeekAssignmentOperator(out int tokenCount)
    {
        tokenCount = 1;
        if (Cur.Kind != TokenKind.Punct) return null;
        switch (Cur.Text)
        {
            case "=" or "+=" or "-=" or "*=" or "/=" or "%=" or "|=" or "&=" or "^=" or "<<=":
                return Cur.Text;
            case ">" when PeekAt(1).Is(">") && PeekAt(2).Is("=") && Touching(0, 1) && Touching(1, 2):
                tokenCount = 3;
                return ">>=";
        }
        return null;
    }

    private bool Touching(int a, int b) => PeekAt(b).Position == PeekAt(a).Position + 1;

    public Expr ParseExpression() => ParseTernary();

    private Expr ParseTernary()
    {
        Expr condition = ParseBinary(0);
        if (AtPunct("?"))
        {
            Token at = Next();
            bool savedGreater = _noGreater, savedOr = _noBitOr;
            _noGreater = false;
            _noBitOr = false;
            Expr then = ParseTernary();
            _noGreater = savedGreater;
            _noBitOr = savedOr;
            Expect(":");
            Expr otherwise = ParseTernary();
            return new TernaryExpr(condition, then, otherwise) { Line = at.Line, Column = at.Column };
        }
        return condition;
    }

    private static readonly string[][] Levels =
    [
        ["||"], ["^^"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", ">", "<=", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"],
    ];

    // The binary operator at the current position (merging touching '>' tokens), and how many tokens it takes.
    private string? PeekBinaryOperator(out int tokenCount)
    {
        tokenCount = 1;
        if (Cur.Kind != TokenKind.Punct) return null;
        string text = Cur.Text;
        if (text == ">")
        {
            if (_noGreater) return null;
            if (PeekAt(1).Is(">") && Touching(0, 1))
            {
                if (PeekAt(2).Is("=") && Touching(1, 2)) return null; // >>= is an assignment
                tokenCount = 2;
                return ">>";
            }
            if (PeekAt(1).Is("=") && Touching(0, 1) && !PeekAt(2).Is("="))
            {
                tokenCount = 2;
                return ">=";
            }
        }
        if (text == "|" && _noBitOr) return null;
        return text;
    }

    private Expr ParseBinary(int level)
    {
        if (level >= Levels.Length) return ParseUnary();

        Expr left = ParseBinary(level + 1);
        while (true)
        {
            string? op = PeekBinaryOperator(out int tokenCount);
            if (op is null || Array.IndexOf(Levels[level], op) < 0) return left;
            Token at = Cur;
            for (int i = 0; i < tokenCount; i++) Next();
            Expr right = ParseBinary(level + 1);
            left = new BinaryExpr(op, left, right) { Line = at.Line, Column = at.Column };
        }
    }

    private Expr ParseUnary()
    {
        if (Cur.Kind == TokenKind.Punct && Cur.Text is "-" or "+" or "!" or "~")
        {
            Token op = Next();
            Expr operand = ParseUnary();
            return new UnaryExpr(op.Text, operand) { Line = op.Line, Column = op.Column };
        }
        return ParsePostfix(ParsePrimary());
    }

    private Expr ParsePostfix(Expr expr)
    {
        while (true)
        {
            Token at = Cur;
            if (AtPunct(".") && PeekAt(1).Kind == TokenKind.Ident)
            {
                Next();
                expr = new MemberExpr(expr, Next().Text) { Line = at.Line, Column = at.Column };
            }
            else if (AtPunct("[") && !AtAttributeStart())
            {
                Next();
                bool savedGreater = _noGreater, savedOr = _noBitOr;
                _noGreater = false;
                _noBitOr = false;
                Expr index = ParseExpression();
                _noGreater = savedGreater;
                _noBitOr = savedOr;
                Expect("]");
                expr = new IndexExpr(expr, index) { Line = at.Line, Column = at.Column };
            }
            else if (AtPunct("(") && expr is PathExpr or MemberExpr or TypeExpr)
            {
                Next();
                bool savedGreater = _noGreater, savedOr = _noBitOr;
                _noGreater = false;
                _noBitOr = false;
                List<Expr> args = [];
                while (!AtPunct(")"))
                {
                    args.Add(ParseExpression());
                    if (!Eat(",")) break;
                }
                _noGreater = savedGreater;
                _noBitOr = savedOr;
                Expect(")");
                expr = new CallExpr(expr, args) { Line = at.Line, Column = at.Column };
            }
            else return expr;
        }
    }

    private Expr ParsePrimary()
    {
        Token t = Cur;
        switch (t.Kind)
        {
            case TokenKind.Integer:
                Next();
                return new LiteralExpr(Value.FromInt(t.IntValue, t.Unsigned)) { Line = t.Line, Column = t.Column };
            case TokenKind.Float:
                Next();
                return new LiteralExpr(Value.FromFloat(t.FloatValue)) { Line = t.Line, Column = t.Column };
            case TokenKind.String:
                Next();
                return new LiteralExpr(Value.FromString(t.StringValue)) { Line = t.Line, Column = t.Column };
            case TokenKind.Char:
                Next();
                return new LiteralExpr(Value.FromChar((int)t.IntValue)) { Line = t.Line, Column = t.Column };
            case TokenKind.Punct when t.Text == "$":
                Next();
                return new CursorExpr { Line = t.Line, Column = t.Column };
            case TokenKind.Punct when t.Text == "(":
            {
                Next();
                bool savedGreater = _noGreater, savedOr = _noBitOr;
                _noGreater = false;
                _noBitOr = false;
                Expr inner = ParseExpression();
                _noGreater = savedGreater;
                _noBitOr = savedOr;
                Expect(")");
                return inner;
            }
            case TokenKind.Ident:
                return ParseIdentifierExpression();
        }
        throw Error($"unexpected '{Describe(t)}'");
    }

    private Expr ParseIdentifierExpression()
    {
        Token t = Cur;
        if (t.Text is "true" or "false")
        {
            Next();
            return new LiteralExpr(Value.FromBool(t.Text == "true")) { Line = t.Line, Column = t.Column };
        }

        if (t.Text is "sizeof" or "addressof" or "typenameof" && PeekAt(1).Is("("))
        {
            Next();
            Next();
            Expr? operand = null;
            TypeRef? type = null;
            int save = _pos;
            try
            {
                operand = ParseExpression();
                if (!AtPunct(")")) throw Error("expected ')'");
            }
            catch (HexpatException)
            {
                _pos = save;
                operand = null;
                type = ParseTypeRef() ?? throw Error($"expected a value or type in {t.Text}()");
            }
            Expect(")");
            return new IntrinsicExpr(t.Text, type, operand) { Line = t.Line, Column = t.Column };
        }

        List<string> parts = [Next().Text];
        while (AtPunct("::") && PeekAt(1).Kind == TokenKind.Ident)
        {
            Next();
            parts.Add(Next().Text);
        }

        // A templated type used as an expression, e.g. a cast or a call: Foo<u8>(x).
        if (AtPunct("<") && !_noGreater)
        {
            int save = _pos;
            List<Expr> args = [];
            bool savedLess = _noGreater;
            if (TryParseTemplateArgs(args) && AtPunct("("))
            {
                _noGreater = savedLess;
                TypeRef type = new() { Path = parts, TemplateArgs = args, Line = t.Line, Column = t.Column };
                return new TypeExpr(type) { Line = t.Line, Column = t.Column };
            }
            _pos = save;
        }

        return new PathExpr(parts) { Line = t.Line, Column = t.Column };
    }
}
