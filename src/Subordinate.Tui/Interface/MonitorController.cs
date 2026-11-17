using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Platform;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;
using Subordinate.Tui.Presentation;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using TerminalAttribute = Terminal.Gui.Drawing.Attribute;

namespace Subordinate.Tui.Interface;

public sealed class MonitorController
{
    private static readonly TerminalAttribute RunningAttribute = new(ColorName16.BrightGreen, ColorName16.Black);
    private static readonly TerminalAttribute ApprovalAttribute = new(ColorName16.BrightYellow, ColorName16.Black);
    private static readonly TerminalAttribute FailureAttribute = new(ColorName16.BrightRed, ColorName16.Black);
    private static readonly TerminalAttribute DimAttribute = new(ColorName16.DarkGray, ColorName16.Black);
    private static readonly TerminalAttribute GrayAttribute = new(ColorName16.Gray, ColorName16.Black);
    private static readonly TerminalAttribute ToolAttribute = new(ColorName16.BrightBlue, ColorName16.Black);
    private static readonly TerminalAttribute WarningAttribute = new(ColorName16.BrightYellow, ColorName16.Black);
    private static readonly TerminalAttribute LiveAttribute = new(ColorName16.BrightCyan, ColorName16.Black);

    private readonly MonitorWindow _window;
    private readonly SessionCatalog _catalog;
    private readonly LiveSessionRegistry _registry;
    private readonly HistoryLoader _history;
    private readonly MonitorServer _server;
    private readonly Func<string, int?> _contextWindowLookup;
    private readonly List<SessionSummary> _sessions = [];
    private readonly List<ChatMessage> _messages = [];
    private readonly List<HistoryBlock> _blocks = [];
    private readonly List<HistoryBlockKind> _detailKinds = [];

    private string? _selectedDirectory;
    private string? _selectedName;
    private bool _showAll;
    private bool _detailOpen;
    private bool _showReasoning;
    private bool _expandOutputs;
    private bool _follow = true;
    private bool _seenUser;
    private bool _historyDirty;
    private int _spinner;
    private int _lastWidth = -1;
    private int _liveLineCount;
    private long _catalogVersion = -1;
    private long _registryVersion = -1;
    private long _historyVersion = -1;
    private DateTimeOffset _lastRowRefresh = DateTimeOffset.MinValue;
    private string? _notice;

    public MonitorController(
        MonitorWindow window,
        SessionCatalog catalog,
        LiveSessionRegistry registry,
        HistoryLoader history,
        MonitorServer server,
        Func<string, int?> contextWindowLookup)
    {
        _window = window;
        _catalog = catalog;
        _registry = registry;
        _history = history;
        _server = server;
        _contextWindowLookup = contextWindowLookup;
        _window.List.RowRender += (_, args) => args.RowAttribute = ListAttribute(args.Row);
        _window.DetailList.RowRender += (_, args) => args.RowAttribute = DetailAttribute(args.Row);
    }

    public bool DetailOpen => _detailOpen;

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _catalog.Snapshot;
        var width = ResolveWidth();
        var sessions = SessionSelection.FilterAndSort(snapshot.Sessions, _showAll, now);
        _sessions.Clear();
        _sessions.AddRange(sessions);
        var rebuild = snapshot.Version != _catalogVersion
            || _registry.Version != _registryVersion
            || now - _lastRowRefresh >= TimeSpan.FromSeconds(1);

        if (rebuild)
        {
            RebuildRows(width, now);
            _catalogVersion = snapshot.Version;
            _registryVersion = _registry.Version;
            _lastRowRefresh = now;
            _spinner++;
        }

