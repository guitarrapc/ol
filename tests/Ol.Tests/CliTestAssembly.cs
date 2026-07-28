namespace Ol.Tests;

internal static class CliTestAssembly
{
    public static string ResolveOlDllPath(string testBaseDirectory)
        => Path.Combine(testBaseDirectory, "ol.dll");
}
