namespace RunicToolkit.Hosting.CsWebUi;

internal static class CsWebUiDesktopBridgeScript
{
    internal const string Bootstrap = """
        (() => {
          if (globalThis.__runicToolkitDesktop) return;
          const maxBytes = 16 * 1024 * 1024;
          const send = message => {
            const call = globalThis.__runicToolkit_desktop_result(JSON.stringify(message));
            if (call && typeof call.catch === "function") call.catch(() => {});
          };
          const bytesToBase64 = bytes => {
            let binary = "";
            const chunk = 0x8000;
            for (let offset = 0; offset < bytes.length; offset += chunk) {
              binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
            }
            return btoa(binary);
          };
          const base64ToBytes = value => {
            const binary = atob(value);
            const bytes = new Uint8Array(binary.length);
            for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
            return bytes;
          };
          const readFiles = async files => {
            let total = 0;
            const result = [];
            for (const file of files) {
              total += file.size;
              if (total > maxBytes) throw new Error("Selected files exceed the 16 MiB desktop bridge limit.");
              const bytes = new Uint8Array(await file.arrayBuffer());
              result.push({
                name: file.name,
                mediaType: file.type || "application/octet-stream",
                content: bytesToBase64(bytes)
              });
            }
            return result;
          };
          const accelerators = new Map();
          document.addEventListener("keydown", event => {
            for (const [id, item] of accelerators) {
              if (event.key === item.key &&
                  event.ctrlKey === item.control &&
                  event.altKey === item.alternate &&
                  event.shiftKey === item.shift &&
                  event.metaKey === item.meta) {
                event.preventDefault();
                send({ kind: "event", name: "accelerator", id, payload: {} });
                break;
              }
            }
          }, true);
          document.addEventListener("dragover", event => event.preventDefault(), true);
          document.addEventListener("drop", async event => {
            event.preventDefault();
            try {
              const files = await readFiles(event.dataTransfer?.files || []);
              const text = event.dataTransfer?.getData("text/plain") || null;
              send({ kind: "event", name: "drop", id: "root", payload: { files, text } });
            } catch (error) {
              send({
                kind: "event",
                name: "drop-error",
                id: "root",
                payload: { message: String(error?.message || error) }
              });
            }
          }, true);
          const invoke = async (id, operation, payload) => {
            try {
              let value = null;
              switch (operation) {
                case "clipboard.read":
                  value = await navigator.clipboard.readText();
                  break;
                case "clipboard.write":
                  await navigator.clipboard.writeText(payload.text);
                  break;
                case "files.open":
                  value = await new Promise((resolve, reject) => {
                    const input = document.createElement("input");
                    input.type = "file";
                    input.multiple = !!payload.allowMultiple;
                    input.accept = payload.accept || "";
                    input.style.display = "none";
                    const finish = () => input.remove();
                    input.addEventListener("change", async () => {
                      try { resolve(await readFiles(input.files || [])); }
                      catch (error) { reject(error); }
                      finally { finish(); }
                    }, { once: true });
                    input.addEventListener("cancel", () => { resolve([]); finish(); }, { once: true });
                    document.body.append(input);
                    input.click();
                  });
                  break;
                case "files.save": {
                  const bytes = base64ToBytes(payload.content);
                  if (bytes.length > maxBytes) throw new Error("Save content exceeds the 16 MiB desktop bridge limit.");
                  const blob = new Blob([bytes], { type: payload.mediaType || "application/octet-stream" });
                  const url = URL.createObjectURL(blob);
                  const link = document.createElement("a");
                  link.href = url;
                  link.download = payload.fileName;
                  link.style.display = "none";
                  document.body.append(link);
                  link.click();
                  link.remove();
                  setTimeout(() => URL.revokeObjectURL(url), 0);
                  value = true;
                  break;
                }
                case "notification.show": {
                  let permission = Notification.permission;
                  if (permission === "default") permission = await Notification.requestPermission();
                  if (permission !== "granted") throw new Error("Notification permission was not granted.");
                  new Notification(payload.title, { body: payload.body, tag: payload.tag || undefined });
                  break;
                }
                case "storage.read":
                  value = localStorage.getItem(payload.key);
                  break;
                case "storage.write":
                  localStorage.setItem(payload.key, payload.value);
                  break;
                case "storage.remove":
                  localStorage.removeItem(payload.key);
                  break;
                case "accelerator.register":
                  accelerators.set(payload.id, payload.accelerator);
                  break;
                case "accelerator.remove":
                  accelerators.delete(payload.id);
                  break;
                default:
                  throw new Error(`Unsupported desktop bridge operation '${operation}'.`);
              }
              send({ kind: "result", id, ok: true, value });
            } catch (error) {
              send({
                kind: "result",
                id,
                ok: false,
                error: String(error?.message || error).slice(0, 1024)
              });
            }
          };
          Object.defineProperty(globalThis, "__runicToolkitDesktop", {
            value: Object.freeze({ invoke }),
            configurable: false,
            enumerable: false,
            writable: false
          });
        })();
        """;
}
