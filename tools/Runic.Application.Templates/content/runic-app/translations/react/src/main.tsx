import React from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { startTranslations } from "./translations";

// The page shows the application's language, from Frontend/translations; until the window's locale arrives, the
// browser's. <html lang dir> follow it.
startTranslations();

createRoot(document.getElementById("app")!).render(
  <React.StrictMode><App /></React.StrictMode>
);
