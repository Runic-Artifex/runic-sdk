import { revision } from "./value.js";

sessionStorage.setItem("loads", String(Number(sessionStorage.getItem("loads") ?? 0) + 1));
window.__loads = Number(sessionStorage.getItem("loads"));
window.__hmrUpdates = 0;
document.querySelector("#value").textContent = revision;
import.meta.hot?.accept("./value.js", module => {
  window.__hmrUpdates += 1;
  document.querySelector("#value").textContent = module.revision;
});
