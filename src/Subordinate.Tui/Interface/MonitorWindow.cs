using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Subordinate.Tui.Interface;

public sealed class MonitorWindow : IDisposable
{
    public MonitorWindow(string home)
    {
        Home = home;
        Window = new Window { Title = $"Subordinate monitor - {home}" };
        Header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        List = new ListView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(1) };
        DetailTitle = new Label { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Visible = false };
        DetailHeader = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 5, Visible = false };
        DetailRule = new Line { X = 0, Y = 6, Width = Dim.Fill(), Visible = false };
        DetailList = new ListView { X = 0, Y = 7, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
        Footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        Window.SetScheme(MonitorColors.Window);
        Header.SetScheme(MonitorColors.Bar);
        Footer.SetScheme(MonitorColors.Bar);
        DetailRule.SetScheme(MonitorColors.Rule);
        Window.Add(Header, List, DetailTitle, DetailHeader, DetailRule, DetailList, Footer);
        List.KeystrokeNavigator = null;
        DetailList.KeystrokeNavigator = null;
        List.SetSource(Rows);
        DetailList.SetSource(DetailLines);
    }

    public string Home { get; }

    public Window Window { get; }

    public Label Header { get; }

    public ListView List { get; }

    public Label DetailTitle { get; }

    public Label DetailHeader { get; }

    public Line DetailRule { get; }

    public ListView DetailList { get; }

    public Label Footer { get; }

    public ObservableCollection<string> Rows { get; } = [];

    public ObservableCollection<string> DetailLines { get; } = [];

    public void ShowList()
    {
        Header.Visible = true;
        List.Visible = true;
        DetailTitle.Visible = false;
        DetailHeader.Visible = false;
        DetailRule.Visible = false;
        DetailList.Visible = false;
        List.SetFocus();
    }

    public void ShowDetail()
    {
        Header.Visible = false;
        List.Visible = false;
        DetailTitle.Visible = true;
        DetailHeader.Visible = true;
        DetailRule.Visible = true;
        DetailList.Visible = true;
        DetailList.SetFocus();
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}
