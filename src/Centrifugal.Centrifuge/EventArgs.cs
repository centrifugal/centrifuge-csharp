using System;
using System.Collections.Generic;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Event arguments for state changes.
    /// </summary>
    public class StateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the previous state.
        /// </summary>
        public ClientState OldState { get; }

        /// <summary>
        /// Gets the new state.
        /// </summary>
        public ClientState NewState { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="StateEventArgs"/> class.
        /// </summary>
        public StateEventArgs(ClientState oldState, ClientState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Event arguments for connecting state.
    /// </summary>
    public class ConnectingEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="ConnectingEventArgs"/> class.
        /// </summary>
        public ConnectingEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for connected state.
    /// </summary>
    public class ConnectedEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="ConnectedEventArgs"/> class.
        /// </summary>
        public ConnectedEventArgs(string clientId, string transport, byte[]? data = null)
        {
            ClientId = clientId ?? string.Empty;
            Transport = transport ?? string.Empty;
            Data = data;
        }
    }

    /// <summary>
    /// Event arguments for disconnected state.
    /// </summary>
    public class DisconnectedEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="DisconnectedEventArgs"/> class.
        /// </summary>
        public DisconnectedEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for error events.
    /// </summary>
    public class ErrorEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="ErrorEventArgs"/> class.
        /// </summary>
        public ErrorEventArgs(string type, int code, string message, bool temporary = false, Exception? exception = null)
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
    public class MessageEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the message data.
        /// </summary>
        public byte[] Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageEventArgs"/> class.
        /// </summary>
        public MessageEventArgs(byte[] data)
        {
            Data = data ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Client information.
    /// </summary>
    public class ClientInfo
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
        /// Initializes a new instance of the <see cref="ClientInfo"/> class.
        /// </summary>
        public ClientInfo(string user, string client, byte[]? connInfo = null, byte[]? chanInfo = null)
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
    public class PublicationEventArgs : EventArgs
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
        public ClientInfo? Info { get; }

        /// <summary>
        /// Gets the stream offset if available.
        /// </summary>
        public ulong? Offset { get; }

        /// <summary>
        /// Gets the publication tags if available.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Tags { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="PublicationEventArgs"/> class.
        /// </summary>
        public PublicationEventArgs(string channel, byte[] data, ClientInfo? info = null, ulong? offset = null, IReadOnlyDictionary<string, string>? tags = null)
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
    public class JoinEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the client info for the joined client.
        /// </summary>
        public ClientInfo Info { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="JoinEventArgs"/> class.
        /// </summary>
        public JoinEventArgs(string channel, ClientInfo info)
        {
            Channel = channel ?? string.Empty;
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Event arguments for leave events.
    /// </summary>
    public class LeaveEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Gets the client info for the leaving client.
        /// </summary>
        public ClientInfo Info { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="LeaveEventArgs"/> class.
        /// </summary>
        public LeaveEventArgs(string channel, ClientInfo info)
        {
            Channel = channel ?? string.Empty;
            Info = info ?? throw new ArgumentNullException(nameof(info));
        }
    }

    /// <summary>
    /// Event arguments for subscription state changes.
    /// </summary>
    public class SubscriptionStateEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the previous state.
        /// </summary>
        public SubscriptionState OldState { get; }

        /// <summary>
        /// Gets the new state.
        /// </summary>
        public SubscriptionState NewState { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscriptionStateEventArgs"/> class.
        /// </summary>
        public SubscriptionStateEventArgs(SubscriptionState oldState, SubscriptionState newState)
        {
            OldState = oldState;
            NewState = newState;
        }
    }

    /// <summary>
    /// Event arguments for subscribing state.
    /// </summary>
    public class SubscribingEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="SubscribingEventArgs"/> class.
        /// </summary>
        public SubscribingEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for subscribed state.
    /// </summary>
    public class SubscribedEventArgs : EventArgs
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
        public StreamPosition? StreamPosition { get; }

        /// <summary>
        /// Gets the optional subscription data from server.
        /// </summary>
        public byte[]? Data { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SubscribedEventArgs"/> class.
        /// </summary>
        public SubscribedEventArgs(bool wasRecovering, bool recovered, bool recoverable, bool positioned, StreamPosition? streamPosition = null, byte[]? data = null)
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
    public class UnsubscribedEventArgs : EventArgs
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
        /// Initializes a new instance of the <see cref="UnsubscribedEventArgs"/> class.
        /// </summary>
        public UnsubscribedEventArgs(int code, string reason)
        {
            Code = code;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// Stream position for recovery.
    /// </summary>
    public class StreamPosition
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
        /// Initializes a new instance of the <see cref="StreamPosition"/> class.
        /// </summary>
        public StreamPosition(ulong offset, string epoch)
        {
            Offset = offset;
            Epoch = epoch ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for server-side subscription subscribing state.
    /// </summary>
    public class ServerSubscribingEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerSubscribingEventArgs"/> class.
        /// </summary>
        public ServerSubscribingEventArgs(string channel)
        {
            Channel = channel ?? string.Empty;
        }
    }

    /// <summary>
    /// Event arguments for server-side subscription subscribed state.
    /// </summary>
    public class ServerSubscribedEventArgs : EventArgs
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
        public StreamPosition? StreamPosition { get; }

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
        /// Initializes a new instance of the <see cref="ServerSubscribedEventArgs"/> class.
        /// </summary>
        public ServerSubscribedEventArgs(string channel, bool wasRecovering, bool recovered, bool recoverable, bool positioned, StreamPosition? streamPosition = null, byte[]? data = null)
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
    public class ServerUnsubscribedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the channel name.
        /// </summary>
        public string Channel { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ServerUnsubscribedEventArgs"/> class.
        /// </summary>
        public ServerUnsubscribedEventArgs(string channel)
        {
            Channel = channel ?? string.Empty;
        }
    }
}
