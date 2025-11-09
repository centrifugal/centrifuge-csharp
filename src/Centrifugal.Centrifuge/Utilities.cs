using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Utility methods for the Centrifuge client.
    /// </summary>
    internal static class Utilities
    {
        private static readonly Random Random = new Random();

        /// <summary>
        /// Calculates backoff delay with full jitter.
        /// Full jitter technique from: https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
        /// </summary>
        /// <param name="attempt">The attempt number (0-based).</param>
        /// <param name="minDelay">Minimum delay.</param>
        /// <param name="maxDelay">Maximum delay.</param>
        /// <returns>Delay in milliseconds.</returns>
        public static int CalculateBackoff(int attempt, TimeSpan minDelay, TimeSpan maxDelay)
        {
            if (attempt < 0) attempt = 0;
            if (attempt > 31) attempt = 31; // Prevent overflow

            int minDelayMs = (int)minDelay.TotalMilliseconds;
            int maxDelayMs = (int)maxDelay.TotalMilliseconds;

            // Calculate exponential backoff: minDelay * 2^attempt
            double exponential = minDelayMs * Math.Pow(2, attempt);

            // Calculate the random interval: [0, min(maxDelay, minDelay * 2^attempt)]
            int intervalMax = (int)Math.Min(maxDelayMs, exponential);

            int interval;
            lock (Random)
            {
                interval = Random.Next(0, intervalMax + 1);
            }

            // Return min + interval, capped at maxDelay
            return Math.Min(maxDelayMs, minDelayMs + interval);
        }

        /// <summary>
        /// Converts TTL in seconds to milliseconds, accounting for clock skew.
        /// </summary>
        /// <param name="ttl">TTL in seconds.</param>
        /// <returns>TTL in milliseconds, reduced by a small amount to account for network delays.</returns>
        public static int TtlToMilliseconds(uint ttl)
        {
            if (ttl == 0) return 0;

            // Reduce by 5% to account for clock skew and network delays
            double ms = ttl * 1000 * 0.95;
            return (int)Math.Max(1, ms);
        }
    }

    /// <summary>
    /// Varint encoder/decoder for Protobuf messages.
    /// </summary>
    internal static class VarintCodec
    {
        /// <summary>
        /// Reads a varint-delimited message from a stream asynchronously.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="buffer">Buffer for reading.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The message bytes, or null if stream ended.</returns>
        public static async Task<byte[]?> ReadDelimitedMessageAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            // Read the varint length prefix
            int length = await ReadVarintAsync(stream, cancellationToken).ConfigureAwait(false);
            if (length < 0)
            {
                return null; // End of stream
            }

            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            // Read the message data
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(data, offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of stream while reading message data");
                }
                offset += read;
            }

            return data;
        }

        /// <summary>
        /// Reads a varint-delimited message from a stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="buffer">Buffer for reading.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The message bytes, or null if stream ended.</returns>
        public static byte[]? ReadDelimitedMessage(Stream stream, byte[] buffer, CancellationToken cancellationToken)
        {
            // Read the varint length prefix
            int length = ReadVarint(stream, cancellationToken);
            if (length < 0)
            {
                return null; // End of stream
            }

            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            // Read the message data
            byte[] data = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(data, offset, length - offset);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of stream while reading message data");
                }
                offset += read;
            }

            return data;
        }

        /// <summary>
        /// Writes a varint-delimited message to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="data">The message data.</param>
        public static void WriteDelimitedMessage(Stream stream, byte[] data)
        {
            // Write the varint length prefix
            WriteVarint(stream, data.Length);

            // Write the message data
            stream.Write(data, 0, data.Length);
        }

        /// <summary>
        /// Reads a varint from a stream asynchronously.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The decoded varint value, or -1 if end of stream.</returns>
        private static async Task<int> ReadVarintAsync(Stream stream, CancellationToken cancellationToken)
        {
            int result = 0;
            int shift = 0;
            byte[] buffer = new byte[1];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = await stream.ReadAsync(buffer, 0, 1, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0)
                {
                    return shift == 0 ? -1 : throw new IOException("Unexpected end of stream while reading varint");
                }

                byte b = buffer[0];
                result |= (b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift >= 32)
                {
                    throw new IOException("Varint too long");
                }
            }
        }

        /// <summary>
        /// Reads a varint from a stream.
        /// </summary>
        /// <param name="stream">The stream to read from.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The decoded varint value, or -1 if end of stream.</returns>
        private static int ReadVarint(Stream stream, CancellationToken cancellationToken)
        {
            int result = 0;
            int shift = 0;
            byte[] buffer = new byte[1];

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesRead = stream.Read(buffer, 0, 1);
                if (bytesRead == 0)
                {
                    return shift == 0 ? -1 : throw new IOException("Unexpected end of stream while reading varint");
                }

                byte b = buffer[0];
                result |= (b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
                if (shift >= 32)
                {
                    throw new IOException("Varint too long");
                }
            }
        }

        /// <summary>
        /// Writes a varint to a stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        /// <param name="value">The value to encode.</param>
        private static void WriteVarint(Stream stream, int value)
        {
            uint uvalue = (uint)value;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(5);
            try
            {
                int index = 0;
                while (uvalue > 0x7F)
                {
                    buffer[index++] = (byte)((uvalue & 0x7F) | 0x80);
                    uvalue >>= 7;
                }
                buffer[index++] = (byte)uvalue;
                stream.Write(buffer, 0, index);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
