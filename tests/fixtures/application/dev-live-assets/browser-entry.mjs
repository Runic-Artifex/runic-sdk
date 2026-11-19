// `dotnet runic dev --browser`'s address for a client that is not a browser (#1187, D077.12, D077.15). The address,
// http://<label>.localhost:<port>/, is meant for a browser: browsers resolve names under .localhost to the loopback
// interface themselves, but Node's resolver is the system's, and Windows (and Linux without systemd's resolver or
// nss-myhostname) does not resolve them. So this client connects to 127.0.0.1 and sends the session's name as Host, as a
// browser does. It then admits itself as a browser is admitted: the one-time code of the `[dev] Browser:` line gets a
// pending cookie, and the first navigation to an entry with that cookie confirms it.
import http from "node:http";

/**
 * Admits a client with the code URL of a `[dev] Browser:` line. Returns `get(path)`, which requests a path on the
 * address with the admitted cookie and follows the entry's redirects, and resolves to `{ status, headers, body }`.
 */
export async function admitToBrowserEntry(codeUrl, { timeout = 10_000 } = {}) {
  const address = new URL(codeUrl);
  if (!/^[a-z0-9]+\.localhost$/.test(address.hostname) || !address.searchParams.has("code"))
    throw new Error(`${codeUrl} is not a --browser code URL (http://<label>.localhost:<port>/?code=…)`);
  const request = (path, cookie) => new Promise((resolve, reject) => {
    const outgoing = http.request({
      host: "127.0.0.1",
      port: Number(address.port),
      path,
      method: "GET",
      // The session's name, which the address answers alone; a navigation's Sec-Fetch-Site.
      headers: { Host: address.host, "Sec-Fetch-Site": "none", ...(cookie ? { Cookie: cookie } : {}) },
    }, response => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", chunk => { body += chunk; });
      response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, body }));
    });
    outgoing.on("error", reject);
    outgoing.setTimeout(timeout, () => outgoing.destroy(new Error(`GET ${path} on ${address.host} timed out`)));
    outgoing.end();
  });

  const admission = await request(address.pathname + address.search);
  const setCookie = [admission.headers["set-cookie"] ?? []].flat()[0];
  if (admission.status !== 200 || !setCookie)
    throw new Error(`The code did not admit the client: HTTP ${admission.status} ${admission.body.slice(0, 200)}`);
  const cookie = setCookie.split(";")[0];

  const get = async path => {
    for (let hop = 0; hop < 5; hop++) {
      const answer = await request(path, cookie);
      if (answer.status !== 302 || !answer.headers.location) return answer;
      const next = new URL(answer.headers.location, address);
      if (next.host !== address.host) return answer;
      path = next.pathname + next.search;
    }
    throw new Error(`${path}: too many redirects`);
  };
  // The navigation that the admission's page makes, which makes the cookie the admitted one.
  const confirmation = await get(address.pathname);
  if (confirmation.status === 403) throw new Error(`The admission was not confirmed: ${confirmation.body.slice(0, 200)}`);
  return { cookie, get };
}
