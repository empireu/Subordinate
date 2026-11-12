using System.Runtime.CompilerServices;
using System.Text;

namespace KnowledgeSystem.Ai.Providers.Transport;

public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true
        );

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line == null)
            {
                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();

            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                yield break;
            }

            yield return data;
        }
    }
}
