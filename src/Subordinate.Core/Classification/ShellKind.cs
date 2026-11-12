namespace Subordinate.Core.Classification;

/// <summary>
///     The shell that executes commands. The parser is always selected from this, never from the command text.
/// </summary>
public enum ShellKind
{
    Bash,
    WindowsPowerShell,
    PowerShell7
}

public static class ShellKindNames
{
    public static string ToToken(this ShellKind shell)
    {
        return shell switch
        {
            ShellKind.Bash => "bash",
            ShellKind.WindowsPowerShell => "powershell",
            ShellKind.PowerShell7 => "pwsh",
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
        };
    }

    public static string DialectName(this ShellKind shell)
    {
        return shell switch
        {
            ShellKind.Bash => "bash",
            ShellKind.WindowsPowerShell => "WindowsPowerShell51",
            ShellKind.PowerShell7 => "PowerShell7",
            _ => throw new ArgumentOutOfRangeException(nameof(shell), shell, null)
        };
    }

    public static bool TryParse(string token, out ShellKind shell)
    {
        switch (token.Trim().ToLowerInvariant())
        {
            case "bash":
            {
                shell = ShellKind.Bash;
                return true;
            }
            case "powershell":
            {
                shell = ShellKind.WindowsPowerShell;
                return true;
            }
            case "pwsh":
            {
                shell = ShellKind.PowerShell7;
                return true;
            }
            default:
            {
                shell = default;
                return false;
            }
        }
    }
}
