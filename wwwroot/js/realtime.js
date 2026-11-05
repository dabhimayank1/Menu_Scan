/**
 * MenuScan Real-time Hub & Reconnect Helper (Phase 4)
 */

function escapeHtml(str) {
    if (str === null || str === undefined) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}
window.escapeHtml = escapeHtml;

function createDebouncedReload(delay = 3000) {
    let timer = null;
    return function () {
        if (timer) {
            clearTimeout(timer);
        }
        timer = setTimeout(() => {
            timer = null;
            if (typeof window.__customReloadHandler === 'function') {
                window.__customReloadHandler();
            } else {
                window.location.reload();
            }
        }, delay);
    };
}
window.createDebouncedReload = createDebouncedReload;
window.debouncedReload = createDebouncedReload(3000);

/**
 * Initializes and starts a SignalR connection to /hubs/order with robust reconnection,
 * automatic re-joining, exponential/periodic 5s retries on failure, and status reporting.
 * 
 * @param {Object} options
 * @param {Function} [options.onJoin] - Called after every successful start and onreconnected: async (conn) => { ... }
 * @param {Object} [options.handlers] - Map of eventName -> callback function
 * @param {Function} [options.onStatus] - Called with 'Connected' | 'Reconnecting' | 'Offline'
 * @returns {Object} { connection, start }
 */
function startHub(options) {
    const opts = options || {};
    const onJoin = opts.onJoin;
    const handlers = opts.handlers || {};
    const onStatus = opts.onStatus;

    function reportStatus(status) {
        if (typeof onStatus === 'function') {
            try {
                onStatus(status);
            } catch (err) {
                console.error("realtime.js: onStatus callback error:", err);
            }
        }
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/order")
        .withAutomaticReconnect([0, 1000, 2000, 5000])
        .build();

    connection.serverTimeoutInMilliseconds = 15000;

    // Register event handlers
    for (const [eventName, handler] of Object.entries(handlers)) {
        if (typeof handler === 'function') {
            connection.on(eventName, handler);
        }
    }

    let retryTimer = null;
    let isStarting = false;

    function scheduleRetry() {
        if (retryTimer) return;
        retryTimer = setTimeout(() => {
            retryTimer = null;
            start();
        }, 5000);
    }

    connection.onreconnecting((error) => {
        console.warn("SignalR reconnecting...", error);
        reportStatus("Reconnecting");
    });

    connection.onreconnected(async (connectionId) => {
        console.log("SignalR reconnected successfully. ID:", connectionId);
        reportStatus("Connected");
        if (typeof onJoin === 'function') {
            try {
                await onJoin(connection);
            } catch (err) {
                console.error("SignalR: Error during onJoin after reconnect:", err);
            }
        }
    });

    connection.onclose((error) => {
        console.warn("SignalR connection closed:", error);
        reportStatus("Offline");
        scheduleRetry();
    });

    window.addEventListener('offline', () => {
        console.warn("Browser offline event detected.");
        reportStatus("Reconnecting");
    });

    window.addEventListener('online', () => {
        console.log("Browser online event detected.");
        if (connection.state === signalR.HubConnectionState.Disconnected) {
            start();
        }
    });

    async function start() {
        if (isStarting) return;
        if (connection.state === signalR.HubConnectionState.Connected) {
            reportStatus("Connected");
            return;
        }

        isStarting = true;
        try {
            await connection.start();
            console.log("SignalR connected successfully.");
            reportStatus("Connected");
            if (typeof onJoin === 'function') {
                try {
                    await onJoin(connection);
                } catch (err) {
                    console.error("SignalR: Error during onJoin after start:", err);
                }
            }
        } catch (err) {
            console.error("SignalR initial start failed:", err);
            reportStatus("Offline");
            scheduleRetry();
        } finally {
            isStarting = false;
        }
    }

    // Trigger initial connection start
    start();

    return {
        connection: connection,
        start: start
    };
}
window.startHub = startHub;
