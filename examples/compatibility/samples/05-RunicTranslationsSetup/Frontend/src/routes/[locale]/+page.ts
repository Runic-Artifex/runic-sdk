import { error } from "@sveltejs/kit";
import { resolveLocale, type Locale } from "virtual:runic-translations/setup/runtime";
import type { PageLoad } from "./$types";

export const load: PageLoad = ({ params }) => {
  const locale = resolveLocale(params.locale);
  if (locale !== params.locale) error(404, `Unsupported locale '${params.locale}'.`);
  return { locale: locale as Locale };
};
