using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Event arguments for state changes.
    /// </summary>
    public class CentrifugeStateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the previous state.
        /// </summary>
        public CentrifugeClientState OldState { get; }

        /// <summary>
        /// Gets the new state.
        /// </summary>
        public CentrifugeClientState NewState { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeStateEventArgs"/> class.
        /// </summary>
        public CentrifugeStateEventArgs(CentrifugeClientState oldState, CentrifugeClientState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Event arguments for connecting state.
    /// </summary>
    public class CentrifugeConnectingEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the connecting code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets the reason for connecting.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeConnectingEventArgs"/> class.
        /// </summary>
        public CentrifugeConnectingEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for connected state.
    /// </summary>
    public class CentrifugeConnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the client ID assigned by server.
        /// </summary>
        public string ClientId { get; }

        /// <summary>
        /// Gets the transport name.
        /// </summary>
        public string Transport { get; }

        /// <summary>
        /// Gets the optional connection data from server.
        /// </summary>
        public byte[]? Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeConnectedEventArgs"/> class.
        /// </summary>
        public CentrifugeConnectedEventArgs(string clientId, string transport, byte[]? data = null)
        {
            ClientId = clientId ?? string.Empty;
            Transport = transport ?? string.Empty;
            Data = data;
        }
    }

    /// <summary>
    /// Event arguments for disconnected state.
    /// </summary>
    public class CentrifugeDisconnectedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the disconnection code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets the reason for disconnection.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeDisconnectedEventArgs"/> class.
        /// </summary>
        public CentrifugeDisconnectedEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for error events.
    /// </summary>
    public class CentrifugeErrorEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the error type.
        /// </summary>
        public string Type { get; }

        /// <summary>
        /// Gets the exception if available.
        /// </summary>
        public Exception? Exception { get; }

        /// <summary>
        /// Gets the error code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets the error message.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// Gets whether this error is temporary and may be retried.
        /// </summary>
        public bool Temporary { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeErrorEventArgs"/> class.
        /// </summary>
        public CentrifugeErrorEventArgs(string type, int code, string message, bool temporary = false, Exception? exception = null)
        {
            Type = type ?? string.Empty;
            Code = code;
            Message = message ?? string.Empty;
            Temporary = temporary;
            Exception = exception;
        }
    }

    /// <summary>
    /// Event arguments for messages from server.
    /// </summary>
    public class CentrifugeMessageEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the message data.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeMessageEventArgs"/> class.
        /// </summary>
        public CentrifugeMessageEventArgs(byte[] data)
        {
            Data = data ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Client information.
    /// </summary>
    public class CentrifugeClientInfo
    {
        /// <summary>
        /// Gets the user ID.
        /// </summary>
        public string User { get; }

        /// <summary>
        /// Gets the client ID.
        /// </summary>
        public string Client { get; }

        /// <summary>
        /// Gets the connection info.
        /// </summary>
        public byte[]? ConnInfo { get; }

        /// <summary>
        /// Gets the channel info.
        /// </summary>
        public byte[]? ChanInfo { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeClientInfo"/> class.
        /// </summary>
        public CentrifugeClientInfo(string user, string client, byte[]? connInfo = null, byte[]? chanInfo = null)
        {
            User = user ?? string.Empty;
            Client = client ?? string.Empty;
            ConnInfo = connInfo;
            ChanInfo = chanInfo;
        }
    }

    /// <summary>
    /// Event arguments for publications.
    /// </summary>
    public class CentrifugePublicationEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the publication data.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Gets the optional publisher client info.
        /// </summary>
        public CentrifugeClientInfo? Info { get; }

        /// <summary>
        /// Gets the stream offset if available.
        /// </summary>
        public ulong? Offset { get; }

        /// <summary>
        /// Gets the publication tags if available.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Tags { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugePublicationEventArgs"/> class.
        /// </summary>
        public CentrifugePublicationEventArgs(string channel, byte[] data, CentrifugeClientInfo? info = null, ulong? offset = null, IReadOnlyDictionary<string, string>? tags = null)
        {
            Channel = channel ?? string.Empty;
            Data = data ?? Array.Empty<byte>();
            Info = info;
            Offset = offset;
            Tags = tags;
        }
    }

    /// <summary>
    /// Event arguments for join events.
    /// </summary>
    public class CentrifugeJoinEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the client info for the joined client.
        /// </summary>
        public CentrifugeClientInfo Info { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeJoinEventArgs"/> class.
        /// </summary>
        public CentrifugeJoinEventArgs(string channel, CentrifugeClientInfo info)
        {
            Channel = channel ?? string.Empty;
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Event arguments for leave events.
    /// </summary>
    public class CentrifugeLeaveEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the client info for the leaving client.
        /// </summary>
        public CentrifugeClientInfo Info { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeLeaveEventArgs"/> class.
        /// </summary>
        public CentrifugeLeaveEventArgs(string channel, CentrifugeClientInfo info)
        {
            Channel = channel ?? string.Empty;
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Event arguments for subscription state changes.
    /// </summary>
    public class CentrifugeSubscriptionStateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the previous state.
        /// </summary>
        public CentrifugeSubscriptionState OldState { get; }

        /// <summary>
        /// Gets the new state.
        /// </summary>
        public CentrifugeSubscriptionState NewState { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeSubscriptionStateEventArgs"/> class.
        /// </summary>
        public CentrifugeSubscriptionStateEventArgs(CentrifugeSubscriptionState oldState, CentrifugeSubscriptionState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Event arguments for subscribing state.
    /// </summary>
    public class CentrifugeSubscribingEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the subscribing code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets the reason for subscribing.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeSubscribingEventArgs"/> class.
        /// </summary>
        public CentrifugeSubscribingEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for subscribed state.
    /// </summary>
    public class CentrifugeSubscribedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets whether recovery was attempted.
        /// </summary>
        public bool WasRecovering { get; }

        /// <summary>
        /// Gets whether all missed publications were recovered.
        /// </summary>
        public bool Recovered { get; }

        /// <summary>
        /// Gets whether subscription is recoverable (can automatically recover missed messages).
        /// </summary>
        public bool Recoverable { get; }

        /// <summary>
        /// Gets whether subscription is positioned (server tracks message loss on the way from PUB/SUB broker).
        /// </summary>
        public bool Positioned { get; }

        /// <summary>
        /// Gets the stream position (set when subscription is recoverable or positioned).
        /// </summary>
        public CentrifugeStreamPosition? StreamPosition { get; }

        /// <summary>
        /// Gets the optional subscription data from server.
        /// </summary>
        public byte[]? Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeSubscribedEventArgs"/> class.
        /// </summary>
        public CentrifugeSubscribedEventArgs(bool wasRecovering, bool recovered, bool recoverable, bool positioned, CentrifugeStreamPosition? streamPosition = null, byte[]? data = null)
        {
            WasRecovering = wasRecovering;
            Recovered = recovered;
            Recoverable = recoverable;
            Positioned = positioned;
            StreamPosition = streamPosition;
            Data = data;
        }
    }

    /// <summary>
    /// Event arguments for unsubscribed state.
    /// </summary>
    public class CentrifugeUnsubscribedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the unsubscribe code.
        /// </summary>
        public int Code { get; }

        /// <summary>
        /// Gets the reason for unsubscribe.
        /// </summary>
        public string Reason { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeUnsubscribedEventArgs"/> class.
        /// </summary>
        public CentrifugeUnsubscribedEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Stream position for recovery.
    /// </summary>
    public class CentrifugeStreamPosition
    {
        /// <summary>
        /// Gets the offset in the stream.
        /// </summary>
        public ulong Offset { get; }

        /// <summary>
        /// Gets the epoch identifier.
        /// </summary>
        public string Epoch { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeStreamPosition"/> class.
        /// </summary>
        public CentrifugeStreamPosition(ulong offset, string epoch)
        {
            Offset = offset;
            Epoch = epoch ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for server-side subscription subscribing state.
    /// </summary>
    public class CentrifugeServerSubscribingEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeServerSubscribingEventArgs"/> class.
        /// </summary>
        public CentrifugeServerSubscribingEventArgs(string channel)
        {
            Channel = channel ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for server-side subscription subscribed state.
    /// </summary>
    public class CentrifugeServerSubscribedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets whether subscription is recoverable.
        /// </summary>
        public bool Recoverable { get; }

        /// <summary>
        /// Gets whether subscription is positioned.
        /// </summary>
        public bool Positioned { get; }

        /// <summary>
        /// Gets the stream position (set when subscription is recoverable or positioned).
        /// </summary>
        public CentrifugeStreamPosition? StreamPosition { get; }

        /// <summary>
        /// Gets whether recovery was attempted.
        /// </summary>
        public bool WasRecovering { get; }

        /// <summary>
        /// Gets whether recovery succeeded.
        /// </summary>
        public bool Recovered { get; }

        /// <summary>
        /// Gets the optional subscription data from server.
        /// </summary>
        public byte[]? Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeServerSubscribedEventArgs"/> class.
        /// </summary>
        public CentrifugeServerSubscribedEventArgs(string channel, bool wasRecovering, bool recovered, bool recoverable, bool positioned, CentrifugeStreamPosition? streamPosition = null, byte[]? data = null)
        {
            Channel = channel ?? string.Empty;
            WasRecovering = wasRecovering;
            Recovered = recovered;
            Recoverable = recoverable;
            Positioned = positioned;
            StreamPosition = streamPosition;
            Data = data;
        }
    }

    /// <summary>
    /// Event arguments for server-side subscription unsubscribed state.
    /// </summary>
    public class CentrifugeServerUnsubscribedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="CentrifugeServerUnsubscribedEventArgs"/> class.
        /// </summary>
        public CentrifugeServerUnsubscribedEventArgs(string channel)
        {
            Channel = channel ?? string.Empty;
        }
    }
}
