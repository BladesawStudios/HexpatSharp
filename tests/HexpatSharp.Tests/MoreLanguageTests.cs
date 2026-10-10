using System.Text.Json;

namespace HexpatSharp.Tests;

public class MoreLanguageTests
{
    private static JsonElement Tree(string pattern, params byte[] data) => LanguageTests.Tree(pattern, data);

    private static string Value(JsonElement node) => node.GetProperty("value").GetString()!;

    private static JsonElement Child(JsonElement node, string name)
        => node.GetProperty("children").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    [Fact]
    public void AnUnnamedTypeAlignsTheCursor()
    {
        JsonElement tree = Tree("""
            struct S { u8 a; std::mem::AlignTo<4>; u8 b; };
            S s @ 0;
            """, 1, 0, 0, 0, 2);

        Assert.Equal("2", Value(Child(tree[0], "b")));
        Assert.Equal(5, tree[0].GetProperty("size").GetInt32());
        Assert.Equal(4, Child(tree[0], "b").GetProperty("start_offset").GetInt32());
    }

    [Fact]
    public void BitfieldsFollowTheByteOrder()
    {
        // Big endian: the first field takes the top bits, unless an order says otherwise.
        JsonElement big = Tree("""
            #pragma endian big
            bitfield Size { flags : 2; size : 30; };
            Size s @ 0;
            """, 0x40, 0, 1, 0);
        Assert.Equal("1", Value(Child(big[0], "flags")));
        Assert.Equal("256", Value(Child(big[0], "size")));

        JsonElement ordered = Tree("""
            bitfield Size { flags : 2; size : 30; } [[bitfield_order(std::core::BitfieldOrder::MostToLeastSignificant, 32)]];
            Size s @ 0;
            """, 0, 1, 0, 0x40);
        Assert.Equal("1", Value(Child(ordered[0], "flags")));
    }

    [Fact]
    public void FunctionsWithDefaultsVariadicsLoopsAndTheCursor()
    {
        LanguageTests.Run("""
            fn add(u32 a, u32 b = 10) { return a + b; };
            fn total(auto ... values) {
                u64 sum = 0;
                u32 i = 0;
                while (i < 3) {
                    sum += values[i];
                    i += 1;
                }
                return sum;
            };
            fn firstBig(auto limit) {
                for (u32 i = 0, i < 100, i += 1) {
                    if (i * i < limit) { continue; }
                    return i;
                }
                return 0;
            };
            fn check(auto a, auto b) { std::assert(a == b, std::format("{} != {}", a, b)); };
            check(add(1), 11);
            check(add(1, 2), 3);
            check(total(1, 2, 3), 6);
            check(firstBig(50), 8);
            u8 first @ 0;
            $ = 3;
            u8 third @ $;
            check(third, 4);
            """, 1, 2, 3, 4);
    }

    [Fact]
    public void TemplatesTakeTypesAndValues()
    {
        JsonElement tree = Tree("""
            struct Buffer<T, auto N> { u8 count = N; T values[N]; };
            Buffer<u16, 2> b @ 0;
            """, 1, 0, 2, 0);

        JsonElement b = tree[0];
        Assert.Equal("Buffer<u16, 2>", b.GetProperty("type").GetString());
        Assert.Equal(4, b.GetProperty("size").GetInt32());
        Assert.Equal("[1, 2]", Value(Child(b, "values")));
    }

    [Fact]
    public void ThePreprocessorDefinesAndSkips()
    {
        JsonElement tree = Tree("""
            #define SIZE 2
            #ifdef SIZE
            u8 a[SIZE] @ 0;
            #else
            u8 a[5] @ 0;
            #endif
            #ifndef SIZE
            u8 missing @ 0;
            #endif
            """, 7, 8, 9);

        Assert.Single(tree.EnumerateArray());
        Assert.Equal("[7, 8]", Value(tree[0]));
    }

    [Fact]
    public void SizeofAddressofAndTypenameof()
    {
        LanguageTests.Run("""
            struct S { u16 a; u32 b; };
            S s @ 2;
            fn check(auto a, auto b) { std::assert(a == b, std::format("{} != {}", a, b)); };
            check(sizeof(S), 6);
            check(sizeof(u64), 8);
            check(sizeof(s), 6);
            check(addressof(s), 2);
            check(addressof(s.b), 4);
            check(typenameof(s), "S");
            check(sizeof(S) + 1, 7);
            """, 0, 0, 1, 0, 2, 0, 0, 0);
    }

    [Fact]
    public void CharacterArraysCompareWithStringsAndIndex()
    {
        LanguageTests.Run("""
            char magic[4] @ 0;
            fn check(auto a, auto b) { std::assert(a == b, std::format("{} != {}", a, b)); };
            check(magic, "ABCD");
            check(magic[1], 'B');
            check(std::string::length("hello"), 5);
            check(std::string::substr("hello", 1, 3), "ell");
            check(std::string::to_upper("abc"), "ABC");
            check(std::string::starts_with("abcdef", "abc"), true);
            """, (byte)'A', (byte)'B', (byte)'C', (byte)'D');
    }

    [Fact]
    public void ParseErrorsSayWhere()
    {
        EvaluationResult result = Hexpat.Evaluate("u8 a @ 0;\nstruct { u8 b; };\n", [0]);

        Assert.StartsWith("2:", result.Error);
        Assert.Empty(result.Patterns);
    }

    [Fact]
    public void PatternsWithinTheLimitsOnly()
    {
        EvaluationResult result = Hexpat.Evaluate("struct E { u8 v; };\nE items[100] @ 0;", new byte[200], new EvaluationOptions { PatternLimit = 50 });
        Assert.Contains("pattern_limit", result.Error);

        EvaluationResult arrays = Hexpat.Evaluate("u8 big[1000] @ 0;", new byte[2000], new EvaluationOptions { ArrayLimit = 10 });
        Assert.Contains("array_limit", arrays.Error);
    }

    [Fact]
    public void RunawayLoopsStop()
    {
        EvaluationResult result = Hexpat.Evaluate("fn spin() { while (true) { } };\nspin();", [0], new EvaluationOptions { StepLimit = 10000 });

        Assert.Contains("too many steps", result.Error);
    }

    [Fact]
    public void RecursionIsBounded()
    {
        EvaluationResult result = Hexpat.Evaluate("fn again(auto n) { return again(n + 1); };\nagain(0);", [0], new EvaluationOptions { EvalDepth = 64 });

        Assert.Contains("nested too deeply", result.Error);
    }

    [Fact]
    public void ForwardDeclaredTypesAndParentChains()
    {
        JsonElement tree = Tree("""
            using Child;
            struct Parent { u8 base; Child c; };
            struct Child { u8 own; u8 total = own + parent.base; u8 at @ total; };
            Parent p @ 0;
            """, 1, 2, 3, 4);

        Assert.Equal("4", Value(Child(Child(tree[0], "c"), "at")));
    }
}
