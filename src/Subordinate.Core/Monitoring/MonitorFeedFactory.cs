namespace Subordinate.Core.Monitoring;

public static class MonitorFeedFactory
{
    public static IMonitorFeed Create(MonitorEndpoint endpoint, MonitorRunInfo runInfo)
    {
        if (!endpoint.Enabled || endpoint.Problem != null || endpoint.SocketPath == null)
        {
            return NullMonitorFeed.Instance;
        }

        return new GuardedMonitorFeed(new SocketMonitorFeed(endpoint.SocketPath, runInfo));
    }
}
