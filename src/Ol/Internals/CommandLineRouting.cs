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

        // Which command was named decides what its options mean, so an unrecognized command is reported
        // before anything is said about the options written after it.
        return TryValidateCommand(args, out error) && TryValidateSingleUseOptions(args, out error);
    }

    /// <summary>
    /// Rejects an invocation that supplies one option more than once.
    /// </summary>
    /// <remarks>
    /// Ol has no layer a later value could override: no configuration file, no environment defaults, and
    /// [policy files are outside the check contract](cli.md#contract-policy-checks). So no invocation means
    /// "replace what I said before", and a repeat is either an accident or an intent to accumulate. Keeping
    /// one of the values silently served neither: it changed the policy a run enforced, and which value
    /// survived depended on argument order, so the same two flags passed in the other order gave a
    /// different answer with nothing said about it.
    ///
    /// The options that do accumulate are collapsed into a single occurrence by
    /// <see cref="CommandLineArguments.NormalizeRepeatedInputs"/> before this runs, so this rule needs no
    /// list of exemptions and cannot fall out of step with which options actually accumulate.
    /// </remarks>
    private static bool TryValidateSingleUseOptions(ReadOnlySpan<string> args, out string error)
    {
        for (var i = 1; i < args.Length; i++)
        {
            // Everything past the escape is a value, whatever it looks like.
            if (args[i] == ArgumentEscape)
            {
                break;
            }

            if (!IsOption(args[i]))
            {
                continue;
            }

            for (var j = i + 1; j < args.Length; j++)
            {
                if (args[j] == ArgumentEscape)
                {
                    break;
                }

                // Matched the way the parser matches option names, so casing cannot slip a duplicate past.
                if (!string.Equals(args[j], args[i], StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                error = $"Argument '{args[i].ToLowerInvariant()}' was supplied more than once.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private const string ArgumentEscape = "--";

    /// <summary>Reports whether an argument names an option rather than a value.</summary>
    /// <remarks>
    /// Ol accepts neither <c>--option=value</c> nor short forms, so an option is exactly a token longer than
    /// the escape that starts with it. A value could in principle be written the same way, which is what the
    /// escape is for.
    /// </remarks>
    private static bool IsOption(string argument)
        => argument.Length > 2 && argument[0] == '-' && argument[1] == '-';

    private static bool TryValidateCommand(ReadOnlySpan<string> args, out string error)
    {
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
            case "skill":
                return ValidateSkill(args, out error);
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

        if (args[1] == "prune")
        {
            if (args.Length > 2 && IsFrameworkOutput(args[2]))
            {
                error = string.Empty;
                return true;
            }

            for (var i = 2; i < args.Length; i++)
            {
                if (args[i] == "--max-age"
                    || args[i].StartsWith("--max-age=", StringComparison.Ordinal))
                {
                    error = string.Empty;
                    return true;
                }
            }

            error = "Required argument 'max-age' was not specified.";
            return false;
        }

        if (args[1] is "pack" or "unpack")
        {
            if (args.Length > 2)
            {
                error = string.Empty;
                return true;
            }

            error = $"Command 'cache {args[1]}' requires an argument. Use 'ol cache {args[1]} --help' for usage.";
            return false;
        }

        error = $"Command 'cache {args[1]}' is not recognized.";
        return false;
    }

    private static bool ValidateSkill(ReadOnlySpan<string> args, out string error)
    {
        if (args.Length == 1)
        {
            error = "Command 'skill' requires a subcommand. Use 'ol skill --help' for usage.";
            return false;
        }

        if (IsFrameworkOutput(args[1]) || args[1] == "install")
        {
            error = string.Empty;
            return true;
        }

        if (args[1] == "export-plugin")
        {
            if (args.Length > 2 && IsFrameworkOutput(args[2]))
            {
                error = string.Empty;
                return true;
            }
            for (var i = 2; i + 1 < args.Length; i++)
            {
                if (args[i] == "--output")
                {
                    error = string.Empty;
                    return true;
                }
            }

            error = "Command 'skill export-plugin' requires --output. Use 'ol skill export-plugin --help' for usage.";
            return false;
        }

        error = $"Command 'skill {args[1]}' is not recognized.";
        return false;
    }

    private static bool IsFrameworkOutput(string argument)
        => argument is "--help" or "-h" or "--version";
}
