import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { mount, unmount } from "svelte";
import { startSvelteMvvmApplication } from "@webuitoolkit/mvvm-svelte";
import App from "./App.svelte";
import { CounterContract } from "./counter-contract.g";

const mock = import.meta.env.MODE === "mock"
  ? await import("./counter.mock")
  : undefined;
const application = await startSvelteMvvmApplication({
  contract: CounterContract,
  ...(mock === undefined
    ? {}
    : { channelFactory: mock.createCounterMockChannel }),
});
const app = mount(App, { target: document.querySelector("#app")!, props: { application } });
application.addCleanup(() => unmount(app));
