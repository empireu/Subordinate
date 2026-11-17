using System.Globalization;

namespace Subordinate.Tui.Presentation;

public static class Units
{
    public static string FormatTokens(long value)
    {
        if (value < 0)
        {
            value = 0;
        }

        if (value < 1_000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value < 100_000)
        {
            var tenths = Math.Round(value / 1_000.0, 1, MidpointRounding.AwayFromZero);

            if (tenths >= 100)
            {
                return FormatWholeThousands(value);
            }

            return tenths.ToString("F1", CultureInfo.InvariantCulture) + "k";
        }

        if (value < 1_000_000)
        {
            var whole = Math.Round(value / 1_000.0, 0, MidpointRounding.AwayFromZero);

            if (whole >= 1_000)
            {
                return FormatTenthsMillions(value);
            }

            return whole.ToString("F0", CultureInfo.InvariantCulture) + "k";
        }

        if (value < 10_000_000)
        {
            var tenths = Math.Round(value / 1_000_000.0, 1, MidpointRounding.AwayFromZero);

            if (tenths >= 10)
            {
                return FormatWholeMillions(value);
            }

            return tenths.ToString("F1", CultureInfo.InvariantCulture) + "M";
        }

        return FormatWholeMillions(value);
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        var total = (long)Math.Floor(seconds);

        if (total < 60)
        {
            return total.ToString(CultureInfo.InvariantCulture) + "s";
        }

        if (total < 3_600)
        {
            return (total / 60).ToString(CultureInfo.InvariantCulture) + "m" + (total % 60).ToString("D2", CultureInfo.InvariantCulture) + "s";
        }

        if (total < 86_400)
        {
            return (total / 3_600).ToString(CultureInfo.InvariantCulture) + "h" + ((total % 3_600) / 60).ToString("D2", CultureInfo.InvariantCulture) + "m";
        }

        return (total / 86_400).ToString(CultureInfo.InvariantCulture) + "d" + ((total % 86_400) / 3_600).ToString("D2", CultureInfo.InvariantCulture) + "h";
    }

    public static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return ((int)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        }

        if (age.TotalMinutes < 60)
        {
            return ((int)age.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m";
        }

        if (age.TotalHours < 24)
        {
            return ((int)age.TotalHours).ToString(CultureInfo.InvariantCulture) + "h";
        }

        return ((int)age.TotalDays).ToString(CultureInfo.InvariantCulture) + "d";
    }

    public static int ContextBarCells(double fraction)
    {
        return (int)Math.Clamp(Math.Round(fraction * 20, MidpointRounding.AwayFromZero), 0, 20);
    }

    public static int ContextPercent(double fraction)
    {
        return (int)Math.Clamp(Math.Round(fraction * 100, MidpointRounding.AwayFromZero), 0, 100);
    }

    public static int TokensPerSecond(long outputTokens, double durationSeconds)
    {
        return durationSeconds <= 0 ? 0 : (int)Math.Round(outputTokens / durationSeconds, MidpointRounding.AwayFromZero);
    }

    private static string FormatWholeThousands(long value)
    {
        return Math.Round(value / 1_000.0, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + "k";
    }

    private static string FormatTenthsMillions(long value)
    {
        return Math.Round(value / 1_000_000.0, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture) + "M";
    }

    private static string FormatWholeMillions(long value)
    {
        return Math.Round(value / 1_000_000.0, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + "M";
    }
}
