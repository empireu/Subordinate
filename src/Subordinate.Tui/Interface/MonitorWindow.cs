using System.Collections.ObjectModel;
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
        DetailHeader = new Label { X = 0, Y = 1, Width = Dim.Fill(), Height = 6, Visible = false };
        DetailList = new ListView { X = 0, Y = 7, Width = Dim.Fill(), Height = Dim.Fill(1), Visible = false };
        Footer = new Label { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        Window.Add(Header, List, DetailHeader, DetailList, Footer);
        List.KeystrokeNavigator = null;
        DetailList.KeystrokeNavigator = null;
        List.SetSource(Rows);
        DetailList.SetSource(DetailLines);
    }

    public string Home { get; }

    public Window Window { get; }

    public Label Header { get; }

    public ListView List { get; }

    public Label DetailHeader { get; }

    public ListView DetailList { get; }

    public Label Footer { get; }

    public ObservableCollection<string> Rows { get; } = [];

    public ObservableCollection<string> DetailLines { get; } = [];

    public void ShowList()
    {
        Header.Visible = true;
        List.Visible = true;
        DetailHeader.Visible = false;
        DetailList.Visible = false;
        List.SetFocus();
    }

    public void ShowDetail()
    {
        Header.Visible = false;
        List.Visible = false;
        DetailHeader.Visible = true;
        DetailList.Visible = true;
        DetailList.SetFocus();
    }

    public void Dispose()
    {
        Window.Dispose();
    }
}
