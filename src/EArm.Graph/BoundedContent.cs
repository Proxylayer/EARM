using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph;

/// <summary>
/// Reads a response body with a hard size limit. The limit applies to the bytes the caller receives, that is AFTER the handler's automatic
/// decompression, so a small gzip bomb is cut off as soon as it inflates past the limit (nothing larger than the limit is ever held in memory
/// beyond one read buffer).
/// </summary>
internal static class BoundedContent
{
    private const int BufferSize = 81_920;

    /// <summary>The whole body, or <paramref name="tooLarge"/> thrown once it exceeds <paramref name="maxBytes"/>.</summary>
    /// <param name="content">The response content (headers already read).</param>
    /// <param name="maxBytes">The limit in bytes; a body of exactly this size is accepted.</param>
    /// <param name="tooLarge">Creates the exception to throw on overflow.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<byte[]> ReadAsync(HttpContent content, long maxBytes, Func<Exception> tooLarge, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(tooLarge);

        // A declared length over the limit is refused before reading anything (the real length is checked while reading either way).
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            throw tooLarge();
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var output = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                long total = 0;
                while (true)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        return output.ToArray();
                    }

                    total += read;
                    if (total > maxBytes)
                    {
                        throw tooLarge();
                    }

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
