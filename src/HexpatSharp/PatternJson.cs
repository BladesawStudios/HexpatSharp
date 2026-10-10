using System.Text;
using System.Text.Json;

namespace HexpatSharp;

/// <summary>
/// The placed patterns as a tree for a viewer: <c>{ name, type, start_offset, size, value?, children }</c> per node. Hidden patterns
/// are left out and inline ones are replaced by their contents.
/// </summary>
public static class PatternJson
{
    public sealed class Options
    {
        /// <summary>Most children shown under one array; the rest are summed up in a final node.</summary>
        public int MaxChildren { get; set; } = 20000;

        /// <summary>Arrays of plain numbers up to this long list each element; longer ones show a preview.</summary>
        public int MaxScalarChildren { get; set; } = 256;

        public int MaxDepth { get; set; } = 100;
    }

    /// <summary>The tree as JSON: <c>{"ast": [...], "log": [...], "error": ...}</c> (log and error only when there are some).</summary>
    public static string ToJson(EvaluationResult result, Options? options = null)
    {
        options ??= new Options();
        Interpreter? interpreter = result.Interpreter; // missing when the pattern did not even parse

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("ast");
            writer.WriteStartArray();
            if (interpreter is not null)
                foreach (Pattern p in Visible(result.Patterns))
                    WriteNode(writer, p, interpreter, options, 0);
            writer.WriteEndArray();

            if (result.Log.Count > 0)
            {
                writer.WritePropertyName("log");
                writer.WriteStartArray();
                foreach (string line in result.Log) writer.WriteStringValue(line);
                writer.WriteEndArray();
            }

            if (result.Error is not null) writer.WriteString("evaluationError", result.Error);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static IEnumerable<Pattern> Visible(IEnumerable<Pattern> patterns)
    {
        foreach (Pattern p in patterns)
        {
            if (p.Hidden) continue;
            if (p.Inline && !(p is ArrayPattern { IsScalar: true }))
            {
                foreach (Pattern child in Visible(ChildrenOf(p))) yield return child;
                continue;
            }
            yield return p;
        }
    }

    private static IEnumerable<Pattern> ChildrenOf(Pattern p) => p switch
    {
        BitfieldPattern bf => bf.Fields.Where(f => !f.IsPadding),
        _ => p.Children,
    };

    private static string TypeOf(Pattern p) => p is ArrayPattern a ? $"{a.ElementTypeName}[{a.Count}]" : p.TypeName;

    private static void WriteNode(Utf8JsonWriter w, Pattern p, Interpreter interpreter, Options options, int depth)
    {
        w.WriteStartObject();
        w.WriteString("name", p.DisplayName ?? p.Name);
        w.WriteString("type", TypeOf(p));
        w.WriteNumber("start_offset", p.Offset);
        w.WriteNumber("size", p.Size);

        string? value = null;
        try
        {
            if (p is StructPattern or BitfieldPattern) value = p.Format is not null ? interpreter.FormatPattern(p) : null;
            else if (p is ArrayPattern { IsScalar: false }) value = p.Format is not null ? interpreter.FormatPattern(p) : null;
            else value = interpreter.FormatPattern(p);
        }
        catch (HexpatException e)
        {
            value = "??"; // not readable, e.g. past the end of the data
            _ = e;
        }
        if (value is not null) w.WriteString("value", value);

        if (p is BitfieldFieldPattern field)
        {
            w.WriteNumber("bit_offset", field.BitOffset);
            w.WriteNumber("bit_size", field.BitSize);
        }
        if (p.Comment is not null) w.WriteString("comment", p.Comment);

        w.WritePropertyName("children");
        w.WriteStartArray();
        if (depth < options.MaxDepth) WriteChildren(w, p, interpreter, options, depth);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteChildren(Utf8JsonWriter w, Pattern p, Interpreter interpreter, Options options, int depth)
    {
        if (p is ArrayPattern { IsScalar: true } scalars)
        {
            // Characters read as the string above; short lists of numbers also list their elements.
            if (scalars.ScalarElement is ScalarKind.Char or ScalarKind.Char16 || scalars.Count > (ulong)options.MaxScalarChildren) return;
            for (ulong i = 0; i < scalars.Count; i++) WriteNode(w, scalars.ScalarAt(i), interpreter, options, depth + 1);
            return;
        }

        int written = 0;
        int total = 0;
        foreach (Pattern child in Visible(ChildrenOf(p)))
        {
            total++;
            if (written >= options.MaxChildren) continue;
            WriteNode(w, child, interpreter, options, depth + 1);
            written++;
        }

        if (total > written)
        {
            w.WriteStartObject();
            w.WriteString("name", "...");
            w.WriteString("type", "truncated");
            w.WriteNumber("start_offset", p.Offset);
            w.WriteNumber("size", 0);
            w.WriteString("value", $"{total - written} more not shown");
            w.WritePropertyName("children");
            w.WriteStartArray();
            w.WriteEndArray();
            w.WriteEndObject();
        }
    }
}
