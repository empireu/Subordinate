using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Monitoring;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Interface;
using Subordinate.Tui.Live;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;

namespace Subordinate.Tests;

[Collection("monitor")]
public class MonitorWindowTests
{
    [Fact]
    public void TheListShowsTheHeaderAndRunningSessionFirst()
    {
        using var harness = new WindowHarness();
        var now = DateTimeOffset.UtcNow;
        harness.Data.CreateRunningSession("running-one", now.AddSeconds(-30));
        harness.Data.CreateSession("approval-one", SessionStates.AwaitingApproval, now.AddMinutes(-5));
        harness.Data.CreateSession("finished-one", SessionStates.Finished, now.AddMinutes(-10));
        harness.Refresh();

        var screen = harness.Screen;

        Assert.Contains("state", screen);
        Assert.Contains("running-one", screen);
        Assert.Contains("approval-one", screen);
        Assert.Contains("finished-one", screen);
        Assert.True(
            screen.IndexOf("running-one", StringComparison.Ordinal) < screen.IndexOf("approval-one", StringComparison.Ordinal),
            "The running session should be listed before the approval session.");
        Assert.True(
            screen.IndexOf("approval-one", StringComparison.Ordinal) < screen.IndexOf("finished-one", StringComparison.Ordinal),
            "The approval session should be listed before the finished session.");
    }

    [Fact]
    public void EnterOpensTheSecondSession()
    {
        using var harness = new WindowHarness();
        var now = DateTimeOffset.UtcNow;
        harness.Data.CreateRunningSession("alpha", now.AddSeconds(-30));
        harness.Data.CreateSession("beta", SessionStates.Finished, now.AddMinutes(-2), "Do the beta task.");
        harness.Data.CreateSession("gamma", SessionStates.Finished, now.AddMinutes(-4), "Do the gamma task.");
        harness.Refresh();

        harness.Key(Key.CursorDown);
        harness.Key(Key.Enter);
        harness.WaitForScreen(screen => screen.Contains("Do the beta task.", StringComparison.Ordinal), "The beta task should appear in the detail view");

        var screen = harness.Screen;
        Assert.True(harness.Controller.DetailOpen);
        Assert.Contains("beta  finished", screen);
        Assert.Contains("turns", screen);
    }

    [Fact]
    public void EscapeReturnsToTheListAndThenQuits()
    {
        using var harness = new WindowHarness();
        harness.Data.CreateSession("escape", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1));
        harness.Refresh();
        harness.OpenFirst();
        Assert.True(harness.Controller.DetailOpen);

        harness.Key(Key.Esc);

        Assert.False(harness.Controller.DetailOpen);
        Assert.False(harness.View.Window.StopRequested);

        harness.Key(Key.Esc);

