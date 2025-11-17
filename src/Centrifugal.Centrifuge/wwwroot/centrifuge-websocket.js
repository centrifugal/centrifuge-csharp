// Centrifuge WebSocket wrapper for Blazor WASM
// This provides a bridge between browser WebSocket API and .NET

window.CentrifugeWebSocket = {
    sockets: {},
    nextId: 1,

    debugLog: function(debug, ...args) {
        if (debug) {
            console.log(...args);
        }
    },

    /**
     * Creates and opens a WebSocket connection
     * @param {string} url - WebSocket URL
     * @param {string} protocol - WebSocket subprotocol (e.g., "centrifuge-protobuf")
     * @param {object} dotnetRef - .NET object reference for callbacks
     * @param {boolean} debug - Enable debug logging
     * @returns {number} Socket ID
     */
    connect: function (url, protocol, dotnetRef, debug) {
        const id = this.nextId++;
        const self = this;
        self.debugLog(debug, '[CentrifugeWebSocket] Connecting to:', url, 'with protocol:', protocol, 'id:', id);

        try {
            const socket = new WebSocket(url, protocol);
            socket.binaryType = 'arraybuffer'; // For protobuf binary messages

            const socketInfo = {
                socket: socket,
                dotnetRef: dotnetRef,
                id: id,
                debug: debug
            };

            socket.onopen = () => {
                self.debugLog(debug, '[CentrifugeWebSocket] Socket opened, id:', id, 'readyState:', socket.readyState);
                dotnetRef.invokeMethodAsync('OnOpen');
            };

            socket.onmessage = (event) => {
                if (event.data instanceof ArrayBuffer) {
                    // Convert ArrayBuffer to base64 for .NET interop
                    const bytes = new Uint8Array(event.data);
                    const base64 = btoa(String.fromCharCode.apply(null, bytes));
                    dotnetRef.invokeMethodAsync('OnMessage', base64);
                } else if (typeof event.data === 'string') {
                    // Text message - convert to UTF-8 bytes then base64
                    const encoder = new TextEncoder();
                    const bytes = encoder.encode(event.data);
                    const base64 = btoa(String.fromCharCode.apply(null, bytes));
                    dotnetRef.invokeMethodAsync('OnMessage', base64);
                }
            };

            socket.onerror = (event) => {
                if (debug) console.error('[CentrifugeWebSocket] Error on socket id:', id, 'readyState:', socket.readyState, 'event:', event);
                dotnetRef.invokeMethodAsync('OnError', 'WebSocket error occurred');
            };

            socket.onclose = (event) => {
                const closeCode = event.code || 0;
                const closeReason = event.reason || '';
                self.debugLog(debug, '[CentrifugeWebSocket] onclose - id:', id, 'code:', closeCode, 'reason:', closeReason, 'wasClean:', event.wasClean, 'event:', event);
                dotnetRef.invokeMethodAsync('OnClose', closeCode, closeReason);
                // Note: Don't delete from sockets here - let dispose() clean up after .NET side is done
            };

            this.sockets[id] = socketInfo;
            self.debugLog(debug, '[CentrifugeWebSocket] Socket registered with id:', id, 'current readyState:', socket.readyState);
            return id;
        } catch (error) {
            console.error('[CentrifugeWebSocket] connect error:', error);
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
            this.debugLog(false, '[CentrifugeWebSocket] close - socket not found:', id);
            return; // Already closed or doesn't exist
        }

        const debug = socketInfo.debug || false;
        const socket = socketInfo.socket;
        // Only close if socket is still CONNECTING or OPEN
        if (socket.readyState === WebSocket.CONNECTING || socket.readyState === WebSocket.OPEN) {
            this.debugLog(debug, '[CentrifugeWebSocket] Closing socket', id, 'with code:', code, 'reason:', reason, 'current state:', socket.readyState);
            try {
                socket.close(code || 1000, reason || '');
            } catch (error) {
                if (debug) console.error('[CentrifugeWebSocket] close error:', error);
            }
        } else {
            this.debugLog(debug, '[CentrifugeWebSocket] Socket', id, 'already closing/closed, readyState:', socket.readyState);
        }
        // Note: Don't remove from registry here - let dispose() do that after cleanup
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
     * Disposes a socket reference and removes it from registry (cleanup)
     * @param {number} id - Socket ID
     */
    dispose: function (id) {
        const debug = this.sockets[id]?.debug || false;
        this.debugLog(debug, '[CentrifugeWebSocket] dispose called for socket', id);
        const socketInfo = this.sockets[id];
        if (socketInfo) {
            this.debugLog(debug, '[CentrifugeWebSocket] Disposing socket', id, 'readyState:', socketInfo.socket.readyState);
            // Clear the .NET reference to prevent memory leaks
            socketInfo.dotnetRef = null;
            // Remove event handlers to prevent callbacks after disposal
            socketInfo.socket.onopen = null;
            socketInfo.socket.onmessage = null;
            socketInfo.socket.onerror = null;
            socketInfo.socket.onclose = null;
            // Remove from registry
            delete this.sockets[id];
            this.debugLog(debug, '[CentrifugeWebSocket] Socket', id, 'disposed successfully');
        } else {
            this.debugLog(false, '[CentrifugeWebSocket] dispose - socket', id, 'not found (already disposed)');
        }
    }
};
