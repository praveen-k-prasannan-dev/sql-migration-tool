using SqlMigrationTool.Services;

namespace TestConsole;

/// <summary>Offline checks of pure helpers (no database needed).</summary>
public static class SelfTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) Out.Ok($"  ✔ {name}");
            else { failures++; Out.Error($"  ✘ {name} {detail}"); }
        }

        Out.Step("BatchSplitter");
        void Batches(string name, string script, params string[] expected)
        {
            var actual = BatchSplitter.Split(script);
            Check(name, actual.SequenceEqual(expected),
                  $"\n      expected [{string.Join(" | ", expected)}]\n      actual   [{string.Join(" | ", actual)}]");
        }

        Batches("splits on GO",                    "SELECT 1\nGO\nSELECT 2",            "SELECT 1", "SELECT 2");
        Batches("GO is case-insensitive",          "SELECT 1\ngo\nSELECT 2",            "SELECT 1", "SELECT 2");
        Batches("CRLF and surrounding spaces",     "SELECT 1\r\n   GO   \r\nSELECT 2", "SELECT 1", "SELECT 2");
        Batches("GO n repeats the batch",          "SELECT 1\nGO 3\n",                  "SELECT 1", "SELECT 1", "SELECT 1");
        Batches("GO with trailing comment",        "SELECT 1\nGO -- done\nSELECT 2",    "SELECT 1", "SELECT 2");
        Batches("GOTO does not split",             "SELECT 1\nGOTO done\ndone:",        "SELECT 1\nGOTO done\ndone:");
        Batches("GO inside block comment",         "/* x\nGO\n*/ SELECT 1",             "/* x\nGO\n*/ SELECT 1");
        Batches("nested block comment",            "/* a /* b */\nGO\n*/ SELECT 1\nGO\nSELECT 2",
                                                   "/* a /* b */\nGO\n*/ SELECT 1", "SELECT 2");
        Batches("GO inside string literal",        "SELECT 'a\nGO\nb'",                 "SELECT 'a\nGO\nb'");
        Batches("escaped quote in string",         "SELECT 'it''s\nGO\nok'\nGO\nSELECT 2", "SELECT 'it''s\nGO\nok'", "SELECT 2");
        Batches("GO inside [identifier]",          "SELECT 1 AS [a\nGO\nb]",            "SELECT 1 AS [a\nGO\nb]");
        Batches("line comment hides quote",        "SELECT 1 -- don't\nGO\nSELECT 2",   "SELECT 1 -- don't", "SELECT 2");
        Batches("GO not alone on its line",        "SELECT 1; GO",                      "SELECT 1; GO");
        Batches("empty batches dropped",           "GO\n\nGO\nSELECT 1\nGO\n",          "SELECT 1");

        Console.WriteLine();
        Out.Step($"Self-test: {(failures == 0 ? "all passed" : $"{failures} failed")}");
        return failures == 0 ? 0 : 1;
    }
}
