using System.Text.Json;

namespace HexpatSharp.Tests;

public class LanguageTests
{
    internal static EvaluationResult Run(string pattern, params byte[] data)
    {
        EvaluationResult result = Hexpat.Evaluate(pattern, data);
        Assert.Null(result.Error);
        return result;
    }

    internal static JsonElement Tree(string pattern, params byte[] data)
    {
        EvaluationResult result = Run(pattern, data);
        return JsonDocument.Parse(result.ToJson()).RootElement.GetProperty("ast");
    }

    private static string Value(JsonElement node) => node.GetProperty("value").GetString()!;

    private static JsonElement Child(JsonElement node, string name)
        => node.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void ScalarsAreReadInOrderWithTheirOffsetsAndSizes()
    {
        JsonElement tree = Tree("""
            struct Header { u8 a; u16 b; u32 c; };
            Header h @ 0;
            """, 1, 2, 3, 4, 5, 6, 7);

        JsonElement h = tree[0];
        Assert.Equal("h", h.GetProperty("name").GetString());
        Assert.Equal("Header", h.GetProperty("type").GetString());
        Assert.Equal(0, h.GetProperty("start_offset").GetInt32());
        Assert.Equal(7, h.GetProperty("size").GetInt32());
        Assert.Equal("1", Value(Child(h, "a")));
        Assert.Equal("770", Value(Child(h, "b")));       // 03 02 little endian
        Assert.Equal("117835012", Value(Child(h, "c")));  // 07 06 05 04
        Assert.Equal(1, Child(h, "b").GetProperty("start_offset").GetInt32());
        Assert.Equal(2, Child(h, "b").GetProperty("size").GetInt32());
    }

    [Fact]
    public void BigEndianPrefixAndPragma()
    {
        JsonElement tree = Tree("struct S { be u16 a; le u16 b; }; S s @ 0;", 1, 2, 1, 2);
        Assert.Equal("258", Value(Child(tree[0], "a")));
        Assert.Equal("513", Value(Child(tree[0], "b")));

        JsonElement all = Tree("#pragma endian big\nu16 x @ 0;", 1, 2);
        Assert.Equal("258", Value(all[0]));
    }

    [Fact]
    public void SignedFloatBoolAndCharValues()
    {
        JsonElement tree = Tree("""
            s8 a @ 0;
            float f @ 1;
            bool t @ 5;
            char c @ 6;
            """, 0xFF, 0, 0, 0x80, 0x3F, 1, (byte)'Z');

        Assert.Equal("-1", Value(tree[0]));
        Assert.Equal("1", Value(tree[1]));
        Assert.Equal("true", Value(tree[2]));
        Assert.Equal("Z", Value(tree[3]));
    }

    [Fact]
    public void ArraysOfNumbersAndCharacters()
    {
        JsonElement tree = Tree("""
            char magic[4] @ 0;
            u8 numbers[3] @ 4;
            char name[] @ 7;
            """, (byte)'T', (byte)'E', (byte)'S', (byte)'T', 1, 2, 3, (byte)'h', (byte)'i', 0, 99);

        Assert.Equal("TEST", Value(tree[0]));
        Assert.Equal("char[4]", tree[0].GetProperty("type").GetString());
        Assert.Equal("[1, 2, 3]", Value(tree[1]));
        Assert.Equal(3, tree[1].GetProperty("children").GetArrayLength());
        Assert.Equal("hi", Value(tree[2]));
        Assert.Equal(3, tree[2].GetProperty("size").GetInt32());   // the terminator is part of it
    }

    [Fact]
    public void ArraysOfStructsAndCountsFromEarlierMembers()
    {
        JsonElement tree = Tree("""
            struct Entry { u8 id; u8 value; };
            struct File { u8 count; Entry entries[count]; };
            File f @ 0;
            """, 2, 10, 20, 11, 21);

        JsonElement entries = Child(tree[0], "entries");
        Assert.Equal("Entry[2]", entries.GetProperty("type").GetString());
        Assert.Equal(2, entries.GetProperty("children").GetArrayLength());
        Assert.Equal("21", Value(Child(entries.GetProperty("children")[1], "value")));
        Assert.Equal(5, tree[0].GetProperty("size").GetInt32());
    }

