namespace Subordinate.Core.Monitoring;

public sealed class NullMonitorFeed : IMonitorFeed
{
    public static readonly NullMonitorFeed Instance = new();

    private NullMonitorFeed()
    {
    }

    public void Start()
    {
    }

    public void Publish(MonitorMessage message)
    {
    }

    public void Dispose()
    {
    }
}
