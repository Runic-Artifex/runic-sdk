import "@fortawesome/fontawesome-free/css/all.min.css";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

function App() {
  return (
    <div className="container py-5">
      <div className="card border-0 shadow">
        <div className="card-body p-5">
          <span className="badge text-bg-primary mb-3">React + CsWebUi</span>
          <h1 className="display-5">WebUIToolkitStarter</h1>
          <p className="lead mb-0">
            Edit <code>Frontend/src/main.tsx</code> and save to exercise Vite HMR
            inside the native window.
          </p>
        </div>
      </div>
    </div>
  );
}

createRoot(document.querySelector("#app")!).render(
  <StrictMode><App /></StrictMode>,
);
