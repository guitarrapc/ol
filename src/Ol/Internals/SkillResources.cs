using System.Reflection;

namespace Ol.Internals;

internal readonly record struct SkillResource(string RelativePath, byte[] Content);

internal static class SkillResources
{
    private const string Prefix = "Skills/license-scan/";
    private static readonly Assembly ThisAssembly = typeof(SkillResources).Assembly;

    public static SkillResource[] ReadAll()
    {
        var names = ThisAssembly.GetManifestResourceNames();
        Array.Sort(names, StringComparer.Ordinal);
        var resources = new List<SkillResource>(names.Length);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var normalizedName = name.Replace('\\', '/');
            if (!normalizedName.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = ThisAssembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded skill resource was not found: {name}");
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            resources.Add(new SkillResource(normalizedName[Prefix.Length..], buffer.ToArray()));
        }

        return resources.ToArray();
    }
}
