using Subordinate.Core.Sessions;
using Subordinate.Tui.Presentation;
using Terminal.Gui.Drawing;
using TerminalAttribute = Terminal.Gui.Drawing.Attribute;

namespace Subordinate.Tui.Interface;

/// <summary>
///     The Tokyo Night palette.
/// </summary>
public static class MonitorColors
{
    private static readonly Color Background = new(0x1a, 0x1b, 0x26);
    private static readonly Color BackgroundDark = new(0x16, 0x16, 0x1e);
    private static readonly Color Surface = new(0x29, 0x2e, 0x42);
    private static readonly Color SurfaceStrong = new(0x3b, 0x42, 0x61);
    private static readonly Color Selection = new(0x28, 0x34, 0x57);
    private static readonly Color Foreground = new(0xc0, 0xca, 0xf5);
    private static readonly Color ForegroundDark = new(0xa9, 0xb1, 0xd6);
    private static readonly Color Muted = new(0x73, 0x7a, 0xa2);
    private static readonly Color Comment = new(0x56, 0x5f, 0x89);
    private static readonly Color Blue = new(0x7a, 0xa2, 0xf7);
    private static readonly Color Cyan = new(0x7d, 0xcf, 0xff);
    private static readonly Color Green = new(0x9e, 0xce, 0x6a);
    private static readonly Color Yellow = new(0xe0, 0xaf, 0x68);
    private static readonly Color Red = new(0xf7, 0x76, 0x8e);
    private static readonly Color Magenta = new(0xbb, 0x9a, 0xf7);
    private static readonly Color Teal = new(0x73, 0xda, 0xca);
    private static readonly Color Orange = new(0xff, 0x9e, 0x64);

    public static readonly Scheme Window = new(new TerminalAttribute(Foreground, Background))
    {
        Focus = new TerminalAttribute(Foreground, Selection),
        Active = new TerminalAttribute(Foreground, Selection),
        HotNormal = new TerminalAttribute(Blue, Background),
        HotFocus = new TerminalAttribute(Blue, Selection)
    };

    public static readonly Scheme Bar = new(new TerminalAttribute(ForegroundDark, BackgroundDark));

    public static readonly Scheme Warning = new(new TerminalAttribute(Background, Red));

    public static readonly Scheme Rule = new(new TerminalAttribute(Comment, Background));

    private static readonly TerminalAttribute RunningText = new(Green, Background);
    private static readonly TerminalAttribute ApprovalText = new(Yellow, Background);
    private static readonly TerminalAttribute FailureText = new(Red, Background);
    private static readonly TerminalAttribute CommentText = new(Comment, Background);
    private static readonly TerminalAttribute MutedText = new(Muted, Background);
    private static readonly TerminalAttribute OutputText = new(ForegroundDark, Background);
    private static readonly TerminalAttribute LiveText = new(Cyan, Background);

    public static TerminalAttribute? ForListRow(string displayState)
    {
        return displayState switch
        {
            SessionStates.Starting or SessionStates.Running => RunningText,
            "approval" => ApprovalText,
            "failed" or "interrupted" => FailureText,
            _ => null
        };
    }

    public static Scheme ForTitle(string displayState)
    {
        var background = displayState switch
        {
            SessionStates.Starting or SessionStates.Running => Green,
            "approval" => Yellow,
            "failed" or "interrupted" => Red,
            _ => SurfaceStrong
        };
        var foreground = background == SurfaceStrong ? Foreground : Background;
        return new Scheme(new TerminalAttribute(foreground, background));
    }

    public static TerminalAttribute? ForHistoryLine(HistoryBlockKind kind, HistoryBlockRole role)
    {
        return role switch
        {
            HistoryBlockRole.Spacer => null,
            HistoryBlockRole.Header => new TerminalAttribute(kind switch
            {
                HistoryBlockKind.Task => Magenta,
                HistoryBlockKind.Supervisor => Yellow,
                HistoryBlockKind.Thinking => Muted,
                HistoryBlockKind.Assistant => Green,
                HistoryBlockKind.Tool => Blue,
                HistoryBlockKind.Live => Cyan,
                HistoryBlockKind.Compaction => Orange,
                _ => Foreground
            }, Surface),
            HistoryBlockRole.Note => kind switch
            {
                HistoryBlockKind.ToolResult => RunningText,
                HistoryBlockKind.ToolFailure => FailureText,
                HistoryBlockKind.Warning => ApprovalText,
                HistoryBlockKind.Compaction => new TerminalAttribute(Orange, Background),
                _ => CommentText
            },
            _ => kind switch
            {
                HistoryBlockKind.Thinking => MutedText,
                HistoryBlockKind.ToolResult => OutputText,
                HistoryBlockKind.Live => LiveText,
                _ => null
            }
        };
    }

    /// <summary>
    ///     Applies a Markdown style on top of the attribute a line is drawn with, keeping a selection background.
    /// </summary>
    public static TerminalAttribute ForMarkdown(TerminalAttribute line, MarkdownStyle style)
    {
        if (style == MarkdownStyle.None)
        {
            return line;
        }

        var foreground = line.Foreground;
        var background = line.Background;
        var textStyle = line.Style;

        if (style.HasFlag(MarkdownStyle.CodeBlock) && background == Background)
        {
            background = BackgroundDark;
        }

        if (style.HasFlag(MarkdownStyle.Code))
        {
            foreground = Teal;
        }

        if (style.HasFlag(MarkdownStyle.Heading))
        {
            foreground = Blue;
            textStyle |= TextStyle.Bold;
        }

        if (style.HasFlag(MarkdownStyle.Link))
        {
            foreground = Cyan;
            textStyle |= TextStyle.Underline;
        }

        if (style.HasFlag(MarkdownStyle.Marker))
        {
            foreground = Comment;
        }

        if (style.HasFlag(MarkdownStyle.Bold))
        {
            textStyle |= TextStyle.Bold;
        }

        if (style.HasFlag(MarkdownStyle.Italic))
        {
            textStyle |= TextStyle.Italic;
        }

        if (style.HasFlag(MarkdownStyle.Strikethrough))
        {
            textStyle |= TextStyle.Strikethrough;
        }

        return new TerminalAttribute(foreground, background, textStyle);
    }
}
