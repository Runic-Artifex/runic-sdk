import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { createApp } from "vue";
import { createInjectedMvvmDevelopmentTools } from "@webuitoolkit/mvvm";
import {
  createVueMvvmApplicationPlugin,
  startVueMvvmApplication,
} from "@webuitoolkit/mvvm-vue";
import App from "./App.vue";
import { CounterContract } from "./counter-contract.g";

const mock = import.meta.env.MODE === "mock"
  ? await import("./counter.mock")
  : undefined;
const development = createInjectedMvvmDevelopmentTools(
  CounterContract.memberMetadata,
);
const application = await startVueMvvmApplication({
  contract: CounterContract,
  ...(development === undefined ? {} : { inspector: development.inspector }),
  ...(mock === undefined
    ? {}
    : { channelFactory: mock.createCounterMockChannel }),
});
if (development !== undefined) {
  application.addCleanup(() => development.dispose());
}
createApp(App, { contract: application.contract })
  .use(createVueMvvmApplicationPlugin(application))
  .mount("#app");
