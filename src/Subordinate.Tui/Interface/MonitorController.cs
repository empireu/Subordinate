using KnowledgeSystem.Ai.Providers.Abstractions;
using Subordinate.Core.Platform;
using Subordinate.Core.Sessions;
using Subordinate.Tui.Data;
using Subordinate.Tui.Live;
using Subordinate.Tui.Presentation;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using TerminalAttribute = Terminal.Gui.Drawing.Attribute;

namespace Subordinate.Tui.Interface;

public sealed class MonitorController
{
    private const int HintTailFactor = 4;
    private const int MinimumHintTailCharacters = 256;
    private static readonly TimeSpan RegistryCleanupInterval = TimeSpan.FromSeconds(10);

    private readonly MonitorWindow _window;
    private readonly SessionCatalog _catalog;
    private readonly LiveSessionRegistry _registry;
    private readonly HistoryLoader _history;
    private readonly MonitorServer _server;
    private readonly Func<string, int?> _contextWindowLookup;
    private readonly List<SessionSummary> _sessions = [];
    private readonly List<ChatMessage> _messages = [];
    private readonly List<HistoryBlock> _blocks = [];
    private readonly List<HistoryLine> _historyLines = [];
    private readonly List<HistoryLine> _detailLineInfo = [];
    private readonly HistoryFormatState _formatState = new();

    private string? _selectedDirectory;
    private string? _selectedName;
    private bool _showAll;
    private bool _detailOpen;
    private bool _showReasoning;
    private bool _expandOutputs;
    private bool _follow = true;
    private bool _relayout;
    private string? _titleState;
    private int _spinner;
    private int _layoutWidth = -1;
    private int _laidOutBlocks;
    private int _liveLineCount;
    private int _followSelection = -1;
    private long _catalogVersion = -1;
    private long _registryVersion = -1;
    private long _historyVersion = -1;
    private DateTimeOffset _lastRowRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRegistryCleanup = DateTimeOffset.MinValue;
    private string? _notice;
    private string? _historyNotice;

    public MonitorController(
        MonitorWindow window,
        SessionCatalog catalog,
        LiveSessionRegistry registry,
        HistoryLoader history,
        MonitorServer server,
        Func<string, int?> contextWindowLookup,
        bool showAll = false)
    {
        _window = window;
        _catalog = catalog;
        _registry = registry;
        _history = history;
        _server = server;
        _contextWindowLookup = contextWindowLookup;
        _showAll = showAll;
        _window.List.RowRender += (_, args) => args.RowAttribute = ListAttribute(args.Row);
        _window.DetailList.RowRender += (_, args) => args.RowAttribute = DetailAttribute(args.Row);
        _window.DetailStyles = row => row >= 0 && row < _detailLineInfo.Count ? _detailLineInfo[row].Styles : null;
    }

    public bool DetailOpen => _detailOpen;

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = _catalog.Snapshot;
        var width = ResolveWidth();
        RememberListSelection();
        var rebuild = snapshot.Version != _catalogVersion
            || _registry.Version != _registryVersion
            || now - _lastRowRefresh >= TimeSpan.FromSeconds(1);

        if (rebuild)
        {
            _sessions.Clear();
            _sessions.AddRange(SessionSelection.FilterAndSort(snapshot.Sessions, _showAll, now));

            // The detail view hides the rows, and closing it forces a rebuild:
            if (!_detailOpen)
            {
                RebuildRows(width, now);
            }

            _catalogVersion = snapshot.Version;
            _registryVersion = _registry.Version;
            _lastRowRefresh = now;
            _spinner++;
        }

        if (now - _lastRegistryCleanup >= RegistryCleanupInterval)
        {
            _registry.Cleanup(now);
            _lastRegistryCleanup = now;
        }

