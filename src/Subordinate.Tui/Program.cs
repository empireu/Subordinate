using System.Globalization;

namespace Subordinate.Tui;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var (options, error) = TuiCommandLine.Parse(args);

            if (error != null)
            {
                Console.Error.WriteLine(error);
                return 2;
            }

            if (options!.Help)
            {
                Console.Out.WriteLine(TuiCommandLine.HelpText);
                return 0;
            }

            if (options.Version)
            {
                Console.Out.WriteLine("subordinate-tui " + (typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown"));
                return 0;
            }

            if (!options.Print && RequiresInteractiveTerminal(Console.IsInputRedirected, Console.IsOutputRedirected))
            {
                Console.Error.WriteLine("subordinate-tui needs an interactive terminal. Use --print for a text snapshot.");
                return 2;
            }

            if (!options.Print)
            {
                Console.Error.WriteLine("The interactive monitor is not implemented yet.");
                return 1;
            }

            Console.Error.WriteLine("--print is not implemented yet.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"subordinate-tui stopped unexpectedly: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static bool RequiresInteractiveTerminal(bool inputRedirected, bool outputRedirected)
    {
        return inputRedirected || outputRedirected;
    }
}

internal sealed class TuiOptions
{
    public bool Print { get; set; }

    public bool All { get; set; }

    public bool Help { get; set; }

    public bool Version { get; set; }

    public bool PrintOnlyOptionUsed { get; set; }

    public string? Home { get; set; }

    public string? Session { get; set; }

    public string? Workdir { get; set; }

    public int Width { get; set; } = 120;

    public int WaitSeconds { get; set; }
}

internal static class TuiCommandLine
{
    public const string HelpText = """
        subordinate-tui - watch Subordinate sessions

        Usage:
          subordinate-tui [--home DIR] [--all]
          subordinate-tui --print [--home DIR] [--all] [--session NAME [--workdir DIR]] [--width N] [--wait-seconds S]
          subordinate-tui --help | --version

        Keys:
          up, down, page up, page down, home, end   move or scroll
          enter   open the selected session
          esc     go back to the list, or quit from the list
          q       go back to the list, or quit from the list
          a       show all sessions
          r       show or hide reasoning in the detail view
          o       show or hide full tool outputs in the detail view
          f       follow the live history
        """;

    public static (TuiOptions? Options, string? Error) Parse(IReadOnlyList<string> args)
    {
        var options = new TuiOptions();

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--help":
                case "-h":
                {
                    options.Help = true;
                    break;
                }
                case "--version":
                {
                    options.Version = true;
                    break;
                }
                case "--print":
                {
                    options.Print = true;
                    break;
                }
                case "--all":
                {
                    options.All = true;
                    break;
                }
                case "--home":
                {
                    if (!TryTakeValue(args, ref index, out var home))
                    {
                        return (null, "--home requires a directory.");
                    }

                    options.Home = home;
                    break;
                }
                case "--session":
                {
                    if (!TryTakeValue(args, ref index, out var session))
                    {
                        return (null, "--session requires a name.");
                    }

                    options.Session = session;
                    options.PrintOnlyOptionUsed = true;
                    break;
                }
                case "--workdir":
                {
                    if (!TryTakeValue(args, ref index, out var workdir))
                    {
                        return (null, "--workdir requires a directory.");
                    }

                    options.Workdir = workdir;
                    options.PrintOnlyOptionUsed = true;
                    break;
                }
                case "--width":
                {
                    if (!TryTakeValue(args, ref index, out var widthText) || !int.TryParse(widthText, NumberStyles.None, CultureInfo.InvariantCulture, out var width))
                    {
                        return (null, "--width requires a number.");
                    }

                    options.Width = width;
                    options.PrintOnlyOptionUsed = true;
                    break;
                }
                case "--wait-seconds":
                {
                    if (!TryTakeValue(args, ref index, out var secondsText) || !int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                    {
                        return (null, "--wait-seconds requires a number.");
                    }

                    options.WaitSeconds = seconds;
                    options.PrintOnlyOptionUsed = true;
                    break;
                }
                default:
                {
                    return (null, $"Unknown option \"{args[index]}\".");
                }
            }
        }

        if (options.Help || options.Version)
        {
            return (options, null);
        }

        if (!options.Print && options.PrintOnlyOptionUsed)
        {
            return (null, "--session, --workdir, --width and --wait-seconds require --print.");
        }

        if (options.Width < 40 || options.Width > 400)
        {
            return (null, "--width must be between 40 and 400.");
        }

        if (options.WaitSeconds < 0 || options.WaitSeconds > 60)
        {
            return (null, "--wait-seconds must be between 0 and 60.");
        }

        return (options, null);
    }

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int index, out string value)
    {
        if (index + 1 >= args.Count)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return true;
    }
}
