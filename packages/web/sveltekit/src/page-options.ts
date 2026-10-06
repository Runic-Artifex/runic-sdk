export const runicPrerenderedPageOptions = Object.freeze({
  prerender: true as const,
});

export const runicSpaPageOptions = Object.freeze({
  ssr: false as const,
  prerender: false as const,
});
