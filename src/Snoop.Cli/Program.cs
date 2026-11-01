using Snoop.Cli.CommandLine;
using Snoop.Cli.Commands;
using Snoop.Cli.Output;
using Snoop.Core.Diagnostics;

namespace Snoop.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int Usage = 2;
    public const int PortUnavailable = 3;
}

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var writer = new ConsoleWriter();
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            writer.WriteLine(ex.Message, ConsoleColor.Red, toError: true);
            writer.WriteLine("Run 'snoop help' for usage.", toError: true);
            return ExitCodes.Usage;
        }

        var logger = new ConsoleLogger(options.LogLevel, writer);
        var maintenance = new MaintenanceCommands(logger, writer);
        try
        {
            return options.Command switch
            {
                CliCommand.Run => await new RunCommand(options, logger, writer).ExecuteAsync().ConfigureAwait(false),
                CliCommand.TrustRoot => maintenance.TrustRoot(),
                CliCommand.UntrustRoot => maintenance.UntrustRoot(),
                CliCommand.ExportRoot => maintenance.ExportRoot(options.OutputPath!),
                CliCommand.ResetRoot => maintenance.ResetRoot(),
                CliCommand.RestoreProxy => maintenance.RestoreProxy(),
                _ => ShowHelp(writer),
            };
        }
#pragma warning disable CA1031 // Top level: report any failure as a message and an exit code.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.Error("Snoop stopped because of an unexpected error.", ex);
            return ExitCodes.Failed;
        }
    }

    private static int ShowHelp(ConsoleWriter writer)
    {
        writer.WriteLine(CliOptions.Usage);
        return ExitCodes.Success;
    }
}
