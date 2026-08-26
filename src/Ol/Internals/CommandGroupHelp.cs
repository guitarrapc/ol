using ConsoleAppFramework;

namespace Ol.Internals;

internal static class CommandGroupHelp
{
    private const string Cache = """
        Usage: cache [command] [-h|--help] [--version]

        Manage locally cached scan evidence.

        Commands:
          info      Shows the contents of a cache directory or archive.
          list      Lists managed cache locations and sizes.
          pack      Packs managed cache entries into one deterministic archive.
          unpack    Unpacks an Ol cache archive into the managed cache directories.
          clear     Clears cached evidence for the specified category.
          prune     Removes managed cache entries older than the specified age.
        """;

    private const string Spdx = """
        Usage: spdx [command] [-h|--help] [--version]

        Manage SPDX data.

        Commands:
          clear      Clear user-managed SPDX data.
          list       List installed SPDX data versions.
          update     Download SPDX data into the user data directory.
          use        Switch active SPDX data version.
          version    Show the active SPDX data source.
        """;

    private const string Skill = """
        Usage: skill [command] [-h|--help] [--version]

        Install or export the bundled license-scan agent skill.

        Commands:
          export-plugin    Export a portable Agent Plugin package.
          install          Install the skill into the current workspace.
        """;

    public static bool TryShow(string[] args)
    {
        if (args is not [var commandGroup, "--help" or "-h"])
        {
            return false;
        }

        var help = commandGroup switch
        {
            "cache" => Cache,
            "skill" => Skill,
            "spdx" => Spdx,
            _ => null,
        };
        if (help is null)
        {
            return false;
        }

        ConsoleApp.Log(help);
        return true;
    }
}
