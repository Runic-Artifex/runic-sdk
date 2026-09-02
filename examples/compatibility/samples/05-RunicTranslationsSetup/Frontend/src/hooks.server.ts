import type { Handle } from "@sveltejs/kit";
import { baseLocale, resolveLocale } from "virtual:runic-translations/setup/runtime";
import { runWithLocale } from "virtual:runic-translations/setup/server";

export const handle: Handle = ({ event, resolve }) => {
  const segment = event.url.pathname.split("/")[1];
  const locale = segment ? resolveLocale(segment) : baseLocale;
  return runWithLocale(locale, () => resolve(event, {
    transformPageChunk: ({ html }) => html.replace('<html lang="en">', `<html lang="${locale}">`),
  }));
};
