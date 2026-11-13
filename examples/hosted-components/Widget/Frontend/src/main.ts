import { renderWidget } from "./widget";
import "./widget.css";

// The widget's own page, in its own window: the page's Runic instance.
const name = await renderWidget(document.querySelector<HTMLElement>("#widget")!);
document.title = `${name} widget`;
