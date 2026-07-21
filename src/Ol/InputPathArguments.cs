using ConsoleAppFramework;

[AttributeUsage(AttributeTargets.Parameter)]
internal sealed class InputPathsParserAttribute : Attribute, IArgumentParser<string[]>
{
    public static bool TryParse(ReadOnlySpan<char> value, out string[] result)
    {
        if (value.IsEmpty)
        {
            result = [];
            return false;
        }

        var count = 1;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == CommandLineArguments.InputSeparator)
            {
                count++;
            }
        }

        result = new string[count];
        var start = 0;
        var resultIndex = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i != value.Length && value[i] != CommandLineArguments.InputSeparator)
            {
                continue;
            }

            if (i == start)
            {
                result = [];
                return false;
            }

            result[resultIndex++] = value[start..i].ToString();
            start = i + 1;
        }

        return true;
    }
}

internal static class CommandLineArguments
{
    internal const char InputSeparator = '\0';

    public static string[] NormalizeRepeatedScanInputs(string[] args)
    {
        if (args.Length < 5
            || !string.Equals(args[0], "scan", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(args[0], "check", StringComparison.OrdinalIgnoreCase))
        {
            return args;
        }

        var inputCount = 0;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                inputCount++;
                i++;
            }
        }

        if (inputCount < 2)
        {
            return args;
        }

        var inputs = new string[inputCount];
        var inputIndex = 0;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                break;
            }

            if (string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                inputs[inputIndex++] = args[++i];
            }
        }

        var rewritten = new string[args.Length - ((inputCount - 1) * 2)];
        var rewrittenIndex = 0;
        var emittedInput = false;
        var escaped = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                escaped = true;
            }

            if (!escaped && string.Equals(args[i], "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!emittedInput)
                {
                    rewritten[rewrittenIndex++] = args[i];
                    rewritten[rewrittenIndex++] = string.Join(InputSeparator, inputs);
                    emittedInput = true;
                }

                i++;
                continue;
            }

            rewritten[rewrittenIndex++] = args[i];
        }

        return rewritten;
    }
}