    [Fact]
    public void WhileArraysAndTheCursor()
    {
        JsonElement tree = Tree("""
            struct Entry { u8 v; };
            u8 end = 3;
            Entry entries[while($ < end)] @ 0;
            """, 1, 2, 3, 4);

        Assert.Equal(3, tree[0].GetProperty("children").GetArrayLength());
    }

    [Fact]
    public void EnumsShowTheirNames()
    {
        JsonElement tree = Tree("""
            enum Kind : u8 { A, B = 5, C };
            Kind k1 @ 0; Kind k2 @ 1; Kind k3 @ 2;
            """, 0, 5, 6);

        Assert.Equal("A (0)", Value(tree[0]));
        Assert.Equal("B (5)", Value(tree[1]));
        Assert.Equal("C (6)", Value(tree[2]));
    }

    [Fact]
    public void BitfieldsSplitTheirBitsFromTheLeastSignificantUp()
    {
        JsonElement tree = Tree("""
            bitfield Flags { low : 3; mid : 4; bool top : 1; };
            Flags f @ 0;
            """, 0b1_0101_011);

        JsonElement f = tree[0];
        Assert.Equal("3", Value(Child(f, "low")));
        Assert.Equal("5", Value(Child(f, "mid")));
        Assert.Equal("true", Value(Child(f, "top")));
        Assert.Equal(1, f.GetProperty("size").GetInt32());
    }

    [Fact]
    public void ConditionalsMatchAndLocalVariables()
    {
        JsonElement tree = Tree("""
            struct S {
                u8 kind;
                u8 doubled = kind * 2;
                if (kind == 1) { u8 one; }
                else if (kind == 2) { u16 two; }
                else { u8 other; }
                match (kind) {
                    (1 | 3): u8 odd;
                    (2): u8 even;
                    (_): u8 rest;
                }
            };
            S a @ 0;
            S b @ 4;
            """, 1, 7, 8, 0, 2, 7, 0, 8, 9);

        JsonElement a = tree[0];
        Assert.Equal("7", Value(Child(a, "one")));
        Assert.Equal("8", Value(Child(a, "odd")));
        Assert.Equal(3, a.GetProperty("size").GetInt32());
        JsonElement b = tree[1];
        Assert.Equal("7", Value(Child(b, "two")));
        Assert.Equal("8", Value(Child(b, "even")));
    }

    [Fact]
    public void PointersFollowTheirTarget()
    {
        JsonElement tree = Tree("""
            struct Item { u8 v; };
            Item *ptr : u8 @ 0;
            """, 3, 0, 0, 77);

        JsonElement ptr = tree[0];
        Assert.Equal("*0x3", Value(ptr));
        Assert.Equal(1, ptr.GetProperty("size").GetInt32());
        Assert.Equal("77", Value(Child(ptr.GetProperty("children")[0], "v")));
    }

    [Fact]
    public void FunctionsFormatAndTransformAttributes()
    {
        JsonElement tree = Tree("""
            fn relocate(auto o) { return o.value + 10; };
            fn show(auto o) { return std::format("<{:#06x}>", o.value); };
            struct Off { u8 value; } [[transform("relocate"), format("show")]];
            struct Holder { Off off; u8 data @ off + 1 [[hidden]]; };
            Holder h @ 0;
            """, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5);

        JsonElement h = tree[0];
        Assert.Equal("<0x0001>", Value(Child(h, "off")));
        // off transforms to 1 + 10 = 11, so data would be at 12, but it is hidden.
        Assert.DoesNotContain(h.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "data");
    }

    [Fact]
    public void ParentAndThisReachTheEnclosingStruct()
    {
        JsonElement tree = Tree("""
            struct Inner { u8 x; u8 y = parent.base + this.x - 1; u8 z @ addressof(this) + y; };
            struct Outer { u8 base; Inner inner; };
            Outer o @ 0;
            """, 5, 2, 0, 0, 0, 0, 0, 9);

        Assert.Equal("9", Value(Child(Child(tree[0], "inner"), "z")));
    }

    [Fact]
    public void NamespacesTemplatesAndInheritance()
    {
        JsonElement tree = Tree("""
            namespace a::b { struct Pair<T> { T first; T second; }; }
            struct Base { u8 id; };
            struct Derived : Base { a::b::Pair<u16> pair; };
            Derived d @ 0;
            """, 1, 2, 0, 3, 0);

        Assert.Equal("1", Value(Child(tree[0], "id")));
        JsonElement pair = Child(tree[0], "pair");
        Assert.Equal("a::b::Pair<u16>".Replace("a::b::", ""), pair.GetProperty("type").GetString());
        Assert.Equal("3", Value(Child(pair, "second")));
    }

