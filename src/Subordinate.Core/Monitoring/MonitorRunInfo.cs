namespace Subordinate.Core.Monitoring;

public sealed record MonitorRunInfo(
    string SessionDirectory,
    string Session,
    string Workdir,
    string RunId,
    string RunKind,
    int ProcessId,
    string ModelAlias,
    string ModelId,
    DateTimeOffset StartedAt);
