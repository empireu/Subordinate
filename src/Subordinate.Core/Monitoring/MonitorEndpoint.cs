using System.Text;
using Subordinate.Core.Configuration;

namespace Subordinate.Core.Monitoring;

public sealed record MonitorEndpoint(bool Enabled, string? SocketPath, string? Problem)
{
    public static MonitorEndpoint Resolve(SubordinateConfig? config, string home)
    {
        var monitor = config?.Monitor;

        if (monitor is { Enabled: false })
        {
            return new MonitorEndpoint(false, null, null);
        }

        var path = monitor?.SocketPath;

        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(home, "monitor.sock");
        }
        else if (!Path.IsPathFullyQualified(path))
        {
            return new MonitorEndpoint(false, null, "monitor.socket_path must be an absolute path.");
        }

        if (Encoding.UTF8.GetByteCount(path) > 100)
        {
            return new MonitorEndpoint(false, null, "The monitor socket path is longer than 100 bytes. Set monitor.socket_path to a shorter absolute path.");
        }

        return new MonitorEndpoint(true, path, null);
    }
}
