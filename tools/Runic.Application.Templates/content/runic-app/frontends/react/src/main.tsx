import React from "react";
import { createRoot } from "react-dom/client";
import { syncDocumentLocale } from "@runic-artifex/views";
import App from "./App";

// <html lang="en"> is the starter's language. Once the application sets the window's locale (IRunicWindowLocale),
// lang and dir follow it, and the Translations runtime that configureTranslatedText connects.
syncDocumentLocale();

createRoot(document.getElementById("app")!).render(
  <React.StrictMode><App /></React.StrictMode>
);
