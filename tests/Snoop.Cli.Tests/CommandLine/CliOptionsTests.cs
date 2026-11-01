using Snoop.Cli.CommandLine;
using Snoop.Core.Diagnostics;

namespace Snoop.Cli.Tests.CommandLine;

public class CliOptionsTests
{
    [Fact]
    public void No_arguments_means_run_with_defaults()
    {
        var options = CliOptions.Parse([]);

        Assert.Equal(CliCommand.Run, options.Command);
        Assert.Equal(8866, options.Port);
        Assert.True(options.Decrypt);
        Assert.True(options.CaptureLoopback);
        Assert.False(options.SetSystemProxy);
        Assert.Equal(LogLevel.Info, options.LogLevel);
        Assert.True(options.UseDefaultSkips);
        Assert.True(options.AutoSkip);
    }

    [Fact]
    public void Skip_list_behaviour_can_be_turned_off()
    {
        var options = CliOptions.Parse(["--no-default-skips", "--no-auto-skip", "--keep-zstd"]);

        Assert.False(options.UseDefaultSkips);
        Assert.False(options.AutoSkip);
        Assert.False(options.AvoidZstd);
    }

    [Fact]
    public void Parses_run_options()
    {
        var options = CliOptions.Parse(["run", "-p", "9000", "--system-proxy", "--no-decrypt", "--no-loopback", "--skip", "*.bank.com", "--skip", "pinned.app", "-v", "--show-tunnels", "--allow-remote"]);

        Assert.Equal(9000, options.Port);
        Assert.True(options.SetSystemProxy);
        Assert.False(options.Decrypt);
        Assert.False(options.CaptureLoopback);
        Assert.Equal(new[] { "*.bank.com", "pinned.app" }, options.DoNotDecrypt);
        Assert.Equal(LogLevel.Debug, options.LogLevel);
        Assert.True(options.ShowTunnels);
        Assert.True(options.AllowRemote);
    }

    [Theory]
    [InlineData("trust-root", CliCommand.TrustRoot)]
    [InlineData("UNTRUST-ROOT", CliCommand.UntrustRoot)]
    [InlineData("reset-root", CliCommand.ResetRoot)]
    [InlineData("restore-proxy", CliCommand.RestoreProxy)]
    [InlineData("help", CliCommand.Help)]
    public void Parses_commands(string text, CliCommand expected) => Assert.Equal(expected, CliOptions.Parse([text]).Command);

    [Fact]
    public void Export_root_takes_a_path()
    {
        var options = CliOptions.Parse(["export-root", "root.pem"]);

        Assert.Equal(CliCommand.ExportRoot, options.Command);
        Assert.Equal("root.pem", options.OutputPath);
    }

    [Fact]
    public void Help_flag_wins()
    {
        Assert.Equal(CliCommand.Help, CliOptions.Parse(["--help"]).Command);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("--nope")]
    [InlineData("-p")]
    [InlineData("-p", "70000")]
    [InlineData("-p", "abc")]
    [InlineData("export-root")]
    public void Rejects_bad_input(params string[] args) => Assert.Throws<CliUsageException>(() => CliOptions.Parse(args));
}