        if (_detailOpen)
        {
            RefreshDetail(snapshot, width, now);
        }
        else
        {
            UpdateFooter(snapshot, _sessions);
        }
    }

    private void RememberListSelection()
    {
        if (_detailOpen || _window.List.SelectedItem is not int index || index < 0 || index >= _sessions.Count)
        {
            return;
        }

        _selectedDirectory = _sessions[index].Directory;
        _selectedName = _sessions[index].Name;
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
        ClearHistory();
        _follow = true;
        _followSelection = -1;
        _titleState = null;
        _notice = null;
        _historyNotice = null;
        _window.ShowDetail();
        Refresh();
    }

    public void CloseDetail(string? notice = null)
    {
        _detailOpen = false;
        _history.Close();
        ClearHistory();
        _historyNotice = null;
        _liveLineCount = 0;
        _window.DetailLines.Clear();
        _detailLineInfo.Clear();
        _window.ShowList();
        _notice = notice;
        _catalogVersion = -1;
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
            if (_window.DetailLines.Count > 0)
            {
                _followSelection = _window.DetailLines.Count - 1;
                _window.DetailList.SelectedItem = _followSelection;
                _window.DetailList.EnsureSelectedItemVisible();
            }

            _follow = true;
            Refresh();
            key.Handled = true;
        }
    }

    private void ClearHistory()
    {
        _messages.Clear();
        _blocks.Clear();
        _historyLines.Clear();
        _formatState.Reset();
        _historyVersion = -1;
        _laidOutBlocks = 0;
        _layoutWidth = -1;
        _relayout = true;
    }

    private int ResolveWidth()
    {
        var width = _detailOpen && _window.DetailList.Viewport.Width > 0 ? _window.DetailList.Viewport.Width : _window.List.Viewport.Width;
        return width > 0 ? width : 120;
    }

    private void RebuildRows(int width, DateTimeOffset now)
    {
        var rows = new List<string>(_sessions.Count);
        var activityWidth = SessionRowFormatter.ActivityWidth(width);

        var tailCharacters = Math.Max(MinimumHintTailCharacters, activityWidth * HintTailFactor);

        foreach (var session in _sessions)
        {
            var live = _registry.GetSnapshot(session.Directory, tailCharacters);
            var activity = ActivityHint.Format(session, live, now, activityWidth);
            rows.Add(SessionRowFormatter.FormatRow(session, live?.IsConnected == true, activity, _spinner, width, now));
        }

        SetTextIfChanged(_window.Header, _sessions.Count == 0
            ? $"No sessions in {_window.Home}."
            : SessionRowFormatter.FormatHeader(width));
        ReplaceRows(rows);

        var selectedIndex = -1;

        if (_selectedDirectory != null)
        {
            selectedIndex = _sessions.FindIndex(session => string.Equals(session.Directory, _selectedDirectory, PlatformServices.Current.Paths.Comparison));
        }

        // The open session keeps its identity even when it leaves the list, so the detail view can detect a deletion:
        if (_detailOpen)
        {
            _window.List.SelectedItem = selectedIndex >= 0 ? selectedIndex : null;
            return;
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
        var changed = false;
        _window.List.SuspendCollectionChangedEvent();

        try
        {
            while (_window.Rows.Count > rows.Count)
            {
                _window.Rows.RemoveAt(_window.Rows.Count - 1);
                changed = true;
            }

            for (var index = 0; index < rows.Count; index++)
            {
                if (index < _window.Rows.Count)
                {
                    if (_window.Rows[index] != rows[index])
                    {
                        _window.Rows[index] = rows[index];
                        changed = true;
                    }
                }
                else
                {
                    _window.Rows.Add(rows[index]);
                    changed = true;
                }
            }
        }
        finally
        {
            ResumeList(_window.List, changed);
        }
    }

    private void RefreshDetail(CatalogSnapshot snapshot, int width, DateTimeOffset now)
    {
        var session = _selectedDirectory == null
            ? null
            : snapshot.Sessions.FirstOrDefault(candidate => string.Equals(candidate.Directory, _selectedDirectory, PlatformServices.Current.Paths.Comparison));

        if (session == null)
        {
            CloseDetail($"session {_selectedName ?? "the session"} was deleted");
            return;
        }

        var live = _registry.GetSnapshot(session.Directory);

        if (_window.DetailLines.Count > 0 && _window.DetailList.SelectedItem is int selectedLine)
        {
            _follow = IsFollowing(selectedLine);
        }

        UpdateDetailHeader(session, live, now);
        _history.RequestRefresh(live?.State.TranscriptCount);
        var history = _history.TakeChanges();
        _historyNotice = history.Notice;

        if (history.Version != _historyVersion)
        {
            if (history.Reset)
            {
                _messages.Clear();
                _blocks.Clear();
                _formatState.Reset();
                _relayout = true;
            }

            if (history.NewMessages.Count > 0)
            {
                _messages.AddRange(history.NewMessages);
                AppendBlocks(history.NewMessages);
            }

            _historyVersion = history.Version;
        }

        var liveLines = FormatLiveLines(live, width, now);

        if (_relayout || width != _layoutWidth)
        {
            _historyLines.Clear();
            HistoryLayout.Append(_blocks, width, false, _historyLines);
            _laidOutBlocks = _blocks.Count;
            _layoutWidth = width;
            _relayout = false;
            ReplaceDetailLines(liveLines);
        }
        else
        {
            var firstNewLine = _historyLines.Count;

            if (_laidOutBlocks < _blocks.Count)
            {
                HistoryLayout.Append(_blocks.Skip(_laidOutBlocks), width, false, _historyLines);
                _laidOutBlocks = _blocks.Count;
            }

            UpdateDetailTail(firstNewLine, liveLines);
        }

        if (_follow && _window.DetailLines.Count > 0)
        {
            FollowToEnd();
        }

        UpdateFooter(snapshot, _sessions);
    }

    private void FollowToEnd()
    {
        var last = _window.DetailLines.Count - 1;
        var viewport = _window.DetailList.Viewport;
        _followSelection = last;

        if (_window.DetailList.SelectedItem != last)
        {
            _window.DetailList.SelectedItem = last;
            _window.DetailList.EnsureSelectedItemVisible();
        }
        else if (viewport.Height > 0 && (viewport.Y + viewport.Height <= last || viewport.Y > last))
        {
            _window.DetailList.EnsureSelectedItemVisible();
        }
    }

    /// <summary>
    ///     Follows only while the last line is on screen. The mouse wheel scrolls without moving the selection, so while the selection is still the one follow mode set, the scroll position alone decides.
    /// </summary>
    private bool IsFollowing(int selectedLine)
    {
        var viewport = _window.DetailList.Viewport;
        var count = _window.DetailLines.Count;
        var atBottom = viewport.Height <= 0 || viewport.Y + viewport.Height >= count;

        if (selectedLine != _followSelection)
        {
            return atBottom && selectedLine >= count - 1;
        }

        return atBottom;
    }

    private void UpdateDetailHeader(SessionSummary session, LiveSessionSnapshot? live, DateTimeOffset now)
    {
        var lines = DetailHeaderFormatter.Format(session, live, _contextWindowLookup(session.ModelAlias), now);
        SetTextIfChanged(_window.DetailTitle, " " + lines[0]);
        SetTextIfChanged(_window.DetailHeader, string.Join("\n", lines.Skip(1)));

        if (_titleState != session.DisplayState)
        {
            _window.DetailTitle.SetScheme(MonitorColors.ForTitle(session.DisplayState));
            _titleState = session.DisplayState;
        }
    }

    private static void ResumeList(ListView list, bool changed)
    {
        list.ResumeSuspendCollectionChangedEvent();

        if (!changed)
        {
            return;
        }

        // Resuming does not replay the suppressed changes, so the scrollable size is updated here:
        list.SetContentSize(new System.Drawing.Size(Math.Max(1, list.Viewport.Width), list.Source?.Count ?? 0));
        list.SetNeedsDraw();
    }

    private static void SetTextIfChanged(Label label, string text)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }
    }

    private void AppendBlocks(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            _blocks.AddRange(HistoryFormatter.FormatMessage(message, _formatState, _showReasoning, _expandOutputs));
        }
    }

    private void ReformatBlocks()
    {
        _blocks.Clear();
        _formatState.Reset();
        AppendBlocks(_messages);
        _relayout = true;
        Refresh();
    }

    /// <summary>
    ///     Replaces the old live lines with the history lines laid out since the last tick, then the new live lines. Earlier lines keep their indices.
    /// </summary>
    private void UpdateDetailTail(int firstNewHistoryLine, IReadOnlyList<HistoryLine> liveLines)
    {
        var newHistoryLines = _historyLines.Count - firstNewHistoryLine;

        if (newHistoryLines == 0 && SameLiveLines(liveLines))
        {
            return;
        }

        _window.DetailList.SuspendCollectionChangedEvent();

        try
        {
            var remove = Math.Min(_liveLineCount, _window.DetailLines.Count);

            for (var index = 0; index < remove; index++)
            {
                _window.DetailLines.RemoveAt(_window.DetailLines.Count - 1);
                _detailLineInfo.RemoveAt(_detailLineInfo.Count - 1);
            }

            for (var index = firstNewHistoryLine; index < _historyLines.Count; index++)
            {
                _window.DetailLines.Add(_historyLines[index].Text);
                _detailLineInfo.Add(_historyLines[index]);
            }

            foreach (var line in liveLines)
            {
                _window.DetailLines.Add(line.Text);
                _detailLineInfo.Add(line);
            }
        }
        finally
        {
            ResumeList(_window.DetailList, true);
        }

        _liveLineCount = liveLines.Count;
    }

    private bool SameLiveLines(IReadOnlyList<HistoryLine> liveLines)
    {
        if (liveLines.Count != _liveLineCount || _detailLineInfo.Count < _liveLineCount)
        {
            return false;
        }

        var offset = _detailLineInfo.Count - _liveLineCount;

        for (var index = 0; index < liveLines.Count; index++)
        {
            if (_detailLineInfo[offset + index] != liveLines[index])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Shows the whole history layout followed by the live lines, rewriting only the lines whose text changed.
    /// </summary>
    private void ReplaceDetailLines(IReadOnlyList<HistoryLine> liveLines)
    {
        var total = _historyLines.Count + liveLines.Count;
        _window.DetailList.SuspendCollectionChangedEvent();

        try
        {
            while (_window.DetailLines.Count > total)
            {
                _window.DetailLines.RemoveAt(_window.DetailLines.Count - 1);
            }

            _detailLineInfo.Clear();

            for (var index = 0; index < total; index++)
            {
                var line = index < _historyLines.Count ? _historyLines[index] : liveLines[index - _historyLines.Count];
                _detailLineInfo.Add(line);

                if (index < _window.DetailLines.Count)
                {
                    if (_window.DetailLines[index] != line.Text)
                    {
                        _window.DetailLines[index] = line.Text;
                    }
                }
                else
                {
                    _window.DetailLines.Add(line.Text);
                }
            }
        }
        finally
        {
            ResumeList(_window.DetailList, true);
        }

        _liveLineCount = liveLines.Count;

        if (_followSelection >= total)
        {
            _followSelection = -1;
        }
    }

    private static List<HistoryLine> FormatLiveLines(LiveSessionSnapshot? live, int width, DateTimeOffset now)
    {
        var lines = new List<HistoryLine>();
        HistoryLayout.Append(HistoryFormatter.FormatLive(live, now), width, false, lines);
        return lines;
    }

    private TerminalAttribute? ListAttribute(int row)
    {
        if (row < 0 || row >= _sessions.Count || row == _window.List.SelectedItem)
        {
            return null;
        }

        return MonitorColors.ForListRow(_sessions[row].DisplayState);
    }

    private TerminalAttribute? DetailAttribute(int row)
    {
        if (row < 0 || row >= _detailLineInfo.Count)
        {
            return null;
        }

        // The selection highlight shows only once the user scrolls away from the live end:
        if (row == _window.DetailList.SelectedItem && row < _detailLineInfo.Count - 1)
        {
            return null;
        }

        var line = _detailLineInfo[row];
        return MonitorColors.ForHistoryLine(line.Kind, line.Role);
    }

    private void UpdateFooter(CatalogSnapshot snapshot, IReadOnlyList<SessionSummary> sessions)
    {
        var running = snapshot.Sessions.Count(session => session.DisplayState == "running");
        var approval = snapshot.Sessions.Count(session => session.DisplayState == "approval");
        var input = snapshot.Sessions.Sum(session => session.SessionCounters.InputTokens);
        var output = snapshot.Sessions.Sum(session => session.SessionCounters.OutputTokens);
        var cached = snapshot.Sessions.Sum(session => session.SessionCounters.CachedInputTokens);
        var cachedPercent = input > 0 ? (int)Math.Round(cached * 100.0 / input, MidpointRounding.AwayFromZero) : 0;
        var shownNotice = _detailOpen ? _historyNotice : _notice;
        var notice = shownNotice != null ? shownNotice + " | " : string.Empty;
        var keys = _detailOpen ? "esc back  r reasoning  o outputs  f follow  q back" : "enter open  a all  q quit";
        SetTextIfChanged(_window.Footer, $"{notice}{running} running, {approval} awaiting approval, {sessions.Count} shown of {snapshot.Sessions.Count} | in {Units.FormatTokens(input)} ({cachedPercent}% cached) out {Units.FormatTokens(output)} | live: {_server.Status} | {keys}");
    }
}
