import type { Handle } from "@sveltejs/kit";

export const handle: Handle = ({ event, resolve }) => {
  const segment = event.url.pathname.split("/")[1];
  const locale = segment === "de" ? "de" : "en";
  return resolve(event, {
    transformPageChunk: ({ html }) => html.replace('<html lang="en">', `<html lang="${locale}">`),
  });
};
