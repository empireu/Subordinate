using System.Collections.ObjectModel;
using System.Text;
using Subordinate.Tui.Presentation;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

namespace Subordinate.Tui.Interface;

/// <summary>
///     Draws list lines with a <see cref="MarkdownStyle" /> per character on top of the row attribute. Lines without styles draw like a plain list.
/// </summary>
public sealed class StyledListSource : ListWrapper<string>, IListDataSource
{
    private readonly ObservableCollection<string> _lines;
    private readonly Func<int, string?> _styles;

    public StyledListSource(ObservableCollection<string> lines, Func<int, string?> styles) : base(lines)
    {
        _lines = lines;
        _styles = styles;
    }

    public new void Render(ListView listView, bool selected, int item, int col, int row, int width, int viewportX = 0)
    {
        var text = item >= 0 && item < _lines.Count ? _lines[item] : null;
        var styles = text == null ? null : _styles(item);

        if (text == null || styles == null || styles.Length != text.Length || viewportX > 0)
        {
            base.Render(listView, selected, item, col, row, width, viewportX);
            return;
        }

        listView.Move(col, row);
        var lineAttribute = listView.GetCurrentAttribute();
        var used = 0;
        var index = 0;
        var full = false;

        while (index < text.Length && !full)
        {
            var style = styles[index];
            var end = index;
            var runColumns = 0;

            while (end < text.Length && styles[end] == style)
            {
                var columns = 1;
                var consumed = 1;

                if (Rune.DecodeFromUtf16(text.AsSpan(end), out var rune, out var length) == System.Buffers.OperationStatus.Done)
                {
                    columns = Math.Max(0, rune.GetColumns());
                    consumed = length;
                }

                if (used + runColumns + columns > width)
                {
                    full = true;
                    break;
                }

                runColumns += columns;
                end += consumed;
            }

            if (end > index)
            {
                listView.SetAttribute(MonitorColors.ForMarkdown(lineAttribute, (MarkdownStyle)style));
                listView.AddStr(text[index..end]);
                used += runColumns;
            }

            index = end;
        }

        // ListView caches the attribute it set for the row, so it has to be current again when this returns:
        listView.SetAttribute(lineAttribute);

        for (; used < width; used++)
        {
            listView.AddRune(new Rune(' '));
        }
    }
}
