// Centrifuge HTTP Stream wrapper for Blazor WASM
// This provides a bridge between browser Fetch API with ReadableStream and .NET

window.CentrifugeHttpStream = {
    streams: {},

    debugLog: function(debug, ...args) {
        if (debug) {
            console.log(...args);
        }
    },

    /**
     * Registers an HTTP streaming connection under an ID chosen by the caller, then starts opening
     * it with Fetch API. The caller can close the stream with that ID before this call has returned
     * and while the request still awaits its response. The result arrives via OnOpen or OnError.
     * @param {number} id - Stream ID, unique across streams
     * @param {string} url - HTTP endpoint URL
     * @param {number[]} initialData - Initial data to send (connect command)
     * @param {object} dotnetRef - .NET object reference for callbacks
     * @param {boolean} debug - Enable debug logging
     */
    connect: function (id, url, initialData, dotnetRef, debug) {
        this.debugLog(debug, '[CentrifugeHttpStream] Connecting to:', url, 'with stream ID:', id);
        this.streams[id] = {
            reader: null,
            abortController: new AbortController(),
            dotnetRef: dotnetRef,
            id: id,
            reading: false,
            debug: debug
        };
        this.open(id, url, initialData);
    },

    /**
     * Sends the open request for a registered stream and reports how it went. If the stream is
     * closed (which aborts the request) or disposed first, nothing is reported.
     * @param {number} id - Stream ID
     * @param {string} url - HTTP endpoint URL
     * @param {number[]} initialData - Initial data to send (connect command)
     */
    open: async function (id, url, initialData) {
        const streamInfo = this.streams[id];
        const debug = streamInfo.debug;
        try {
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Accept': 'application/octet-stream',
                    'Content-Type': 'application/octet-stream'
                },
                body: new Uint8Array(initialData),
                signal: streamInfo.abortController.signal,
                mode: 'cors',
                credentials: 'same-origin'
            });

            this.debugLog(debug, '[CentrifugeHttpStream] Response received for stream', id, '- status:', response.status);
            if (!streamInfo.dotnetRef) return;

            if (!response.ok) {
                if (debug) console.error('[CentrifugeHttpStream] HTTP error for stream', id, ':', response.status);
                streamInfo.dotnetRef.invokeMethodAsync('OnError', response.status, `HTTP error ${response.status}`);
                return;
            }

            streamInfo.reader = response.body.getReader();
            streamInfo.dotnetRef.invokeMethodAsync('OnOpen');
            this.readLoop(id);
        } catch (error) {
            if (error.name === 'AbortError' || !streamInfo.dotnetRef) return;
            if (debug) console.error('[CentrifugeHttpStream] connect error for stream', id, ':', error);
            streamInfo.dotnetRef.invokeMethodAsync('OnError', 0, error.message || 'Connection failed');
        }
    },

    /**
     * Reading loop that processes incoming chunks. Stops reporting once the stream is disposed.
     * @param {number} id - Stream ID
     */
    readLoop: async function (id) {
        const streamInfo = this.streams[id];
        if (!streamInfo || streamInfo.reading) {
            return;
        }

        streamInfo.reading = true;
        const reader = streamInfo.reader;
        const debug = streamInfo.debug || false;
        const self = this;

        try {
            self.debugLog(debug, '[CentrifugeHttpStream] Starting read loop for stream', id);
            while (true) {
                const { done, value } = await reader.read();

                if (done) {
                    // Stream completed normally
                    self.debugLog(debug, '[CentrifugeHttpStream] Stream', id, 'completed normally');
                    streamInfo.dotnetRef?.invokeMethodAsync('OnClose', 0, 'stream closed');
                    // Note: Don't delete from streams here - let dispose() clean up
                    break;
                }

                // Pass Uint8Array directly - Blazor marshals it to byte[]
                streamInfo.dotnetRef?.invokeMethodAsync('OnChunk', value);
            }
        } catch (error) {
            self.debugLog(debug, '[CentrifugeHttpStream] Read loop error for stream', id, ':', error.name, error.message);
            if (error.name !== 'AbortError') {
                // Only report non-abort errors
                streamInfo.dotnetRef?.invokeMethodAsync('OnError', 0, error.message || 'Stream read error');
            }
            streamInfo.dotnetRef?.invokeMethodAsync('OnClose', 0, error.message || 'connection closed');
            // Note: Don't delete from streams here - let dispose() clean up
        }
    },

    /**
     * Sends data through emulation endpoint. The request shares its stream's abort signal, so
     * closing the stream aborts it and no request outlives its session.
     * @param {number} id - Stream ID
     * @param {string} url - Emulation endpoint URL
     * @param {number[]} data - Encoded emulation request (session, node and commands)
     * @returns {Promise<number>} HTTP status of the response; rejects if there was none (network error or abort)
     */
    sendEmulation: async function (id, url, data) {
        const signal = this.streams[id]?.abortController?.signal;
        if (!signal) {
            throw new Error('stream closed');
        }
        try {
            const response = await fetch(url, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/octet-stream'
                },
                body: new Uint8Array(data),
                signal: signal,
                mode: 'cors',
                credentials: 'same-origin'
            });
            return response.status;
        } catch (error) {
            if (error.name !== 'AbortError') {
                console.error('CentrifugeHttpStream.sendEmulation error:', error);
            }
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
