using System.Text;

namespace Subordinate.Tui.Presentation;

public static class TerminalText
{
    public static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];

            if (character == '\r')
            {
                builder.Append('\n');

                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            if (character is '\n' or '\t')
            {
                builder.Append(character);
                continue;
            }

            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    builder.Append(character).Append(text[index + 1]);
                    index++;
                }
                else
                {
                    builder.Append('?');
                }

                continue;
            }

            if (char.IsLowSurrogate(character))
            {
                builder.Append('?');
                continue;
            }

            if (character < ' ')
            {
                builder.Append('^').Append((char)(character + 0x40));
                continue;
            }

            if (character == '\u007f')
            {
                builder.Append("^?");
                continue;
            }

            if (character is >= '\u0080' and <= '\u009f')
            {
                builder.Append('?');
                continue;
            }

            if (character is >= '\u202a' and <= '\u202e' or >= '\u2066' and <= '\u2069' or '\ufeff')
            {
                builder.Append('?');
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
