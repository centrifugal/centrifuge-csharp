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
    public class UnauthorizedException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UnauthorizedException"/> class.
        /// </summary>
        public UnauthorizedException(string message = "Unauthorized")
            : base(DisconnectedCodes.Unauthorized, message, false)
        {
        }
    }

    /// <summary>
    /// Exception thrown when operation times out.
    /// </summary>
    public class TimeoutException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TimeoutException"/> class.
        /// </summary>
        public TimeoutException(string message = "Operation timeout")
            : base(ErrorCodes.Timeout, message, true)
        {
        }
    }

    /// <summary>
    /// Exception thrown when configuration is invalid.
    /// </summary>
    public class ConfigurationException : CentrifugeException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationException"/> class.
        /// </summary>
        public ConfigurationException(string message)
            : base(ErrorCodes.BadConfiguration, message, false)
        {
        }
    }
}