    [Fact]
    public void UnionsOverlapAndInlineFlattens()
    {
        JsonElement tree = Tree("""
            union U { u8 a; u16 b; u32 c; };
            struct Wrap { u8 first; U u; } ;
            struct Flat { u8 x; Wrap w [[inline]]; };
            U u @ 0;
            Flat f @ 4;
            """, 1, 2, 3, 4, 9, 7, 8, 9, 0, 0);

        Assert.Equal(4, tree[0].GetProperty("size").GetInt32());
        Assert.Equal("67305985", Value(Child(tree[0], "c")));
        JsonElement flat = tree[1];
        Assert.Contains(flat.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "first");
        Assert.DoesNotContain(flat.GetProperty("children").EnumerateArray(), c => c.GetProperty("name").GetString() == "w");
    }

    [Fact]
    public void ExpressionPrecedenceFollowsC()
    {
        // == binds tighter than &, so 6 & (0x80 != 0) is 6 & 1 = 0.
        EvaluationResult result = Run("""
            u8 dummy @ 0;
            fn check(auto a, auto b) { std::assert(a == b, std::format("{} != {}", a, b)); };
            check(2 + 3 * 4, 14);
            check(6 & 0x80 != 0, 0);
            check((1 << 3) | 1, 9);
            check(10 - 2 - 3, 5);
            check(1 ? 7 : 8, 7);
            check(7 / 2, 3);
            check(-7 % 4, -3);
            check(0xFF >> 4, 15);
            check(!0 && 1, 1);
            check("ab" + "c", "abc");
            """, 0);
        Assert.Empty(result.Log);
    }

    [Fact]
    public void SectionsAndFindSequence()
    {
        EvaluationResult result = Run("""
            fn slice(u128 start, u128 size) {
                std::mem::Section s = std::mem::create_section("slice");
                u8 data[size] @ start;
                std::mem::set_section_size(s, size);
                std::mem::copy_value_to_section(data, s, 0);
                return s;
            };
            u8 first @ 0;
            std::mem::Section s = slice(2, 2);
            u16 copied @ 0 in s;
            u32 found = std::mem::find_sequence_in_range(0, 0, 8, 9, 8);
            std::assert(found == 5, "found");
            std::assert(copied == 0x0403, "copied");
            """, 1, 2, 3, 4, 5, 9, 8, 0);
        Assert.Empty(result.Log);
    }

    [Fact]
    public void ErrorsKeepWhatWasPlacedBeforeThem()
    {
        EvaluationResult result = Hexpat.Evaluate("u8 a @ 0;\nu8 b @ 100;\nstd::assert(b == 0, \"reads b\");\n", [1, 2]);

        Assert.NotNull(result.Error);
        Assert.Contains("3:", result.Error);
        Assert.Equal(2, result.Patterns.Count);
        Assert.Contains("evaluationError", result.ToJson());
    }

    [Fact]
    public void StdErrorAndAssertionsStopEvaluation()
    {
        Assert.NotNull(Hexpat.Evaluate("std::assert(false, \"boom\");", []).Error);
        Assert.Contains("custom", Hexpat.Evaluate("std::error(\"custom\");", []).Error);
        EvaluationResult warn = Hexpat.Evaluate("std::assert_warn(false, \"careful\"); std::print(\"{}\", 5);", []);
        Assert.Null(warn.Error);
        Assert.Equal(["warning: careful", "5"], warn.Log);
    }

    [Fact]
    public void StdFormatSpecifiers()
    {
        EvaluationResult r = Run("""
            fn check(auto a, auto b) { std::assert(a == b, std::format("[{}] vs [{}]", a, b)); };
            check(std::format("{:#x}", 255), "0xff");
            check(std::format("{:08X}", 255), "000000FF");
            check(std::format("{:>5}|{:<5}|{:^5}", 1, 2, 3), "    1|2    |  3  ");
            check(std::format("{0}{1}{0}", "a", "b"), "aba");
            check(std::format("{:.2f}", 3.14159), "3.14");
            check(std::format("{:b}", 5), "101");
            check(std::format("{{}}"), "{}");
            check(std::format("{:d}", -5), "-5");
            """, 0);
        Assert.Empty(r.Log);
    }
}
