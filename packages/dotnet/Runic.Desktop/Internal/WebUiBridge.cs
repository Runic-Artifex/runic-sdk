namespace Runic.Desktop.Internal;

internal static class WebUiBridge
{
    // The reserved raw-data function of Views messages (WebUiSession.PushViewsMessageAsync). The script handles it
    // itself; a page cannot replace it. Keep it in sync with VIEWS_PUSH below.
    internal const string ViewsPushFunction = "__runicViewsPush";

    // The reserved raw-data function of DesktopWindow.AnnounceAsync while the page has the keyboard focus
    // (WebUiSession.AnnounceAsync): the politeness, NUL and the message; an empty message adds the page's live regions.
    // Keep it in sync with ANNOUNCE below.
    internal const string AnnounceFunction = "__runicAnnounce";

    // Implements the wire and public browser contract of webui-dev/webui's
    // MIT-licensed TypeScript bridge at the pinned upstream revision. One difference: raw data for a name that is not
    // a page function is dropped instead of being evaluated as a call expression, so no host-to-page traffic but
    // CMD_JS/CMD_JS_QUICK (DesktopSurface.ExecuteJavaScriptAsync and the internal RunJavaScriptAsync) needs eval.
    internal const string Script = """
        (() => {
          "use strict";

          const SIGNATURE = 0xDD;
          const CMD_JS = 0xFE;
          const CMD_JS_QUICK = 0xFD;
          const CMD_CLICK = 0xFC;
          const CMD_NAVIGATION = 0xFB;
          const CMD_CLOSE = 0xFA;
          const CMD_CALL_FUNC = 0xF9;
          const CMD_SEND_RAW = 0xF8;
          const CMD_ADD_ID = 0xF7;
          const CMD_MULTI = 0xF6;
          const CMD_CHECK_TK = 0xF5;
          const CMD_WINDOW_DRAG = 0xF4;
          const CMD_WINDOW_RESIZED = 0xF3;
          const HEADER_SIZE = 8;
          const MULTI_CHUNK_SIZE = 65500;
          const TOKEN = __TOKEN__;
          const PORT = __PORT__;
          const BASE_PATH = "__BASE_PATH__";
          const SESSION_CREDENTIAL = __SESSION_CREDENTIAL__;
          const CUSTOM_WINDOW_DRAG = __CUSTOM_WINDOW_DRAG__;
          const VIEWS_PUSH = "__runicViewsPush";
          const ANNOUNCE = "__runicAnnounce";
          const encoder = new TextEncoder();
          const decoder = new TextDecoder();
          const AsyncFunction = Object.getPrototypeOf(async function() {}).constructor;
          const hasNavigationApi = "navigation" in globalThis;
          const bindings = new Set();
          const pendingCalls = new Map();
          const connectionWaiters = new Set();
          const liveRegions = new Map();
          const viewsMessageListeners = new Set();
          const viewsConnectionListeners = new Set();
          let viewsConnected = false;
          let nextCallId = 0;
          let socket;
          let tokenAccepted = false;
          let allEvents = false;
          let allowNavigation = true;
          let reconnect = true;
          let reconnectTimer;
          let sendQueue = Promise.resolve();
          let eventCallback = null;
          let lastConnectionEvent = -1;
          let logging = false;
          let resolveInitialConnection;
          let rejectInitialConnection;
          const connected = new Promise((resolve, reject) => {
            resolveInitialConnection = resolve;
            rejectInitialConnection = reject;
          });

          function createPacket(dataLength, id, command) {
            const packet = new Uint8Array(HEADER_SIZE + dataLength);
            const view = new DataView(packet.buffer);
            packet[0] = SIGNATURE;
            view.setUint32(1, TOKEN, true);
            view.setUint16(5, id, true);
            packet[7] = command;
            return packet;
          }

          function readString(packet, offset) {
            let end = packet.indexOf(0, offset);
            if (end < 0) end = packet.length;
            return decoder.decode(packet.subarray(offset, end));
          }

          function notifyConnectionEvent(value) {
            if (eventCallback && lastConnectionEvent !== value) {
              lastConnectionEvent = value;
              eventCallback(value);
            }
          }

          function resolveConnectionWaiters() {
            for (const waiter of connectionWaiters) waiter.resolve();
            connectionWaiters.clear();
          }

          function waitForConnection() {
            if (tokenAccepted && socket?.readyState === WebSocket.OPEN) return Promise.resolve();
            return new Promise((resolve, reject) => {
              const waiter = { resolve, reject };
              connectionWaiters.add(waiter);
              setTimeout(() => {
                if (connectionWaiters.delete(waiter)) {
                  reject(new Error("Timed out waiting for the WebUI connection."));
                }
              }, 10000);
            });
          }

          async function sendPacketNow(packet) {
            if (!socket || socket.readyState !== WebSocket.OPEN) {
              throw new Error("WebSocket is not connected");
            }

            if (packet.length < MULTI_CHUNK_SIZE) {
              socket.send(packet);
              return;
            }

            const lengthBytes = encoder.encode(String(packet.length));
            const header = new Uint8Array(HEADER_SIZE + lengthBytes.length + 1);
            header[0] = SIGNATURE;
            header[7] = CMD_MULTI;
            header.set(lengthBytes, HEADER_SIZE);
            socket.send(header);
            for (let offset = 0; offset < packet.length; offset += MULTI_CHUNK_SIZE) {
              socket.send(packet.subarray(offset, Math.min(offset + MULTI_CHUNK_SIZE, packet.length)));
            }
          }

          function sendPacket(packet) {
            const operation = sendQueue.then(() => sendPacketNow(packet));
            sendQueue = operation.catch(() => {});
            return operation;
          }

          function installBinding(name) {
            if (name === "") {
              allEvents = true;
              allowNavigation = false;
              return;
            }
            if (bindings.has(name)) return;
            bindings.add(name);
            if (name !== "__webui_core_api__") {
              const fn = (...args) => call(name, ...args);
              if (typeof globalThis[name] !== "function") globalThis[name] = fn;
              if (typeof webui[name] !== "function") webui[name] = fn;
            }
          }

          function encodeArgument(argument) {
            if (argument instanceof Uint8Array) return argument;
            return encoder.encode(String(argument));
          }

          function sendCall(name, args) {
            const id = nextCallId = (nextCallId - 1) & 0xFFFF;
            const nameBytes = encoder.encode(name);
            const argumentBytes = args.map(encodeArgument);
            const lengthsBytes = encoder.encode(argumentBytes.map(value => value.length).join(";"));
            const valuesLength = argumentBytes.reduce((length, value) => length + value.length + 1, 0);
            const packet = createPacket(nameBytes.length + 1 + lengthsBytes.length + 1 + valuesLength, id, CMD_CALL_FUNC);
            let offset = HEADER_SIZE;
            packet.set(nameBytes, offset);
            offset += nameBytes.length + 1;
            packet.set(lengthsBytes, offset);
            offset += lengthsBytes.length + 1;
            for (const value of argumentBytes) {
              packet.set(value, offset);
              offset += value.length + 1;
            }

            return new Promise((resolve, reject) => {
              pendingCalls.set(id, { resolve, reject });
              sendPacket(packet).catch(error => {
                pendingCalls.delete(id);
                reject(error);
              });
            });
          }

          async function call(name, ...args) {
            if (!name) throw new SyntaxError("No binding name is provided");
            await waitForConnection();
            name = String(name);
            if (name !== "__webui_core_api__" && !allEvents && !bindings.has(name)) {
              throw new ReferenceError(`No binding was found for "${name}"`);
            }
            return sendCall(name, args);
          }

          function callCore(name, ...args) {
            return call("__webui_core_api__", name, ...args);
          }

          function sendTextEvent(command, text) {
            const value = encoder.encode(text);
            const packet = createPacket(value.length + 1, 0, command);
            packet.set(value, HEADER_SIZE);
            return sendPacket(packet);
          }

          function checkToken() {
            const packet = createPacket(1, 0, CMD_CHECK_TK);
            return sendPacket(packet);
          }

          async function executeJavaScript(packet, id, quick) {
            const script = readString(packet, HEADER_SIZE).replace(/(?:\r\n|\r|\n)/g, "\n");
            try {
              const result = await AsyncFunction(script)();
              if (quick) return;
              const value = result instanceof Uint8Array ? result : encoder.encode(String(result));
              const response = createPacket(value.length + 2, id, CMD_JS);
              response[HEADER_SIZE] = 0;
              response.set(value, HEADER_SIZE + 1);
              await sendPacket(response);
            } catch (error) {
              if (quick) return;
              const value = encoder.encode(error instanceof Error ? error.message : String(error));
              const response = createPacket(value.length + 2, id, CMD_JS);
              response[HEADER_SIZE] = 1;
              response.set(value, HEADER_SIZE + 1);
              await sendPacket(response);
            }
          }

          // A Views message (Views protocol 4) is one UTF-8 JSON object, which has no raw NUL. Its text goes to the
          // listeners that runic-desktop-views.js registers on __runicDesktopViews, in the order the messages arrived.
          // Parsing nothing and evaluating no script lets a Content-Security-Policy without 'unsafe-eval' receive it.
          function dispatchViewsMessage(data) {
            if (viewsMessageListeners.size === 0) return;
            const message = decoder.decode(data);
            for (const listener of [...viewsMessageListeners]) {
              try { listener(message); } catch (error) { console.error("Runic Views message listener failed", error); }
            }
          }

          // Reports whether the Bridge connection is open and its token accepted. Each true after false is a new
          // connection: a new WebSocket, which .NET knows by a new connection id.
          function reportViewsConnection(connected) {
            if (connected === viewsConnected) return;
            viewsConnected = connected;
            for (const listener of [...viewsConnectionListeners]) {
              try { listener(connected); } catch (error) { console.error("Runic connection listener failed", error); }
            }
          }

          // An announcement is the politeness ("polite" or "assertive"), NUL and the UTF-8 message. The host sends it
          // while the keyboard focus is in the page, where a screen reader speaks the page's live regions but may
          // ignore the window's own notification (NVDA on WebView2). Each politeness has a visually hidden live region.
          // The host asks for both with an empty message once the page connects, as a screen reader ignores a change to
          // a live region it does not know yet; one the page has removed is added again, and a message for it waits a
          // moment. Each message is a new line, so a repeated message is said again; the line is removed once it has
          // been said. The regions are styled through the CSSOM, which a Content-Security-Policy without
          // 'unsafe-inline' allows.
          function liveRegion(politeness) {
            let region = liveRegions.get(politeness);
            if (region?.element.isConnected) return region;
            const element = document.createElement("div");
            element.setAttribute("aria-live", politeness);
            element.setAttribute("data-runic-announcer", politeness);
            Object.assign(element.style, {
              position: "absolute", width: "1px", height: "1px", margin: "-1px", padding: "0", border: "0",
              overflow: "hidden", clipPath: "inset(50%)", whiteSpace: "nowrap"
            });
            (document.body ?? document.documentElement).append(element);
            region = { element, ready: new Promise(resolve => setTimeout(resolve, 500)) };
            liveRegions.set(politeness, region);
            return region;
          }

          function announce(data) {
            const separator = data.indexOf(0);
            if (separator < 0) return;
            const politeness = decoder.decode(data.subarray(0, separator)) === "assertive" ? "assertive" : "polite";
            const message = decoder.decode(data.subarray(separator + 1));
            if (message === "") {
              const prepare = () => { liveRegion("polite"); liveRegion("assertive"); };
              if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", prepare, { once: true });
              else prepare();
              return;
            }
            const { element, ready } = liveRegion(politeness);
            ready.then(() => {
              const line = document.createElement("div");
              line.textContent = message;
              element.append(line);
              setTimeout(() => line.remove(), 10000);
            });
          }

          async function receivePacket(event) {
            const packet = new Uint8Array(event.data);
            if (packet.length < HEADER_SIZE || packet[0] !== SIGNATURE) return;
            const view = new DataView(packet.buffer, packet.byteOffset, packet.byteLength);
            const id = view.getUint16(5, true);
            const command = packet[7];
            switch (command) {
              case CMD_CHECK_TK: {
                if (packet[HEADER_SIZE] !== 1) {
                  reconnect = false;
                  rejectInitialConnection(new Error("The WebUI token was rejected."));
                  globalThis.location.reload();
                  return;
                }
                tokenAccepted = true;
                bindings.clear();
                allEvents = false;
                const advertisedBindings = readString(packet, HEADER_SIZE + 1);
                // The wire list ends with a delimiter. Remove exactly that delimiter,
                // preserving a real empty (all-events) binding before it.
                if (advertisedBindings.length > 0) {
                  const names = advertisedBindings.endsWith(",")
                    ? advertisedBindings.slice(0, -1) : advertisedBindings;
                  names.split(",").forEach(installBinding);
                }
                resolveInitialConnection(webui);
                resolveConnectionWaiters();
                notifyConnectionEvent(webui.event.CONNECTED);
                reportViewsConnection(true);
                return;
              }
              case CMD_JS:
                await executeJavaScript(packet, id, false);
                return;
              case CMD_JS_QUICK:
                await executeJavaScript(packet, id, true);
                return;
              case CMD_CALL_FUNC: {
                const operation = pendingCalls.get(id);
                if (!operation) return;
                pendingCalls.delete(id);
                operation.resolve(readString(packet, HEADER_SIZE));
                return;
              }
              case CMD_SEND_RAW: {
                let separator = packet.indexOf(0, HEADER_SIZE);
                if (separator < 0) separator = packet.length;
                const functionName = decoder.decode(packet.subarray(HEADER_SIZE, separator));
                const start = separator + 1;
                const end = Math.max(start, packet.length - 1);
                // The reserved functions decode the data at once, so they read it in place.
                if (functionName === VIEWS_PUSH) {
                  dispatchViewsMessage(packet.subarray(start, end));
                  return;
                }
                if (functionName === ANNOUNCE) {
                  announce(packet.subarray(start, end));
                  return;
                }
                const handler = globalThis[functionName];
                if (typeof handler === "function") handler(packet.slice(start, end));
                else if (logging) console.warn(`WebUI bridge: no page function ${functionName} receives raw data`);
                return;
              }
              case CMD_ADD_ID:
                installBinding(readString(packet, HEADER_SIZE));
                return;
              case CMD_NAVIGATION: {
                reconnect = false;
                allowNavigation = true;
                const url = readString(packet, HEADER_SIZE);
                socket.close();
                globalThis.location.replace(url);
                return;
              }
              case CMD_CLOSE:
                reconnect = false;
                socket.close();
                setTimeout(() => globalThis.close(), 1000);
                return;
              case CMD_WINDOW_RESIZED:
                return;
            }
          }

          function connect() {
            clearTimeout(reconnectTimer);
            tokenAccepted = false;
            const endpoint = new URL(`${BASE_PATH}/_webui_ws_connect`, window.location.href);
            endpoint.protocol = endpoint.protocol === "https:" ? "wss:" : "ws:";
            endpoint.port = String(PORT);
            socket = SESSION_CREDENTIAL.length === 0
              ? new WebSocket(endpoint)
              : new WebSocket(endpoint, `runic-desktop.${SESSION_CREDENTIAL}`);
            socket.binaryType = "arraybuffer";
            socket.addEventListener("open", () => checkToken().catch(() => {}));
            socket.addEventListener("message", event => receivePacket(event).catch(error => {
              if (logging) console.error("WebUI bridge receive failure", error);
            }));
            socket.addEventListener("close", () => {
              tokenAccepted = false;
              const error = new Error("The WebUI connection was closed.");
              for (const operation of pendingCalls.values()) operation.reject(error);
              pendingCalls.clear();
              notifyConnectionEvent(webui.event.DISCONNECTED);
              reportViewsConnection(false);
              if (reconnect) reconnectTimer = setTimeout(connect, 500);
            });
          }

          function handleDocumentClick(event) {
            const target = event.target instanceof Element ? event.target : null;
            const element = target?.closest("[id]");
            if (tokenAccepted && element?.id && (allEvents || bindings.has(element.id))) {
              sendTextEvent(CMD_CLICK, element.id).catch(() => {});
            }
            const anchor = target?.closest("a[href]");
            if (!hasNavigationApi && anchor && allEvents && !allowNavigation && tokenAccepted) {
              event.preventDefault();
              sendTextEvent(CMD_NAVIGATION, anchor.href).catch(() => {});
            }
          }

          function resolveDragRegion(element) {
            while (element) {
              const region = globalThis.getComputedStyle(element).getPropertyValue("--webui-app-region").trim();
              if (region === "drag" || region === "no-drag") return region;
              element = element.parentElement;
            }
            return "";
          }

          function installCustomWindowDrag() {
            if (!CUSTOM_WINDOW_DRAG) return;
            let armed = false;
            let dragging = false;
            document.addEventListener("mousedown", event => {
              dragging = false;
              armed = event.button === 0 && resolveDragRegion(event.target instanceof Element ? event.target : null) === "drag";
            });
            document.addEventListener("mousemove", event => {
              if (event.buttons !== 1) {
                armed = false;
                dragging = false;
              } else if (armed && !dragging && tokenAccepted) {
                dragging = true;
                sendPacket(createPacket(0, 0, CMD_WINDOW_DRAG)).catch(() => {});
              }
            });
            document.addEventListener("mouseup", () => {
              armed = false;
              dragging = false;
            });
          }

          const webui = {
            call,
            callCore,
            connected,
            event: Object.freeze({ CONNECTED: 0, DISCONNECTED: 1 }),
            isConnected: () => socket?.readyState === WebSocket.OPEN && tokenAccepted,
            setEventCallback: callback => { eventCallback = callback; },
            setLogging: status => { logging = Boolean(status); },
            encode: data => btoa(data),
            decode: data => atob(data),
            allowNavigation: status => { allowNavigation = Boolean(status); },
            isHighContrast: async () => (await callCore("high_contrast")) === "1"
          };

          Object.defineProperty(globalThis, "webui", {
            configurable: false,
            enumerable: true,
            value: webui
          });
          // The Views host script (runic-desktop-views.js) builds __runicBridge on these: the Views messages of the
          // reserved raw-data function and the connection's transitions, reported directly from the WebSocket instead
          // of polled, and without taking the application's webui.setEventCallback.
          Object.defineProperty(globalThis, "__runicDesktopViews", {
            configurable: false,
            enumerable: false,
            value: Object.freeze({
              // Each registration is its own entry, so a function registered twice is called twice and each
              // returned function removes only its own registration.
              onMessage(listener) {
                const entry = message => listener(message);
                viewsMessageListeners.add(entry);
                return () => { viewsMessageListeners.delete(entry); };
              },
              onConnectionChange(listener) {
                const entry = connected => listener(connected);
                viewsConnectionListeners.add(entry);
                return () => { viewsConnectionListeners.delete(entry); };
              },
              isConnected: () => viewsConnected
            })
          });
          document.addEventListener("click", handleDocumentClick);
          installCustomWindowDrag();
          if (hasNavigationApi) {
            globalThis.navigation.addEventListener("navigate", event => {
              if (allEvents && !allowNavigation && tokenAccepted) {
                event.preventDefault();
                sendTextEvent(CMD_NAVIGATION, event.destination.url).catch(() => {});
              }
            });
          }
          // pagehide, not beforeunload: the window's web content policy can cancel a navigation after beforeunload
          // (an external link opens in the browser instead), and the page must keep its Bridge then. A page restored
          // from the back-forward cache connects again.
          globalThis.addEventListener("pagehide", () => {
            reconnect = false;
            socket?.close();
          });
          globalThis.addEventListener("pageshow", event => {
            if (!event.persisted) return;
            reconnect = true;
            connect();
          });
          setInterval(() => {
            if (socket?.readyState === WebSocket.OPEN) sendPacket(encoder.encode("ping")).catch(() => {});
          }, 20000);
          connect();
        })();
        """;
}
