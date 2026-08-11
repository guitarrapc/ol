using ConsoleAppFramework;

/// <summary>Writes command failures as concise stderr diagnostics.</summary>
internal sealed class CliExceptionFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Environment.ExitCode = 1;
            ConsoleApp.LogError(exception.Message);
        }
    }
}
