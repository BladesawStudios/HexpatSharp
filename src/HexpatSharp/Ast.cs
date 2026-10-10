namespace HexpatSharp;

// ---- expressions ----

public abstract class Expr
{
    public int Line;
    public int Column;
}

public sealed class LiteralExpr(Value value) : Expr
{
    public Value Value { get; } = value;
}

/// <summary>A name, possibly qualified: <c>x</c>, <c>ns::x</c>, <c>std::mem::eof</c>.</summary>
public sealed class PathExpr(List<string> parts) : Expr
{
    public List<string> Parts { get; } = parts;
    public string Joined => string.Join("::", Parts);
}

public sealed class CursorExpr : Expr;

public sealed class MemberExpr(Expr target, string name) : Expr
{
    public Expr Target { get; } = target;
    public string Name { get; } = name;
}

public sealed class IndexExpr(Expr target, Expr index) : Expr
{
    public Expr Target { get; } = target;
    public Expr Index { get; } = index;
}

public sealed class CallExpr(Expr callee, List<Expr> args) : Expr
{
    public Expr Callee { get; } = callee;
    public List<Expr> Args { get; } = args;
}

public sealed class UnaryExpr(string op, Expr operand) : Expr
{
    public string Op { get; } = op;
    public Expr Operand { get; } = operand;
}

public sealed class BinaryExpr(string op, Expr left, Expr right) : Expr
{
    public string Op { get; } = op;
    public Expr Left { get; } = left;
    public Expr Right { get; } = right;
}

public sealed class TernaryExpr(Expr condition, Expr then, Expr otherwise) : Expr
{
    public Expr Condition { get; } = condition;
    public Expr Then { get; } = then;
    public Expr Otherwise { get; } = otherwise;
}

public sealed class AssignExpr(string op, Expr target, Expr value) : Expr
{
    public string Op { get; } = op;
    public Expr Target { get; } = target;
    public Expr Value { get; } = value;
}

/// <summary><c>sizeof(x)</c>, <c>addressof(x)</c> and <c>typenameof(x)</c>. The operand is a type or an expression.</summary>
public sealed class IntrinsicExpr(string name, TypeRef? type, Expr? operand) : Expr
{
    public string Name { get; } = name;
    public TypeRef? Type { get; } = type;
    public Expr? Operand { get; } = operand;
}

/// <summary>A type used where an expression is expected (a template argument).</summary>
public sealed class TypeExpr(TypeRef type) : Expr
{
    public TypeRef Type { get; } = type;
}

// ---- types ----

public enum EndianOverride { None, Little, Big }

public sealed class TypeRef
{
    public List<string> Path = [];
    public List<Expr> TemplateArgs = [];
    public EndianOverride Endian;
    public int Line;
    public int Column;

    public string Joined => string.Join("::", Path);
    public override string ToString() => Joined;
}

// ---- statements ----

public sealed class Attribute(string name, List<Expr> args)
{
    public string Name { get; } = name;
    public List<Expr> Args { get; } = args;
}

public abstract class Stmt
{
    public int Line;
    public int Column;
}

public enum ArrayKind { None, Sized, While, Unsized }

public sealed class VarDecl : Stmt
{
    public TypeRef Type = new();
    public string Name = "";
    public ArrayKind ArrayKind;
    public Expr? ArraySize;
    public Expr? ArrayCondition;
    public Expr? At;
    public Expr? InSection;
    public Expr? Init;
    public bool IsPointer;
    public TypeRef? PointerSize;
    public List<Attribute> Attributes = [];
    /// <summary><c>in</c> / <c>out</c> variables of the pattern, which take their value from the host and are otherwise locals.</summary>
    public bool IsInOut;
}

public sealed class PaddingStmt(Expr size) : Stmt
{
    public Expr Size { get; } = size;
}

public sealed class ExprStmt(Expr expr) : Stmt
{
    public Expr Expr { get; } = expr;
}

public sealed class BlockStmt(List<Stmt> body) : Stmt
{
    public List<Stmt> Body { get; } = body;
}

