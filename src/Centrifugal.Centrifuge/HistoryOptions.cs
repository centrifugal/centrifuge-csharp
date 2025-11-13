namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Options for history requests.
    /// </summary>
    public class CentrifugeHistoryOptions
    {
        /// <summary>
        /// Gets or sets the maximum number of publications to return.
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Gets or sets the stream position to get publications since.
        /// </summary>
        public CentrifugeStreamPosition? Since { get; set; }

        /// <summary>
        /// Gets or sets whether to return publications in reverse order (newest first).
        /// </summary>
        public bool Reverse { get; set; }
    }
}
