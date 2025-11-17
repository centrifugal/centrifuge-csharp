// Centrifuge HTTP Stream wrapper for Blazor WASM
// This provides a bridge between browser Fetch API with ReadableStream and .NET

window.CentrifugeHttpStream = {
    streams: {},
    nextId: 1,

    debugLog: function(debug, ...args) {
        if (debug) {
            console.log(...args);
        }
    },

    /**
     * Creates and opens an HTTP streaming connection using Fetch API
     * @param {string} url - HTTP endpoint URL
     * @param {number[]} initialData - Initial data to send (connect command)
     * @param {object} dotnetRef - .NET object reference for callbacks
     * @param {boolean} debug - Enable debug logging
     * @returns {number} Stream ID
     */
    connect: async function (url, initialData, dotnetRef, debug) {
        const id = this.nextId++;
        const self = this;
        self.debugLog(debug, '[CentrifugeHttpStream] Connecting to:', url, 'with stream ID:', id);
        const abortController = new AbortController();

        try {
            // Convert byte array to Uint8Array
            const bodyData = new Uint8Array(initialData);
            self.debugLog(debug, '[CentrifugeHttpStream] Sending POST request, body size:', bodyData.length);

            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Accept': 'application/octet-stream',
                    'Content-Type': 'application/octet-stream'
                },
                body: bodyData,
                signal: abortController.signal,
                mode: 'cors',
                credentials: 'same-origin'
            });

            self.debugLog(debug, '[CentrifugeHttpStream] Response received for stream', id, '- status:', response.status);

            if (!response.ok) {
                const statusCode = response.status;
                if (debug) console.error('[CentrifugeHttpStream] HTTP error for stream', id, ':', statusCode);
                dotnetRef.invokeMethodAsync('OnError', statusCode, `HTTP error ${statusCode}`);
                return id;
            }

            const streamInfo = {
                reader: response.body.getReader(),
                abortController: abortController,
                dotnetRef: dotnetRef,
                id: id,
                reading: false,
                debug: debug
            };

            this.streams[id] = streamInfo;
            self.debugLog(debug, '[CentrifugeHttpStream] Stream registered with id:', id);

            // Notify connection opened
            self.debugLog(debug, '[CentrifugeHttpStream] Stream opened, id:', id);
            dotnetRef.invokeMethodAsync('OnOpen');

            // Start reading loop
            this.readLoop(id);

            return id;
        } catch (error) {
            if (debug) console.error('[CentrifugeHttpStream] connect error for stream', id, ':', error);
            dotnetRef.invokeMethodAsync('OnError', 0, error.message || 'Connection failed');
            return id;
        }
    },

    /**
     * Reading loop that processes incoming chunks
     * @param {number} id - Stream ID
     */
    readLoop: async function (id) {
        const streamInfo = this.streams[id];
        if (!streamInfo || streamInfo.reading) {
            return;
        }

        streamInfo.reading = true;
        const reader = streamInfo.reader;
        const dotnetRef = streamInfo.dotnetRef;
        const self = this;

        try {
            const debug = streamInfo.debug || false;
            self.debugLog(debug, '[CentrifugeHttpStream] Starting read loop for stream', id);
            while (true) {
                const { done, value } = await reader.read();

                if (done) {
                    // Stream completed normally
                    self.debugLog(debug, '[CentrifugeHttpStream] Stream', id, 'completed normally');
                    dotnetRef.invokeMethodAsync('OnClose', 0, 'stream closed');
                    // Note: Don't delete from streams here - let dispose() clean up
                    break;
                }

                // Send chunk to .NET as base64 string (JSInterop limitation)
                const base64 = btoa(String.fromCharCode.apply(null, value));
                dotnetRef.invokeMethodAsync('OnChunk', base64);
            }
        } catch (error) {
            self.debugLog(debug, '[CentrifugeHttpStream] Read loop error for stream', id, ':', error.name, error.message);
            if (error.name !== 'AbortError') {
                // Only report non-abort errors
                dotnetRef.invokeMethodAsync('OnError', 0, error.message || 'Stream read error');
            }
            dotnetRef.invokeMethodAsync('OnClose', 0, error.message || 'connection closed');
            // Note: Don't delete from streams here - let dispose() clean up
        }
    },

    /**
     * Sends data through emulation endpoint
     * @param {string} url - Emulation endpoint URL
     * @param {string} sessionId - Session ID
     * @param {string} node - Node ID
     * @param {number[]} data - Byte array to send
     */
    sendEmulation: async function (url, sessionId, node, data) {
        try {
            // Convert byte array to Uint8Array
            const bodyData = new Uint8Array(data);

            await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/octet-stream'
                },
                body: bodyData,
                mode: 'cors',
                credentials: 'same-origin'
            });
        } catch (error) {
            console.error('CentrifugeHttpStream.sendEmulation error:', error);
            throw error;
        }
    },

    /**
     * Closes the HTTP stream connection
     * @param {number} id - Stream ID
     */
    close: function (id) {
        const debug = this.streams[id]?.debug || false;
        this.debugLog(debug, '[CentrifugeHttpStream] close called for stream', id);
        const streamInfo = this.streams[id];
        if (!streamInfo) {
            this.debugLog(false, '[CentrifugeHttpStream] close - stream not found:', id);
            return; // Already closed or doesn't exist
        }

        try {
            // Abort the fetch request
            this.debugLog(debug, '[CentrifugeHttpStream] Aborting fetch for stream', id);
            streamInfo.abortController.abort();

            // Try to cancel the reader
            if (streamInfo.reader) {
                this.debugLog(debug, '[CentrifugeHttpStream] Canceling reader for stream', id);
                streamInfo.reader.cancel().catch((error) => {
                    this.debugLog(debug, '[CentrifugeHttpStream] Reader cancel error (expected):', error.message);
                    // Ignore cancel errors - expected when aborting
                });
            }
        } catch (error) {
            if (debug) console.error('[CentrifugeHttpStream] close error:', error);
        }

        // Note: Don't remove from registry here - let dispose() do that after cleanup
    },

    /**
     * Disposes a stream reference and removes it from registry (cleanup)
     * @param {number} id - Stream ID
     */
    dispose: function (id) {
        const debug = this.streams[id]?.debug || false;
        this.debugLog(debug, '[CentrifugeHttpStream] dispose called for stream', id);
        const streamInfo = this.streams[id];
        if (streamInfo) {
            this.debugLog(debug, '[CentrifugeHttpStream] Disposing stream', id);
            // Clear the .NET reference to prevent memory leaks
            streamInfo.dotnetRef = null;
            // Clear reader reference
            streamInfo.reader = null;
            streamInfo.abortController = null;
            // Remove from registry
            delete this.streams[id];
            this.debugLog(debug, '[CentrifugeHttpStream] Stream', id, 'disposed successfully');
        } else {
            this.debugLog(false, '[CentrifugeHttpStream] dispose - stream', id, 'not found (already disposed)');
        }
    }
};
