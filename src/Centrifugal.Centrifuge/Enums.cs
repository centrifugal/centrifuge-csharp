using System;

namespace Centrifugal.Centrifuge
{
    /// <summary>
    /// Client connection state.
    /// </summary>
    public enum ClientState
    {
        /// <summary>
        /// Client is disconnected from the server.
        /// </summary>
        Disconnected,

        /// <summary>
        /// Client is connecting or reconnecting to the server.
        /// </summary>
        Connecting,

        /// <summary>
        /// Client is connected to the server.
        /// </summary>
        Connected
    }

    /// <summary>
    /// Subscription state.
    /// </summary>
    public enum SubscriptionState
    {
        /// <summary>
        /// Subscription is not subscribed.
        /// </summary>
        Unsubscribed,

        /// <summary>
        /// Subscription is subscribing or resubscribing.
        /// </summary>
        Subscribing,

        /// <summary>
        /// Subscription is subscribed.
        /// </summary>
        Subscribed
    }

    /// <summary>
    /// Transport type.
    /// </summary>
    public enum TransportType
    {
        /// <summary>
        /// WebSocket transport.
        /// </summary>
        WebSocket,

        /// <summary>
        /// HTTP streaming transport.
        /// </summary>
        HttpStream
    }

    /// <summary>
    /// Error codes for client-side errors.
    /// </summary>
    public static class ErrorCodes
    {
        /// <summary>
        /// Operation timeout.
        /// </summary>
        public const int Timeout = 1;

        /// <summary>
        /// Transport closed.
        /// </summary>
        public const int TransportClosed = 2;

        /// <summary>
        /// Client disconnected.
        /// </summary>
        public const int ClientDisconnected = 3;

        /// <summary>
        /// Client closed.
        /// </summary>
        public const int ClientClosed = 4;

        /// <summary>
        /// Client connect token error.
        /// </summary>
        public const int ClientConnectToken = 5;

        /// <summary>
        /// Client refresh token error.
        /// </summary>
        public const int ClientRefreshToken = 6;

        /// <summary>
        /// Subscription unsubscribed.
        /// </summary>
        public const int SubscriptionUnsubscribed = 7;

        /// <summary>
        /// Subscription subscribe token error.
        /// </summary>
        public const int SubscriptionSubscribeToken = 8;

        /// <summary>
        /// Subscription refresh token error.
        /// </summary>
        public const int SubscriptionRefreshToken = 9;

        /// <summary>
        /// Transport write error.
        /// </summary>
        public const int TransportWriteError = 10;

        /// <summary>
        /// Connection closed.
        /// </summary>
        public const int ConnectionClosed = 11;

        /// <summary>
        /// Bad configuration.
        /// </summary>
        public const int BadConfiguration = 12;
    }

    /// <summary>
    /// Codes for connecting state transitions.
    /// </summary>
    public static class ConnectingCodes
    {
        /// <summary>
        /// Connect method was called.
        /// </summary>
        public const int ConnectCalled = 0;

        /// <summary>
        /// Transport was closed.
        /// </summary>
        public const int TransportClosed = 1;

        /// <summary>
        /// No ping received from server.
        /// </summary>
        public const int NoPing = 2;

        /// <summary>
        /// Subscribe timeout.
        /// </summary>
        public const int SubscribeTimeout = 3;

        /// <summary>
        /// Unsubscribe error.
        /// </summary>
        public const int UnsubscribeError = 4;
    }

    /// <summary>
    /// Codes for disconnected state transitions.
    /// </summary>
    public static class DisconnectedCodes
    {
        /// <summary>
        /// Disconnect method was called.
        /// </summary>
        public const int DisconnectCalled = 0;

        /// <summary>
        /// Unauthorized.
        /// </summary>
        public const int Unauthorized = 1;

        /// <summary>
        /// Bad protocol.
        /// </summary>
        public const int BadProtocol = 2;

        /// <summary>
        /// Message size limit exceeded.
        /// </summary>
        public const int MessageSizeLimit = 3;
    }

    /// <summary>
    /// Codes for subscribing state transitions.
    /// </summary>
    public static class SubscribingCodes
    {
        /// <summary>
        /// Subscribe method was called.
        /// </summary>
        public const int SubscribeCalled = 0;

        /// <summary>
        /// Transport was closed.
        /// </summary>
        public const int TransportClosed = 1;
    }

    /// <summary>
    /// Codes for unsubscribed state transitions.
    /// </summary>
    public static class UnsubscribedCodes
    {
        /// <summary>
        /// Unsubscribe method was called.
        /// </summary>
        public const int UnsubscribeCalled = 0;

        /// <summary>
        /// Unauthorized.
        /// </summary>
        public const int Unauthorized = 1;

        /// <summary>
        /// Client closed.
        /// </summary>
        public const int ClientClosed = 2;
    }
}