public sealed class IfStmt(Expr condition, Stmt then, Stmt? otherwise) : Stmt
{
    public Expr Condition { get; } = condition;
    public Stmt Then { get; } = then;
    public Stmt? Otherwise { get; } = otherwise;
}

public sealed class WhileStmt(Expr condition, Stmt body) : Stmt
{
    public Expr Condition { get; } = condition;
    public Stmt Body { get; } = body;
}

public sealed class ForStmt(Stmt? init, Expr? condition, Expr? step, Stmt body) : Stmt
{
    public Stmt? Init { get; } = init;
    public Expr? Condition { get; } = condition;
    public Expr? Step { get; } = step;
    public Stmt Body { get; } = body;
}

public sealed class MatchPattern
{
    public bool Wildcard;
    public Expr? Low;
    public Expr? High;
}

public sealed class MatchArm
{
    /// <summary>Per scrutinee, the alternatives it may match.</summary>
    public List<List<MatchPattern>> Patterns = [];
    public Stmt Body = new BlockStmt([]);
}

public sealed class MatchStmt(List<Expr> scrutinees, List<MatchArm> arms) : Stmt
{
    public List<Expr> Scrutinees { get; } = scrutinees;
    public List<MatchArm> Arms { get; } = arms;
}

public sealed class BreakStmt : Stmt;

public sealed class ContinueStmt : Stmt;

public sealed class ReturnStmt(Expr? value) : Stmt
{
    public Expr? Value { get; } = value;
}

public sealed class TypeDeclStmt(TypeDecl decl) : Stmt
{
    public TypeDecl Decl { get; } = decl;
}

// ---- declarations ----

public abstract class TypeDecl
{
    public string Name = "";
    public int Line;
    public List<Attribute> Attributes = [];
    /// <summary>The namespace the declaration sits in, as a path ("a::b"), or "".</summary>
    public string Namespace = "";
}

public sealed class TemplateParam
{
    public string Name = "";
    /// <summary>A value parameter (<c>auto N</c> or a typed one), not a type parameter.</summary>
    public bool IsValue;
}

public sealed class StructDecl : TypeDecl
{
    public bool IsUnion;
    public List<TemplateParam> TemplateParams = [];
    public List<TypeRef> Bases = [];
    public List<Stmt> Body = [];
}

public sealed class EnumEntry
{
    public string Name = "";
    public Expr? Value;
    public Expr? RangeEnd;
}

public sealed class EnumDecl : TypeDecl
{
    public TypeRef BaseType = new();
    public List<EnumEntry> Entries = [];
}

public abstract class BitfieldItem
{
    public int Line;
}

public sealed class BitfieldField : BitfieldItem
{
    public string Name = "";
    public TypeRef? Type;
    public Expr Bits = new LiteralExpr(Value.FromInt(1));
    public List<Attribute> Attributes = [];
}

public sealed class BitfieldPadding : BitfieldItem
{
    public Expr Bits = new LiteralExpr(Value.FromInt(1));
}

public sealed class BitfieldIf : BitfieldItem
{
    public Expr Condition = new LiteralExpr(Value.FromInt(1));
    public List<BitfieldItem> Then = [];
    public List<BitfieldItem> Otherwise = [];
}

public sealed class BitfieldDecl : TypeDecl
{
    public List<TemplateParam> TemplateParams = [];
    public List<BitfieldItem> Items = [];
}

public sealed class UsingDecl : TypeDecl
{
    /// <summary>The aliased type, or null for a forward declaration (<c>using Name;</c>).</summary>
    public TypeRef? Target;
    public List<TemplateParam> TemplateParams = [];
}

public sealed class FunctionParam
{
    public string Name = "";
    public TypeRef Type = new();
    public Expr? Default;
    public bool IsVariadic;
}

public sealed class FunctionDecl
{
    public string Name = "";
    public string Namespace = "";
    public List<FunctionParam> Params = [];
    public List<Stmt> Body = [];
    public int Line;
}

public sealed class NamespaceDecl
{
    public string Name = "";
    public List<object> Items = [];
}

public sealed class Program
{
    /// <summary>Type declarations, functions, namespaces and global statements, in source order.</summary>
    public List<object> Items = [];
    public Dictionary<string, string> Pragmas = [];
}
