namespace Ol.Internals;

internal static class CommandLineRouting
{
    public static bool TryValidate(ReadOnlySpan<string> args, out string error)
    {
        error = string.Empty;
        if (args.IsEmpty || IsFrameworkOutput(args[0]))
        {
            return true;
        }

        switch (args[0])
        {
            case "scan":
            case "check":
            case "diff":
                return RequireArguments(args, out error);
            case "spdx":
                return ValidateSpdx(args, out error);
            case "cache":
                return ValidateCache(args, out error);
            default:
                error = $"Command '{args[0]}' is not recognized.";
                return false;
        }
    }

    private static bool RequireArguments(ReadOnlySpan<string> args, out string error)
    {
        if (args.Length > 1)
        {
            error = string.Empty;
            return true;
        }

        error = $"Command '{args[0]}' requires arguments. Use 'ol {args[0]} --help' for usage.";
        return false;
    }

    private static bool ValidateSpdx(ReadOnlySpan<string> args, out string error)
    {
        if (args.Length == 1)
        {
            error = "Command 'spdx' requires a subcommand. Use 'ol spdx --help' for usage.";
            return false;
        }

        if (IsFrameworkOutput(args[1]))
        {
            error = string.Empty;
            return true;
        }

        switch (args[1])
        {
            case "clear":
            case "list":
            case "update":
            case "version":
                error = string.Empty;
                return true;
            case "use" when args.Length > 2:
                error = string.Empty;
                return true;
            case "use":
                error = "Command 'spdx use' requires an argument. Use 'ol spdx use --help' for usage.";
                return false;
            default:
                error = $"Command 'spdx {args[1]}' is not recognized.";
                return false;
        }
    }

    private static bool ValidateCache(ReadOnlySpan<string> args, out string error)
    {
        if (args.Length == 1)
        {
            error = "Command 'cache' requires a subcommand. Use 'ol cache --help' for usage.";
            return false;
        }

        if (IsFrameworkOutput(args[1]) || args[1] == "clear")
        {
            error = string.Empty;
            return true;
        }

        error = $"Command 'cache {args[1]}' is not recognized.";
        return false;
    }

    private static bool IsFrameworkOutput(string argument)
        => argument is "--help" or "-h" or "--version";
}
