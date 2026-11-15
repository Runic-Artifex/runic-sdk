// Vitest compiles the components for the tests just in time (the Analog plugin), so the compiler loads first.
import "@angular/compiler";
import { getTestBed } from "@angular/core/testing";
import { BrowserTestingModule, platformBrowserTesting } from "@angular/platform-browser/testing";
import { afterEach } from "vitest";

getTestBed().initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
// Each test starts with a new testing module, as `ng test` does.
afterEach(() => getTestBed().resetTestingModule());
