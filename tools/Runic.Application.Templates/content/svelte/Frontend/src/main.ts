import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { mount } from "svelte";
import App from "./App.svelte";

mount(App, { target: document.querySelector("#app")! });
