using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Waiters for a state (ReadyAsync, a method call waiting for the connection or the subscription), owned by
    /// an event loop: added, resolved and rejected in its steps. A waiter's timeout and cancellation are steps
    /// too.
    /// </summary>
    internal sealed class Waiters
    {
        private readonly EventLoop _loop;
        private readonly Dictionary<int, Waiter> _waiters = new Dictionary<int, Waiter>();
        private int _nextId;

        private sealed class Waiter
        {
            public Waiter(Action resolve, Action<Exception> reject)
            {
                Resolve = resolve;
                Reject = reject;
            }

            public Action Resolve { get; }

            public Action<Exception> Reject { get; }

            public LoopTimer? Timer { get; set; }

            public CancellationTokenRegistration Registration { get; set; }
        }

        public Waiters(EventLoop loop) => _loop = loop;

        /// <summary>Adds a waiter, failed with <see cref="CentrifugeTimeoutException"/> after
        /// <paramref name="timeoutMs"/> (none — no timeout) and with <see cref="OperationCanceledException"/> on
        /// cancellation. Call in a step.</summary>
        public void Add(Action resolve, Action<Exception> reject, int? timeoutMs, CancellationToken cancellationToken)
        {
            var id = ++_nextId;
            var waiter = new Waiter(resolve, reject);
            _waiters[id] = waiter;
            if (timeoutMs is { } ms)
            {
                waiter.Timer = LoopTimer.Start(_loop, ms, () =>
                {
                    if (Remove(id)) reject(new CentrifugeTimeoutException("timeout"));
                });
            }
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => _loop.Execute(() =>
                {
                    if (Remove(id)) reject(new OperationCanceledException(cancellationToken));
                }));
            }
        }

        private bool Remove(int id)
        {
            if (!_waiters.TryGetValue(id, out var waiter)) return false;
            _waiters.Remove(id);
            waiter.Timer?.Cancel();
            waiter.Registration.Dispose();
            return true;
        }

        /// <summary>Resolves every waiter. Call in a step.</summary>
        public void ResolveAll()
        {
            foreach (var waiter in TakeAll()) waiter.Resolve();
        }

        /// <summary>Rejects all pending waiters with <paramref name="error"/>. Call in a step.</summary>
        public void RejectAll(Exception error)
        {
            foreach (var waiter in TakeAll()) waiter.Reject(error);
        }

        private List<Waiter> TakeAll()
        {
            var all = new List<Waiter>(_waiters.Values);
            _waiters.Clear();
            foreach (var waiter in all)
            {
                waiter.Timer?.Cancel();
                waiter.Registration.Dispose();
            }
            return all;
        }

        /// <summary>Fails <paramref name="completion"/>: cancelled for a cancellation, faulted otherwise.</summary>
        public static void Fail<T>(TaskCompletionSource<T> completion, Exception error)
        {
            if (error is OperationCanceledException canceled)
                completion.TrySetCanceled(canceled.CancellationToken);
            else
                completion.TrySetException(error);
        }
    }

    /// <summary>Application callbacks (GetToken, GetState): run off the loop, their outcome applied in a step.</summary>
    internal static class Callbacks
    {
        /// <summary>
        /// Runs <paramref name="callback"/> on the thread pool — a callback blocking before it returns its task
        /// doesn't hold the loop — and then <paramref name="onResult"/> or <paramref name="onError"/> in a step
        /// of <paramref name="loop"/>. A callback throwing, returning no task or a cancelled one fails.
        /// </summary>
        public static void Run<T>(EventLoop loop, Func<Task<T>> callback, Action<T> onResult, Action<Exception> onError)
        {
            Task.Run(callback).ContinueWith(task => loop.Execute(() =>
            {
                if (task.IsFaulted)
                    onError(task.Exception!.InnerExceptions.Count == 1 ? task.Exception.InnerException! : task.Exception);
                else if (task.IsCanceled)
                    onError(new OperationCanceledException("callback cancelled"));
                else
                    onResult(task.Result);
            }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Event raising for clients and subscriptions. An exception thrown by a handler goes to the
    /// owner's Error event and is returned to the owner, which stops the client (see
    /// CentrifugeClient.HandlerFailed); the SDK's own processing goes on as if the handler had returned.
    /// </summary>
    internal static class EventDispatch
    {
        /// <summary>Cached invocation lists, one per (immutable) delegate instance, so a raise doesn't
        /// allocate a new list.</summary>
        private static readonly ConditionalWeakTable<Delegate, Delegate[]> InvocationLists = new ConditionalWeakTable<Delegate, Delegate[]>();

        /// <summary>
        /// Calls the subscribers of an event one after another. If one throws, the exception is raised
        /// as an Error of type <paramref name="type"/>, or just logged when nobody listens to Error, the
        /// remaining subscribers are not called (the client is about to stop, the event is outdated for
        /// them), and the exception is returned. Null when no handler threw.
        /// </summary>
        public static Exception? Raise<TArgs>(object sender, EventHandler<TArgs>? handler, TArgs args, string type,
            EventHandler<CentrifugeErrorEventArgs>? error, ILogger? logger)
        {
            if (handler == null) return null;
#if NET9_0_OR_GREATER
            if (handler.HasSingleTarget) return Invoke(handler);
#endif
            foreach (var subscriber in InvocationListOf(handler))
            {
                var failure = Invoke((EventHandler<TArgs>)subscriber);
                if (failure != null) return failure;
            }
            return null;

            Exception? Invoke(EventHandler<TArgs> subscriber)
            {
                try
                {
                    subscriber(sender, args);
                    return null;
                }
                catch (Exception ex)
                {
                    if (error == null) Log(logger, ex, type + " handler threw");
                    else RaiseError(sender, error, type, ex, logger);
                    return ex;
                }
            }
        }

        /// <summary>
        /// Calls the Error subscribers one after another, taking code and temporary flag from a
        /// <see cref="CentrifugeException"/> when there is one. An Error handler that throws has nowhere
        /// else to report to, so its exception is only logged; it is returned, like one of any handler.
        /// </summary>
        public static Exception? RaiseError(object sender, EventHandler<CentrifugeErrorEventArgs>? handler,
            string type, Exception exception, ILogger? logger)
        {
            if (handler == null) return null;
            var args = exception is CentrifugeException centrifugeException
                ? new CentrifugeErrorEventArgs(type, centrifugeException.Code, exception.Message, centrifugeException.Temporary, exception)
                : new CentrifugeErrorEventArgs(type, 0, exception.Message, false, exception);
            return Raise(sender, handler, args, "Error event", null, logger);
        }

        private static Delegate[] InvocationListOf(Delegate handler) =>
            InvocationLists.GetValue(handler, d => d.GetInvocationList());

        private static void Log(ILogger? logger, Exception exception, string message) =>
            logger?.LogError(exception, message);
    }

    /// <summary>
    /// Wraps the application's logger and swallows anything it throws, so a faulty logger can't break
    /// a state transition, a transport callback or error reporting.
    /// </summary>
    internal sealed class GuardedLogger : ILogger
    {
        private readonly ILogger _inner;

        private GuardedLogger(ILogger inner) => _inner = inner;

        public static ILogger? Wrap(ILogger? logger) => logger == null ? null : new GuardedLogger(logger);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            try
            {
                _inner.Log(logLevel, eventId, state, exception, formatter);
            }
            catch
            {
            }
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            try
            {
                return _inner.IsEnabled(logLevel);
            }
            catch
            {
                return false;
            }
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            try
            {
                return _inner.BeginScope(state);
            }
            catch
            {
                return NoScope.Instance;
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new NoScope();

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Utility methods for the Centrifuge client.
    /// </summary>
    internal static class Utilities
    {
        private static readonly Random Random = new Random();

        /// <summary>Largest interval accepted by timers on all supported runtimes.</summary>
        public static readonly TimeSpan MaxTimerInterval = TimeSpan.FromMilliseconds(int.MaxValue);

        /// <summary>Accepts no timeout, an infinite one, or one in 0..<see cref="MaxTimerInterval"/>; throws otherwise.</summary>
        public static void ValidateWaitTimeout(TimeSpan? timeout, string paramName)
        {
            if (timeout is { } t && t != System.Threading.Timeout.InfiniteTimeSpan && (t < TimeSpan.Zero || t > MaxTimerInterval))
                throw new ArgumentOutOfRangeException(paramName, t, "The timeout must be infinite or between zero and MaxTimerInterval.");
        }

        /// <summary>
        /// Calculates backoff delay with full jitter.
        /// Full jitter technique from: https://aws.amazon.com/blogs/architecture/exponential-backoff-and-jitter/
        /// </summary>
        /// <param name="attempt">The attempt number (0-based).</param>
        /// <param name="minDelay">Minimum delay.</param>
        /// <param name="maxDelay">Maximum delay.</param>
        /// <returns>Delay in milliseconds. Never below 1, so a retry loop always yields between attempts.</returns>
        public static int CalculateBackoff(int attempt, TimeSpan minDelay, TimeSpan maxDelay)
        {
            if (attempt < 0) attempt = 0;
            if (attempt > 31) attempt = 31; // Prevent overflow

            double minDelayMs = minDelay.TotalMilliseconds;
            double maxDelayMs = maxDelay.TotalMilliseconds;

            // Calculate exponential backoff: minDelay * 2^attempt
            double exponential = minDelayMs * Math.Pow(2, attempt);

            // Calculate the random interval: [0, min(maxDelay, minDelay * 2^attempt)]
            double intervalMax = Math.Min(maxDelayMs, exponential);

            double interval;
            lock (Random)
            {
                interval = Random.NextDouble() * intervalMax;
            }

            // Return min + interval, capped at maxDelay
            return Math.Max(1, (int)Math.Min(maxDelayMs, minDelayMs + interval));
        }

        /// <summary>
        /// Converts TTL in seconds to milliseconds, accounting for clock skew.
        /// </summary>
        /// <param name="ttl">TTL in seconds.</param>
        /// <returns>
        /// TTL in milliseconds, reduced by a small amount to account for network delays,
        /// clamped to <see cref="int.MaxValue"/> (~24.8 days).
        /// </returns>
        public static int TtlToMilliseconds(uint ttl)
        {
            if (ttl == 0) return 0;

            // Reduce by 5% to account for clock skew and network delays.
            // Widen to double before multiplying: uint arithmetic wraps for TTLs above
            // ~49 days (e.g. a 1 year token TTL), which would schedule the refresh far
            // too early - a 49.7 day TTL wrapped to a ~670ms delay.
            double ms = (double)ttl * 1000 * 0.95;

            // Clamp so the result stays a valid Timer due time. Refreshing a token
            // earlier than strictly necessary is harmless.
            if (ms > int.MaxValue) return int.MaxValue;

            return (int)Math.Max(1, ms);
        }

        /// <summary>
        /// How long to wait for a server ping, in milliseconds: the ping interval (given in seconds) plus
        /// <paramref name="maxDelay"/>. Capped at <see cref="int.MaxValue"/> to remain a valid Timer due time.
        /// </summary>
        public static int PingDeadlineMilliseconds(uint pingInterval, TimeSpan maxDelay)
        {
            double ms = (double)pingInterval * 1000 + maxDelay.TotalMilliseconds;
            return ms > int.MaxValue ? int.MaxValue : (int)ms;
        }

        /// <summary>
        /// Returns true if <paramref name="task"/> finishes before <paramref name="cancellationToken"/> is
        /// cancelled, false otherwise. A faulted task is not observed here; that is up to the caller.
        /// The token registration is disposed after the wait, so a long-lived token doesn't collect one
        /// registration per call. A token that can never be cancelled just awaits the task.
        /// </summary>
        public static async Task<bool> CompletesBeforeCancellationAsync(Task task, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                try { await task.ConfigureAwait(false); }
                catch { }
                return true;
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
            {
                return await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) == task;
            }
        }
    }

    /// <summary>
    /// Varint encoder/decoder for Protobuf messages.
    /// </summary>
    internal static class VarintCodec
    {
        /// <summary>
        /// Size of the receive buffer a transport holds between messages. A bigger message enlarges it
        /// only until that message is consumed, so one large message doesn't keep memory for the
        /// whole session.
        /// </summary>
        public const int ReceiveBufferSize = 16 * 1024;

        /// <summary>Largest byte array length, i.e. Array.MaxLength, which netstandard2.1 doesn't expose.</summary>
        private const int MaxBufferLength = 0x7FFFFFC7;

        /// <summary>
        /// Enlarges a receive buffer that is filled by the start of one incomplete message, preserving
        /// the first <paramref name="count"/> bytes. The size doubles, but never beyond the message size
        /// once the length prefix is known. Growth follows the bytes actually received, so a corrupt
        /// prefix can't make it allocate whatever length it claims.
        /// </summary>
        public static byte[] Grow(byte[] buffer, int count)
        {
            long size = (long)buffer.Length * 2;
            if (TryReadLength(buffer, 0, count, out int length, out int dataStart))
                size = Math.Min(size, (long)dataStart + length);
            if (size > MaxBufferLength) throw new IOException("Message exceeds the maximum buffer size");
            var grown = new byte[size];
            Buffer.BlockCopy(buffer, 0, grown, 0, count);
            return grown;
        }

        /// <summary>
        /// Shifts the <paramref name="remaining"/> unread bytes that follow <paramref name="consumed"/> to
        /// the front. An enlarged buffer is swapped back for one of <see cref="ReceiveBufferSize"/> as soon
        /// as the leftover fits.
        /// </summary>
        public static byte[] Compact(byte[] buffer, int consumed, int remaining)
        {
            var target = buffer.Length > ReceiveBufferSize && remaining <= ReceiveBufferSize
                ? new byte[ReceiveBufferSize]
                : buffer;
            Buffer.BlockCopy(buffer, consumed, target, 0, remaining);
            return target;
        }

        /// <summary>
        /// Extracts every complete varint-delimited message from the start of a buffer. A partial
        /// message at the end is left in place: stream transports wait for the next chunk to complete
        /// it, message-based transports treat it as malformed.
        /// </summary>
        /// <param name="buffer">Source buffer.</param>
        /// <param name="count">Number of valid bytes in the buffer.</param>
        /// <param name="messages">Receives the extracted messages.</param>
        /// <returns>Bytes consumed.</returns>
        public static int ReadCompleteMessages(byte[] buffer, int count, List<byte[]> messages)
        {
            int consumed = 0;
            while (consumed < count)
            {
                if (!TryReadLength(buffer, consumed, count, out int length, out int position) || count - position < length)
                    return consumed;
                var message = length == 0 ? Array.Empty<byte>() : new byte[length];
                Buffer.BlockCopy(buffer, position, message, 0, length);
                messages.Add(message);
                consumed = position + length;
            }
            return consumed;
        }

        /// <summary>
        /// Decodes the varint length prefix at <paramref name="position"/>. Returns false if the prefix
        /// hasn't fully arrived; otherwise outputs the length and the offset of the message data. A
        /// malformed prefix, or a length beyond the largest possible byte array, means the stream is
        /// broken and throws before any allocation is made.
        /// </summary>
        private static bool TryReadLength(byte[] buffer, int position, int count, out int length, out int dataStart)
        {
            int start = position;
            ulong value = 0;
            int shift = 0;
            while (true)
            {
                if (position == count)
                {
                    length = 0;
                    dataStart = 0;
                    return false;
                }
                byte b = buffer[position++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 32) throw new IOException("Varint too long");
            }

            if (value > (ulong)(MaxBufferLength - (position - start))) throw new IOException("Message length out of range");
            length = (int)value;
            dataStart = position;
            return true;
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
