using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Subordinate.Core.Json;

/// <summary>
///     Writes JSON whose text contains only printable ASCII. Non-ASCII characters inside strings are escaped, while quotes, angle brackets and ampersands stay readable.
/// </summary>
public static class AsciiJson
{
    private static readonly JsonWriterOptions CompactOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false
    };

    private static readonly JsonWriterOptions IndentedOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
        NewLine = "\n"
    };

    public static string Write(Action<Utf8JsonWriter> build, bool indented)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, indented ? IndentedOptions : CompactOptions))
        {
            build(writer);
        }

        return EscapeNonAscii(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    public static string EscapeNonAscii(string json)
    {
        var needsEscaping = false;
        foreach (var character in json)
        {
            if (character > '~')
            {
                needsEscaping = true;
                break;
            }
        }

        if (!needsEscaping)
        {
            return json;
        }

        var builder = new StringBuilder(json.Length + 32);
        foreach (var character in json)
        {
            if (character > '~')
            {
                builder.Append("\\u");
                builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    public static void WriteStringArray(this Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    public static void WriteNullableString(this Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value == null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }
}
