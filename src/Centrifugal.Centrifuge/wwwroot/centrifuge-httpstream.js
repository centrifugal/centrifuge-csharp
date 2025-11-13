// Centrifuge HTTP Stream wrapper for Blazor WASM
// This provides a bridge between browser Fetch API with ReadableStream and .NET

window.CentrifugeHttpStream = {
    streams: {},
    nextId: 1,

    /**
     * Creates and opens an HTTP streaming connection using Fetch API
     * @param {string} url - HTTP endpoint URL
     * @param {number[]} initialData - Initial data to send (connect command)
     * @param {object} dotnetRef - .NET object reference for callbacks
     * @returns {number} Stream ID
     */
    connect: async function (url, initialData, dotnetRef) {
        const id = this.nextId++;
        const abortController = new AbortController();

        try {
            // Convert byte array to Uint8Array
            const bodyData = new Uint8Array(initialData);

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

            if (!response.ok) {
                const statusCode = response.status;
                dotnetRef.invokeMethodAsync('OnError', statusCode, `HTTP error ${statusCode}`);
                return id;
            }

            const streamInfo = {
                reader: response.body.getReader(),
                abortController: abortController,
                dotnetRef: dotnetRef,
                id: id,
                reading: false
            };

            this.streams[id] = streamInfo;

            // Notify connection opened
            dotnetRef.invokeMethodAsync('OnOpen');

            // Start reading loop
            this.readLoop(id);

            return id;
        } catch (error) {
            console.error('CentrifugeHttpStream.connect error:', error);
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

        try {
            while (true) {
                const { done, value } = await reader.read();

                if (done) {
                    // Stream completed normally
                    dotnetRef.invokeMethodAsync('OnClose', 0, 'stream closed');
                    delete this.streams[id];
                    break;
                }

                // Send chunk to .NET as byte array
                // Convert Uint8Array to regular array for .NET interop
                dotnetRef.invokeMethodAsync('OnChunk', Array.from(value));
            }
        } catch (error) {
            if (error.name !== 'AbortError') {
                // Only report non-abort errors
                dotnetRef.invokeMethodAsync('OnError', 0, error.message || 'Stream read error');
            }
            dotnetRef.invokeMethodAsync('OnClose', 0, error.message || 'connection closed');
            delete this.streams[id];
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
        const streamInfo = this.streams[id];
        if (!streamInfo) {
            return; // Already closed or doesn't exist
        }

        try {
            // Abort the fetch request
            streamInfo.abortController.abort();

            // Try to cancel the reader
            if (streamInfo.reader) {
                streamInfo.reader.cancel().catch(() => {
                    // Ignore cancel errors
                });
            }
        } catch (error) {
            console.error('CentrifugeHttpStream.close error:', error);
        }

        delete this.streams[id];
    },

    /**
     * Disposes a stream reference without closing (cleanup)
     * @param {number} id - Stream ID
     */
    dispose: function (id) {
        const streamInfo = this.streams[id];
        if (streamInfo) {
            streamInfo.dotnetRef = null;
            delete this.streams[id];
        }
    }
};