        if (_detailOpen)
        {
            RefreshDetail(width, now);
        }
        else
        {
            UpdateFooter(snapshot, sessions);
        }
    }

    public void OpenSelected()
    {
        var index = _window.List.SelectedItem ?? -1;

        if (index < 0 || index >= _sessions.Count)
        {
            return;
        }

        var session = _sessions[index];
        _selectedDirectory = session.Directory;
        _selectedName = session.Name;
        _detailOpen = true;
        _history.Open(session.Directory);
        _messages.Clear();
        _blocks.Clear();
        _seenUser = false;
        _historyVersion = -1;
        _lastWidth = -1;
        _historyDirty = true;
        _follow = true;
        _notice = null;
        _window.ShowDetail();
        Refresh();
    }

    public void CloseDetail(string? notice = null)
    {
        _detailOpen = false;
        _history.Close();
        _messages.Clear();
        _blocks.Clear();
        _seenUser = false;
        _historyVersion = -1;
        _lastWidth = -1;
        _liveLineCount = 0;
        _window.DetailLines.Clear();
        _detailKinds.Clear();
        _window.ShowList();
        _notice = notice;
        Refresh();
    }

    public void HandleKey(Key key)
    {
        if (key == Key.Esc)
        {
            if (_detailOpen)
            {
                CloseDetail();
                key.Handled = true;
            }

            return;
        }

        if (key == Key.Q)
        {
            if (_detailOpen)
            {
                CloseDetail();
            }
            else
            {
                _window.Window.StopRequested = true;
            }

            key.Handled = true;
            return;
        }

        if (key == Key.A)
        {
            if (!_detailOpen)
            {
                _showAll = !_showAll;
                _catalogVersion = -1;
                Refresh();
                key.Handled = true;
            }

            return;
        }

        if (!_detailOpen)
        {
            return;
        }

        if (key == Key.R)
        {
            _showReasoning = !_showReasoning;
            ReformatBlocks();
            key.Handled = true;
        }
        else if (key == Key.O)
        {
            _expandOutputs = !_expandOutputs;
            ReformatBlocks();
            key.Handled = true;
        }
        else if (key == Key.F)
        {
            _follow = true;
            Refresh();
            key.Handled = true;
        }
    }

    private int ResolveWidth()
    {
        var width = _window.List.Viewport.Width;
        return width > 0 ? width : 120;
    }

    private void RebuildRows(int width, DateTimeOffset now)
    {
        var rows = new List<string>(_sessions.Count);
        var activityWidth = SessionRowFormatter.ActivityWidth(width);

        foreach (var session in _sessions)
        {
            var live = _registry.GetSnapshot(session.Directory);
            var activity = ActivityHint.Format(session, live, now, activityWidth);
            rows.Add(SessionRowFormatter.FormatRow(session, live?.IsConnected == true, activity, _spinner, width, now));
        }

        _window.Header.Text = _sessions.Count == 0
            ? $"No sessions in {_window.Home}."
            : SessionRowFormatter.FormatHeader(width);
        ReplaceRows(rows);

        var selectedIndex = -1;

        if (_selectedDirectory != null)
        {
            selectedIndex = _sessions.FindIndex(session => string.Equals(session.Directory, _selectedDirectory, PlatformServices.Current.Paths.Comparison));
        }

        if (selectedIndex < 0)
        {
            selectedIndex = _sessions.Count > 0 ? 0 : -1;
        }

        _window.List.SelectedItem = selectedIndex >= 0 ? selectedIndex : null;
        _selectedDirectory = selectedIndex >= 0 ? _sessions[selectedIndex].Directory : null;
        _selectedName = selectedIndex >= 0 ? _sessions[selectedIndex].Name : null;
    }

    private void ReplaceRows(IReadOnlyList<string> rows)
    {
        _window.List.SuspendCollectionChangedEvent();

        try
        {
            while (_window.Rows.Count > rows.Count)
            {
                _window.Rows.RemoveAt(_window.Rows.Count - 1);
            }

            for (var index = 0; index < rows.Count; index++)
            {
                if (index < _window.Rows.Count)
                {
                    if (_window.Rows[index] != rows[index])
                    {
                        _window.Rows[index] = rows[index];
                    }
                }
                else
                {
                    _window.Rows.Add(rows[index]);
                }
            }
        }
        finally
        {
            _window.List.ResumeSuspendCollectionChangedEvent();
        }
    }

    private void RefreshDetail(int width, DateTimeOffset now)
    {
        var session = _selectedDirectory == null
            ? null
            : _sessions.FirstOrDefault(candidate => string.Equals(candidate.Directory, _selectedDirectory, PlatformServices.Current.Paths.Comparison));

        if (session == null)
        {
            CloseDetail($"session {_selectedName ?? "the session"} was deleted");
            return;
        }

        var live = _registry.GetSnapshot(session.Directory);

        if (_window.DetailLines.Count > 0 && _window.DetailList.SelectedItem is int selectedLine)
        {
            _follow = selectedLine >= _window.DetailLines.Count - 1;
        }

        _window.DetailHeader.Text = string.Join("\n", DetailHeaderFormatter.Format(session, live, _contextWindowLookup(session.ModelAlias), now));
        _history.RequestRefresh(live?.State.TranscriptCount);
        var history = _history.Snapshot;

        if (history.Version != _historyVersion)
        {
            if (history.Reset)
            {
                _messages.Clear();
                _blocks.Clear();
                _seenUser = false;
            }

            if (history.NewMessages.Count > 0)
            {
                _messages.AddRange(history.NewMessages);
                AppendBlocks(history.NewMessages);
            }

            _historyVersion = history.Version;
            _historyDirty = true;
        }

        if (_historyDirty || width != _lastWidth)
        {
            RebuildDetailLines(live, width, now);
            _historyDirty = false;
            _lastWidth = width;
        }
        else
        {
            UpdateLiveSection(live, width, now);
        }

        if (_follow && _window.DetailLines.Count > 0)
        {
            _window.DetailList.SelectedItem = _window.DetailLines.Count - 1;
            _window.DetailList.EnsureSelectedItemVisible();
        }

        UpdateFooter(_catalog.Snapshot, _sessions);
    }

    private void AppendBlocks(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            var isFirstUser = message.Role == ChatRole.User && !_seenUser;

            if (message.Role == ChatRole.User)
            {
                _seenUser = true;
            }

            _blocks.AddRange(HistoryFormatter.FormatMessage(message, isFirstUser, _showReasoning, _expandOutputs));
        }
    }

    private void ReformatBlocks()
    {
        _blocks.Clear();
        _seenUser = false;
        AppendBlocks(_messages);
        _historyDirty = true;
        Refresh();
    }

    private void RebuildDetailLines(LiveSessionSnapshot? live, int width, DateTimeOffset now)
    {
        var lines = new List<string>();
        var kinds = new List<HistoryBlockKind>();

        foreach (var block in _blocks)
        {
            var wrapped = TextWrap.Wrap(block.Text, width, 0);
            lines.AddRange(wrapped);

            foreach (var _ in wrapped)
            {
                kinds.Add(block.Kind);
            }
        }

        var liveLines = FormatLiveLines(live, width, now);
        lines.AddRange(liveLines);

        foreach (var _ in liveLines)
        {
            kinds.Add(HistoryBlockKind.Live);
        }

        ReplaceDetailLines(lines, kinds);
        _liveLineCount = liveLines.Count;
    }

    private void UpdateLiveSection(LiveSessionSnapshot? live, int width, DateTimeOffset now)
    {
        var liveLines = FormatLiveLines(live, width, now);
        _window.DetailList.SuspendCollectionChangedEvent();

        try
        {
            var remove = Math.Min(_liveLineCount, _window.DetailLines.Count);

            for (var index = 0; index < remove; index++)
            {
                _window.DetailLines.RemoveAt(_window.DetailLines.Count - 1);
            }

            for (var index = 0; index < remove; index++)
            {
                _detailKinds.RemoveAt(_detailKinds.Count - 1);
            }

            foreach (var line in liveLines)
            {
                _window.DetailLines.Add(line);
                _detailKinds.Add(HistoryBlockKind.Live);
            }
        }
        finally
        {
            _window.DetailList.ResumeSuspendCollectionChangedEvent();
        }

        _liveLineCount = liveLines.Count;
    }

    private void ReplaceDetailLines(IReadOnlyList<string> lines, IReadOnlyList<HistoryBlockKind> kinds)
    {
        _window.DetailList.SuspendCollectionChangedEvent();

        try
        {
            while (_window.DetailLines.Count > lines.Count)
            {
                _window.DetailLines.RemoveAt(_window.DetailLines.Count - 1);
            }

            while (_detailKinds.Count > kinds.Count)
            {
                _detailKinds.RemoveAt(_detailKinds.Count - 1);
            }

            for (var index = 0; index < lines.Count; index++)
            {
                if (index < _window.DetailLines.Count)
                {
                    if (_window.DetailLines[index] != lines[index])
                    {
                        _window.DetailLines[index] = lines[index];
                    }
                }
                else
                {
                    _window.DetailLines.Add(lines[index]);
                }
            }

            _detailKinds.Clear();
            _detailKinds.AddRange(kinds);
        }
        finally
        {
            _window.DetailList.ResumeSuspendCollectionChangedEvent();
        }
    }

    private static List<string> FormatLiveLines(LiveSessionSnapshot? live, int width, DateTimeOffset now)
    {
        var lines = new List<string>();

        foreach (var block in HistoryFormatter.FormatLive(live, now, width))
        {
            lines.AddRange(TextWrap.Wrap(block.Text, width, 0));
        }

        return lines;
    }

    private TerminalAttribute? ListAttribute(int row)
    {
        if (row < 0 || row >= _sessions.Count)
        {
            return null;
        }

        return _sessions[row].DisplayState switch
        {
            SessionStates.Starting or SessionStates.Running => RunningAttribute,
            "approval" => ApprovalAttribute,
            "failed" or "interrupted" => FailureAttribute,
            _ => null
        };
    }

    private TerminalAttribute? DetailAttribute(int row)
    {
        if (row < 0 || row >= _detailKinds.Count)
        {
            return null;
        }

        return _detailKinds[row] switch
        {
            HistoryBlockKind.System or HistoryBlockKind.Thinking => DimAttribute,
            HistoryBlockKind.Supervisor or HistoryBlockKind.ToolResult => GrayAttribute,
            HistoryBlockKind.Tool => ToolAttribute,
            HistoryBlockKind.Warning => WarningAttribute,
            HistoryBlockKind.Live => LiveAttribute,
            _ => null
        };
    }

    private void UpdateFooter(CatalogSnapshot snapshot, IReadOnlyList<SessionSummary> sessions)
    {
        var running = snapshot.Sessions.Count(session => session.DisplayState == "running");
        var approval = snapshot.Sessions.Count(session => session.DisplayState == "approval");
        var input = snapshot.Sessions.Sum(session => session.SessionCounters.InputTokens);
        var output = snapshot.Sessions.Sum(session => session.SessionCounters.OutputTokens);
        var notice = _notice != null ? _notice + " | " : string.Empty;
        var keys = _detailOpen ? "esc back  r reasoning  o outputs  f follow  q back" : "enter open  a all  q quit";
        _window.Footer.Text = $"{notice}{running} running, {approval} awaiting approval, {sessions.Count} shown of {snapshot.Sessions.Count} | in {Units.FormatTokens(input)} out {Units.FormatTokens(output)} | live: {_server.Status} | {keys}";
    }
}
