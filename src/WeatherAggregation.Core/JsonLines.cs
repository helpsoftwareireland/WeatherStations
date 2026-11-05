using System.Buffers;
using System.Runtime.CompilerServices;

namespace WeatherAggregation.Core;

public static class JsonLines
{
    // The yielded memory remains valid until the next MoveNext call; consumers must not retain it.
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(Stream stream, int maxLineBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var input = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var line = ArrayPool<byte>.Shared.Rent(checked(maxLineBytes + 1));
        var length = 0;
        long number = 1;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(input.AsMemory(0, 64 * 1024), cancellationToken)) != 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (input[i] == (byte)'\n')
                    {
                        var count = length > 0 && line[length - 1] == (byte)'\r' ? length - 1 : length;
                        if (count > maxLineBytes) throw TooLong();
                        yield return line.AsMemory(0, count);
                        length = 0;
                        number++;
                    }
                    else
                    {
                        if (length == maxLineBytes + 1) throw TooLong();
                        line[length++] = input[i];
                    }
                }
            }
            if (length > 0)
            {
                if (length > maxLineBytes) throw TooLong();
                yield return line.AsMemory(0, length);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(line);
        }
        InputException TooLong() => new("line_too_long", "The JSONL record exceeds the configured line limit.", number, 413);
    }
}
