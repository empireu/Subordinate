using Subordinate.Core.Sessions;
using Subordinate.Tui;

namespace Subordinate.Tests;

public class TuiPrintTests
{
    [Fact]
    public void PrintListsTheHeaderAndEverySession()
    {
        using var home = new TuiTestHome();
        home.CreateSession("alpha", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-5));
        home.CreateSession("beta", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-10));
        var writer = new StringWriter();

        var exit = TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Width = 120 }, writer);

        Assert.Equal(0, exit);
        var text = writer.ToString();
        Assert.StartsWith("home: ", text);
        Assert.Contains("state", text);
        Assert.Contains("alpha", text);
        Assert.Contains("beta", text);
    }

    [Fact]
    public void PrintOnAnEmptyHomeShowsAHint()
    {
        using var home = new TuiTestHome();
        var writer = new StringWriter();

        var exit = TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Width = 120 }, writer);

        Assert.Equal(0, exit);
        Assert.Contains($"No sessions in {home.Home}.", writer.ToString());
    }

    [Fact]
    public void PrintSessionShowsTheStatsHeaderAndHistory()
    {
        using var home = new TuiTestHome();
        home.CreateSession("alpha", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-5), "Do the alpha task.");
        var writer = new StringWriter();

        var exit = TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Session = "alpha", Width = 120 }, writer);

        Assert.Equal(0, exit);
        var text = writer.ToString();
        Assert.Contains("alpha  finished", text);
        Assert.Contains("tokens", text);
        Assert.Contains("\n-- task ---", text);
        Assert.Contains("\n  Do the alpha task.", text);
    }

    [Fact]
    public void AnUnknownSessionExitsWithCodeThree()
    {
        using var home = new TuiTestHome();
        home.CreateSession("alpha", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-5));

        var exception = Assert.Throws<TuiException>(() => TuiPrint.Run(new TuiOptions { Print = true, Home = home.Home, Session = "missing", Width = 120 }, new StringWriter()));

        Assert.Equal(3, exception.ExitCode);
    }

    [Fact]
    public void BadOptionsAreRejected()
    {
        var (options, error) = TuiCommandLine.Parse(["--bogus"]);
        Assert.Null(options);
        Assert.NotNull(error);

        var (widthOptions, widthError) = TuiCommandLine.Parse(["--print", "--width", "10"]);
        Assert.Null(widthOptions);
        Assert.Contains("--width", widthError);
    }

    [Fact]
    public void RedirectedStreamsRequirePrint()
    {
        Assert.True(Subordinate.Tui.Program.RequiresInteractiveTerminal(true, false));
        Assert.True(Subordinate.Tui.Program.RequiresInteractiveTerminal(false, true));
        Assert.False(Subordinate.Tui.Program.RequiresInteractiveTerminal(false, false));
    }
}
