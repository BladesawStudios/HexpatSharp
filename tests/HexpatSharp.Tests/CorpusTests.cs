namespace HexpatSharp.Tests;

/// <summary>Runs against the patterns shipped with TKVSC when <c>TKVSC_HEXPAT_DIR</c> points at their folder (config/hexpat).</summary>
public class CorpusTests
{
    [Fact]
    public void ShippedPatternsAllParse()
    {
        string? folder = Environment.GetEnvironmentVariable("TKVSC_HEXPAT_DIR");
        if (folder is null || !Directory.Exists(folder)) return;

        foreach (string file in Directory.GetFiles(folder, "*.hexpat"))
        {
            EvaluationResult result = Hexpat.Evaluate(File.ReadAllText(file), new byte[1 << 16]);

            // Data of zeros will stop most patterns part way; what must not happen is a pattern failing to parse at all.
            Assert.True(result.Interpreter is not null, $"{Path.GetFileName(file)} did not parse: {result.Error}");
        }
    }
}
