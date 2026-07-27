import { Component, provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";

@Component({
  selector: "webuitoolkit-root",
  standalone: true,
  template: `
    <div class="container py-5">
      <div class="card border-0 shadow">
        <div class="card-body p-5">
          <span class="badge text-bg-danger mb-3">Angular + CsWebUi</span>
          <h1 class="display-5">WebUIToolkitStarter</h1>
          <p class="lead mb-0">
            Edit <code>Frontend/src/main.ts</code> and save to exercise the
            Angular development builder inside the native app workflow.
          </p>
        </div>
      </div>
    </div>
  `,
})
class App {}

await bootstrapApplication(App, {
  providers: [provideZonelessChangeDetection()],
});
