using System;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Options for configuring a subscription.
    /// </summary>
    public class CentrifugeSubscriptionOptions
    {
        /// <summary>
        /// Gets or sets the initial subscription token (JWT).
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/refresh subscription token.
        /// Throw <see cref="CentrifugeUnauthorizedException"/> to stop token refresh attempts.
        /// </summary>
        public Func<string, Task<string>>? GetToken { get; set; }

        /// <summary>
        /// Gets or sets the subscription data (attached to every subscribe/resubscribe request).
        /// </summary>
        public ReadOnlyMemory<byte> Data { get; set; }

        /// <summary>
        /// Gets or sets the minimum delay between resubscribe attempts.
        /// Default is 500ms.
        /// </summary>
        public TimeSpan MinResubscribeDelay { get; set; } = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Gets or sets the maximum delay between resubscribe attempts.
        /// Default is 20 seconds.
        /// </summary>
        public TimeSpan MaxResubscribeDelay { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// Gets or sets the stream position to start subscription from (attempt recovery on first subscribe).
        /// </summary>
        public CentrifugeStreamPosition? Since { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to make subscription positioned (if not forced by server).
        /// </summary>
        public bool Positioned { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to make subscription recoverable (if not forced by server).
        /// </summary>
        public bool Recoverable { get; set; }

        /// <summary>
        /// Gets or sets whether to ask server to push Join/Leave messages (if not forced by server).
        /// </summary>
        public bool JoinLeave { get; set; }

        /// <summary>
        /// Gets or sets the server-side publication filter based on publication tags.
        /// Cannot be used together with delta compression.
        /// </summary>
        public CentrifugeFilterNode? TagsFilter { get; set; }

        /// <summary>
        /// Gets or sets the delta format to use for bandwidth optimization.
        /// Currently only "fossil" is supported.
        /// Cannot be used together with tags filtering.
        /// </summary>
        public string? Delta { get; set; }

        /// <summary>
        /// Gets or sets the callback to load the app's current state and stream position.
        /// Requires Centrifugo &gt;= 6.8.0.
        /// <para>
        /// The SDK calls the callback:
        /// <list type="bullet">
        /// <item>On initial subscribe (no saved position)</item>
        /// <item>On reconnect when recovery fails (server returns error 112 — unrecoverable position)</item>
        /// </list>
        /// NOT called on reconnects where the server successfully recovers missed publications —
        /// in that case the recovered publications arrive as events and the callback is skipped.
        /// </para>
        /// <para>
        /// The app should load its data from its own source of truth (database, API), render it,
        /// and return the stream position. The SDK subscribes with recovery from the returned
        /// position, so any publications between the state read and the subscribe are delivered
        /// as publication events.
        /// </para>
        /// <para>
        /// IMPORTANT: inside the callback, read the stream position FIRST, then read your data.
        /// This ensures the position is a lower bound — any data loaded after the position read
        /// is guaranteed to be included. The reverse order can produce gaps.
        /// </para>
        /// <para>
        /// Recovered publications may overlap with data already loaded by the callback. This works
        /// correctly when updates are idempotent (applying the same update twice produces the same
        /// result). For non-idempotent updates, deduplicate by publication offset.
        /// </para>
        /// <para>
        /// On error, the SDK raises the Error event (type "getState") and retries with backoff.
        /// </para>
        /// </summary>
        public Func<string, Task<CentrifugeStreamPosition>>? GetState { get; set; }

        /// <summary>
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (MinResubscribeDelay < TimeSpan.Zero)
            {
                throw new CentrifugeConfigurationException("MinResubscribeDelay cannot be negative");
            }

            if (MaxResubscribeDelay < MinResubscribeDelay)
            {
                throw new CentrifugeConfigurationException("MaxResubscribeDelay must be >= MinResubscribeDelay");
            }

            if (!string.IsNullOrEmpty(Delta) && Delta != "fossil")
            {
                throw new CentrifugeConfigurationException("Unsupported delta format. Only 'fossil' is supported.");
            }

            if (!string.IsNullOrEmpty(Delta) && TagsFilter != null)
            {
                throw new CentrifugeConfigurationException("Cannot use delta and TagsFilter together");
            }
        }
    }
}