        Assert.True(harness.View.Window.StopRequested);
    }

    [Fact]
    public void QReturnsToTheList()
    {
        using var harness = new WindowHarness();
        harness.Data.CreateSession("q-session", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1));
        harness.Refresh();
        harness.OpenFirst();
        Assert.True(harness.View.DetailList.HasFocus);

        harness.Key(Key.Q);

        Assert.False(harness.Controller.DetailOpen);
        Assert.False(harness.View.Window.StopRequested);
    }

    [Fact]
    public void AShowsAnOldFinishedSession()
    {
        using var harness = new WindowHarness();
        var now = DateTimeOffset.UtcNow;
        harness.Data.CreateSession("old-finished", SessionStates.Finished, now.AddDays(-2));
        harness.Data.CreateSession("recent-finished", SessionStates.Finished, now.AddMinutes(-1));
        harness.Refresh();

        Assert.DoesNotContain("old-finished", harness.Screen);

        harness.Key(Key.A);

        Assert.Contains("old-finished", harness.Screen);
    }

    [Fact]
    public void LiveReasoningAppearsInTheRowAndTheDetail()
    {
        using var harness = new WindowHarness();
        var now = DateTimeOffset.UtcNow;
        var paths = harness.Data.CreateRunningSession("live-one", now.AddSeconds(-30));
        harness.Refresh();
        harness.Registry.Apply(paths.Directory, new MonitorHello(paths.Directory, "live-one", harness.Data.Workdir, "run-1", "start", 1234, "test", "scripted", now.AddSeconds(-30)), true);
        harness.Registry.Apply(paths.Directory, new MonitorTurnStarted(1, now), true);
        harness.Registry.Apply(paths.Directory, new MonitorReasoningDelta("Checking the build output carefully."), true);
        harness.Refresh();

        Assert.Contains("thinking", harness.Screen);

        harness.OpenFirst();
        harness.WaitForScreen(screen => screen.Contains("Checking the build output", StringComparison.Ordinal), "The live reasoning tail should appear in the detail view");
    }

    [Fact]
    public void FollowModeTracksTheLastLineUntilTheUserMovesUp()
    {
        using var harness = new WindowHarness();
        var paths = harness.Data.CreateSession("follow", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1), "Follow this task.");
        harness.Refresh();
        harness.OpenFirst();
        harness.WaitForScreen(screen => screen.Contains("Follow this task.", StringComparison.Ordinal), "The task should load");
        Assert.Equal(harness.View.DetailLines.Count - 1, harness.View.DetailList.SelectedItem);

        harness.Data.AppendMessages(paths, Assistant("First answer."));
        harness.WaitForScreen(screen => screen.Contains("First answer.", StringComparison.Ordinal), "The first answer should load");
        Assert.Equal(harness.View.DetailLines.Count - 1, harness.View.DetailList.SelectedItem);

        harness.Key(Key.CursorUp);
        harness.Refresh();
        var selected = harness.View.DetailList.SelectedItem;
        Assert.True(selected < harness.View.DetailLines.Count - 1, "Moving up should leave follow mode.");

        harness.Data.AppendMessages(paths, Assistant("Second answer."));
        harness.WaitForScreen(screen => screen.Contains("Second answer.", StringComparison.Ordinal), "The second answer should load");
        Assert.Equal(selected, harness.View.DetailList.SelectedItem);

        harness.Key(Key.End);
        harness.Refresh();
        Assert.Equal(harness.View.DetailLines.Count - 1, harness.View.DetailList.SelectedItem);

        harness.Data.AppendMessages(paths, Assistant("Third answer."));
        harness.WaitForScreen(screen => screen.Contains("Third answer.", StringComparison.Ordinal), "The third answer should load");
        Assert.Equal(harness.View.DetailLines.Count - 1, harness.View.DetailList.SelectedItem);
    }

    [Fact]
    public void ResizingToATinyTerminalDoesNotThrow()
    {
        using var harness = new WindowHarness();
        harness.Data.CreateSession("resize", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1));
        harness.Refresh();

        harness.Draw(60, 20);
        harness.Controller.Refresh();
        harness.Draw(60, 20);
        harness.Draw(30, 10);
        harness.Controller.Refresh();
        harness.Draw(30, 10);

        Assert.Contains("state", harness.Screen);
    }

    [Fact]
    public void DeletingTheOpenSessionReturnsToTheListWithANotice()
    {
        using var harness = new WindowHarness();
        var paths = harness.Data.CreateSession("doomed", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1));
        harness.Refresh();
        harness.OpenFirst();
        Assert.True(harness.Controller.DetailOpen);

        Directory.Delete(paths.Directory, true);
        harness.Catalog.PollOnce();
        harness.Controller.Refresh();
        harness.Draw();

        Assert.False(harness.Controller.DetailOpen);
        Assert.Contains("was deleted", harness.Screen);
    }

    [Fact]
    public void EscapeSequencesInTheTranscriptAreShownAsCaretNotation()
    {
        using var harness = new WindowHarness();
        harness.Data.CreateSession("escape-text", SessionStates.Finished, DateTimeOffset.UtcNow.AddMinutes(-1), "Color \u001b[31mred\u001b[0m text.");
        harness.Refresh();
        harness.OpenFirst();
        harness.WaitForScreen(screen => screen.Contains("^[", StringComparison.Ordinal), "The escape should be shown as ^[");

        Assert.DoesNotContain('\u001b', harness.Screen);
    }

    private static ChatMessage Assistant(string text)
    {
        return new ChatMessage { Role = ChatRole.Assistant, Contents = [new TextContent { Text = text }] };
    }

    private sealed class WindowHarness : IDisposable
    {
        private readonly IApplication _app;
        private readonly SessionToken? _token;

        public WindowHarness()
        {
            Data = new TuiTestHome();
            Registry = new LiveSessionRegistry();
            Catalog = new SessionCatalog(Data.Home, directory => Registry.IsConnected(directory));
            History = new HistoryLoader();
            Server = new MonitorServer(new MonitorEndpoint(false, null, null), Registry);
            _app = Application.Create(new VirtualTimeProvider());
            _app.Init(DriverRegistry.Names.ANSI);
            View = new MonitorWindow(Data.Home);
            Controller = new MonitorController(View, Catalog, Registry, History, Server, _ => 200_000);
            View.Window.KeyDown += (_, key) => Controller.HandleKey(key);
            View.List.Accepting += (_, args) =>
            {
                Controller.OpenSelected();
                args.Handled = true;
            };
            _token = _app.Begin(View.Window);
            View.List.SetFocus();
        }

        public TuiTestHome Data { get; }

        public LiveSessionRegistry Registry { get; }

        public SessionCatalog Catalog { get; }

        public HistoryLoader History { get; }

        public MonitorServer Server { get; }

        public MonitorWindow View { get; }

        public MonitorController Controller { get; }

        public string Screen => _app.Driver!.ToString();

        public void Refresh()
        {
            Catalog.PollOnce();
            Controller.Refresh();
            Draw();
        }

        public void Draw(int width = 120, int height = 30)
        {
            _app.Driver!.SetScreenSize(width, height);
            _app.LayoutAndDraw(true);
        }

        public void Key(Key key)
        {
            _app.InjectKey(key);
            Draw();
        }

        public void OpenFirst()
        {
            Controller.OpenSelected();
            Draw();
        }

        public void WaitForScreen(Func<string, bool> predicate, string message)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                Catalog.PollOnce();
                Controller.Refresh();
                Draw();

                if (predicate(Screen))
                {
                    return;
                }

                Thread.Sleep(20);
            }

            throw new TimeoutException($"{message}. Screen:{Environment.NewLine}{Screen}");
        }

        public void Dispose()
        {
            if (_token != null)
            {
                try
                {
                    _app.End(_token);
                }
                catch (Exception)
                {
                }
            }

            _app.Dispose();
            View.Dispose();
            History.Dispose();
            Server.Dispose();
            Data.Dispose();
        }
    }
}
