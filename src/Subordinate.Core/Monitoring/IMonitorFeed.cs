namespace Subordinate.Core.Monitoring;

public interface IMonitorFeed : IDisposable
{
    void Start();

    void Publish(MonitorMessage message);
}
