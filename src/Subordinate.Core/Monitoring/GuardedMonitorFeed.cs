namespace Subordinate.Core.Monitoring;

public sealed class GuardedMonitorFeed(IMonitorFeed inner) : IMonitorFeed
{
    private IMonitorFeed? _inner = inner;
    private int _failed;

    public void Start()
    {
        try
        {
            _inner?.Start();
        }
        catch (Exception)
        {
            Fail();
        }
    }

    public void Publish(MonitorMessage message)
    {
        try
        {
            _inner?.Publish(message);
        }
        catch (Exception)
        {
            Fail();
        }
    }

    public void Dispose()
    {
        var inner = Interlocked.Exchange(ref _inner, null);

        try
        {
            inner?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private void Fail()
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        Dispose();
    }
}
