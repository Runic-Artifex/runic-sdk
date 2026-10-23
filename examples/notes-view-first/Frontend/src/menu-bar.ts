// The plain frontend renders no in-page menu bar. The React and Vue variants swap in their own module
// (React/src/menu-bar.ts, Vue/src/menu-bar.ts) as they swap in editor.ts; Svelte and Angular render one in
// their app component. See the README's "In-page menu bar".
export function mountMenuBar(_host: HTMLElement): () => void {
  return () => {};
}
