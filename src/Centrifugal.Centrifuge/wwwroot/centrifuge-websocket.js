// Centrifuge WebSocket wrapper for Blazor WASM
// This provides a bridge between browser WebSocket API and .NET

window.CentrifugeWebSocket = {
    sockets: {},
    nextId: 1,

    /**
     * Creates and opens a WebSocket connection
     * @param {string} url - WebSocket URL
     * @param {string} protocol - WebSocket subprotocol (e.g., "centrifuge-protobuf")
     * @param {object} dotnetRef - .NET object reference for callbacks
     * @returns {number} Socket ID
     */
    connect: function (url, protocol, dotnetRef) {
        const id = this.nextId++;

        try {
            const socket = new WebSocket(url, protocol);
            socket.binaryType = 'arraybuffer'; // For protobuf binary messages

            const socketInfo = {
                socket: socket,
                dotnetRef: dotnetRef,
                id: id
            };

            socket.onopen = () => {
                dotnetRef.invokeMethodAsync('OnOpen');
            };

            socket.onmessage = (event) => {
                if (event.data instanceof ArrayBuffer) {
                    // Convert ArrayBuffer to Uint8Array for .NET
                    const bytes = new Uint8Array(event.data);
                    dotnetRef.invokeMethodAsync('OnMessage', Array.from(bytes));
                } else if (typeof event.data === 'string') {
                    // Text message - convert to UTF-8 bytes
                    const encoder = new TextEncoder();
                    const bytes = encoder.encode(event.data);
                    dotnetRef.invokeMethodAsync('OnMessage', Array.from(bytes));
                }
            };

            socket.onerror = (event) => {
                dotnetRef.invokeMethodAsync('OnError', 'WebSocket error occurred');
            };

            socket.onclose = (event) => {
                dotnetRef.invokeMethodAsync('OnClose', event.code, event.reason || '');
                delete this.sockets[id];
            };

            this.sockets[id] = socketInfo;
            return id;
        } catch (error) {
            console.error('CentrifugeWebSocket.connect error:', error);
            throw error;
        }
    },

    /**
     * Sends binary data through the WebSocket
     * @param {number} id - Socket ID
     * @param {number[]} data - Byte array to send
     */
    send: function (id, data) {
        const socketInfo = this.sockets[id];
        if (!socketInfo) {
            throw new Error(`WebSocket ${id} not found`);
        }

        const socket = socketInfo.socket;
        if (socket.readyState !== WebSocket.OPEN) {
            throw new Error(`WebSocket ${id} is not open (state: ${socket.readyState})`);
        }

        try {
            // Convert byte array to Uint8Array and send
            const bytes = new Uint8Array(data);
            socket.send(bytes.buffer);
        } catch (error) {
            console.error('CentrifugeWebSocket.send error:', error);
            throw error;
        }
    },

    /**
     * Closes the WebSocket connection
     * @param {number} id - Socket ID
     * @param {number} code - Close code (default 1000)
     * @param {string} reason - Close reason
     */
    close: function (id, code, reason) {
        const socketInfo = this.sockets[id];
        if (!socketInfo) {
            return; // Already closed or doesn't exist
        }

        try {
            socketInfo.socket.close(code || 1000, reason || '');
        } catch (error) {
            console.error('CentrifugeWebSocket.close error:', error);
            // Still remove from registry
        }

        delete this.sockets[id];
    },

    /**
     * Gets the current ready state of the WebSocket
     * @param {number} id - Socket ID
     * @returns {number} Ready state (0=CONNECTING, 1=OPEN, 2=CLOSING, 3=CLOSED)
     */
    getReadyState: function (id) {
        const socketInfo = this.sockets[id];
        if (!socketInfo) {
            return 3; // CLOSED
        }
        return socketInfo.socket.readyState;
    },

    /**
     * Disposes a socket reference without closing (cleanup)
     * @param {number} id - Socket ID
     */
    dispose: function (id) {
        const socketInfo = this.sockets[id];
        if (socketInfo) {
            socketInfo.dotnetRef = null;
            delete this.sockets[id];
        }
    }
};
