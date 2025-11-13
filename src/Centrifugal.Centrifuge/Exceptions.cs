using System;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Base exception for Centrifuge client errors.
    /// </summary>
    public class CentrifugeException : Exception
    {
        /// <summary>
        /// Gets the error code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets whether this error is temporary.
        /// </summary>
        public bool Temporary { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeException"/> class.
        /// </summary>
        public CentrifugeException(int code, string message, bool temporary = false, Exception? innerException = null)
            : base(message, innerException)
        {
            Code = code;
            Temporary = temporary;
        }
    }

    /// <summary>
    /// Exception thrown when authentication fails.
    /// </summary>
    public class CentrifugeUnauthorizedException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeUnauthorizedException"/> class.
        /// </summary>
        public CentrifugeUnauthorizedException(string message = "Unauthorized")
            : base(CentrifugeDisconnectedCodes.Unauthorized, message, false)
        {
        }
    }

    /// <summary>
    /// Exception thrown when operation times out.
    /// </summary>
    public class CentrifugeTimeoutException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeTimeoutException"/> class.
        /// </summary>
        public CentrifugeTimeoutException(string message = "Operation timeout")
            : base(CentrifugeErrorCodes.Timeout, message, true)
        {
        }
    }

    /// <summary>
    /// Exception thrown when configuration is invalid.
    /// </summary>
    public class CentrifugeConfigurationException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeConfigurationException"/> class.
        /// </summary>
        public CentrifugeConfigurationException(string message)
            : base(CentrifugeErrorCodes.BadConfiguration, message, false)
        {
        }
    }
}
