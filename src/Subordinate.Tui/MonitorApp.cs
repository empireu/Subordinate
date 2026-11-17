using Subordinate.Core;
using Subordinate.Core.Configuration;
using Subordinate.Core.Monitoring;
using Subordinate.Tui.Data;
using Subordinate.Tui.Interface;
using Subordinate.Tui.Live;
using Terminal.Gui.App;

namespace Subordinate.Tui;

internal static class MonitorApp
{
    public static int Run(TuiOptions options)
    {
        var home = options.Home != null ? Path.GetFullPath(options.Home) : SubordinateConfig.ResolveHome();
        SubordinateConfig? config = null;

        try
        {
            config = SubordinateConfig.Load(home);
        }
        catch (SubordinateException)
        {
        }

        var endpoint = MonitorEndpoint.Resolve(config, home);
        using var app = Application.Create();
        app.Init();
        var registry = new LiveSessionRegistry();
        using var server = new MonitorServer(endpoint, registry);
        server.Start();
        var catalog = new SessionCatalog(home, directory => registry.IsConnected(directory));
        using var history = new HistoryLoader();
        using var window = new MonitorWindow(home);
        var controller = new MonitorController(window, catalog, registry, history, server, alias => ResolveContextWindow(config, alias), options.All);
        using var cancellation = new CancellationTokenSource();
        var poll = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                catalog.PollOnce();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        var timeout = app.AddTimeout(TimeSpan.FromMilliseconds(250), () =>
        {
            controller.Refresh();
            return true;
        });
        window.Window.KeyDown += (_, key) => controller.HandleKey(key);
        window.List.Accepting += (_, args) =>
        {
            controller.OpenSelected();
            args.Handled = true;
        };
        app.Run(window.Window);
        app.RemoveTimeout(timeout);
        cancellation.Cancel();

        try
        {
            poll.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static int? ResolveContextWindow(SubordinateConfig? config, string alias)
    {
        return config != null && config.Models.TryGetValue(alias, out var model) ? model.ContextWindow : null;
    }
}
