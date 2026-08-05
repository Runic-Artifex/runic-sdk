import "./hmr-probe.css";

export const token = "baseline";

export function apply() {
  document.documentElement.dataset.runicToolkitHmrProbe = token;
}

apply();

if (import.meta.hot) {
  import.meta.hot.accept((updated) => updated?.apply());
}
