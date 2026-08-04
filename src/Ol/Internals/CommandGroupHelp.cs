using ConsoleAppFramework;

namespace Ol.Internals;

internal static class CommandGroupHelp
{
    private const string Cache = """
        Usage: cache [command] [-h|--help] [--version]

        Manage locally cached scan evidence.

        Commands:
          clear    Clears cached evidence for the specified category.
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

    public static bool TryShow(string[] args)
    {
        if (args is not [var commandGroup, "--help" or "-h"])
        {
            return false;
        }

        var help = commandGroup switch
        {
            "cache" => Cache,
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
