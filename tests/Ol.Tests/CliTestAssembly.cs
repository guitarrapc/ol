namespace Ol.Tests;

internal static class CliTestAssembly
{
    public static string ResolveOlDllPath(string testBaseDirectory)
        => Path.Combine(testBaseDirectory, "ol.dll");

    /// <summary>Returns what a scan wrote to stderr besides its summary, so a test can assert the run was clean.</summary>
    /// <remarks>
    /// Every successful scan writes the summary now, so "stderr is empty" no longer means "nothing went wrong". It
    /// is asserted by shape rather than by matching the summary's wording: the block is a blank line, the heading,
    /// and indented counter lines, while every diagnostic Ol writes starts at column zero. A test that meant
    /// "no warnings" therefore keeps meaning that even when a counter line changes.
    /// </remarks>
    public static string DiagnosticsOnly(string stderr)
    {
        if (stderr.Length == 0) return stderr;

        var kept = new List<string>();
        var inSummary = false;
        foreach (var line in stderr.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text == "Scan summary")
            {
                inSummary = true;
                continue;
            }

            if (inSummary && (text.Length == 0 || text.StartsWith("  ", StringComparison.Ordinal))) continue;
            inSummary = false;
            if (text.Length != 0) kept.Add(text);
        }

        return string.Join(Environment.NewLine, kept);
    }
}
