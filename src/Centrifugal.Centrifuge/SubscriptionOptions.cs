using System;
using System.Threading.Tasks;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Options for configuring a subscription.
    /// </summary>
    public class SubscriptionOptions
    {
        /// <summary>
        /// Gets or sets the initial subscription token (JWT).
        /// </summary>
        public string? Token { get; set; }

        /// <summary>
        /// Gets or sets the callback to get/refresh subscription token.
        /// Throw <see cref="UnauthorizedException"/> to stop token refresh attempts.
        /// </summary>
        public Func<string, Task<string>>? GetToken { get; set; }

        /// <summary>
        /// Gets or sets the subscription data (attached to every subscribe/resubscribe request).
        /// </summary>
        public byte[]? Data { get; set; }

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
        public StreamPosition? Since { get; set; }

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
        public FilterNode? TagsFilter { get; set; }

        /// <summary>
        /// Gets or sets the delta format to use for bandwidth optimization.
        /// Currently only "fossil" is supported.
        /// Cannot be used together with tags filtering.
        /// </summary>
        public string? Delta { get; set; }

        /// <summary>
        /// Validates the options.
        /// </summary>
        public void Validate()
        {
            if (MinResubscribeDelay < TimeSpan.Zero)
            {
                throw new ConfigurationException("MinResubscribeDelay cannot be negative");
            }

            if (MaxResubscribeDelay < MinResubscribeDelay)
            {
                throw new ConfigurationException("MaxResubscribeDelay must be >= MinResubscribeDelay");
            }

            if (!string.IsNullOrEmpty(Delta) && Delta != "fossil")
            {
                throw new ConfigurationException("Unsupported delta format. Only 'fossil' is supported.");
            }

            if (!string.IsNullOrEmpty(Delta) && TagsFilter != null)
            {
                throw new ConfigurationException("Cannot use delta and TagsFilter together");
            }
        }
    }
}
