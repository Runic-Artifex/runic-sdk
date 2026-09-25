// Compile-only fixture for the plain ESM contract emitted after the Debug
// post-MVVM bootstrap. It is not a runtime connector or application sample.
import {
  decodeRouteReply,
  encodeRouteRequest,
  fingerprint,
  routeIds,
  routeName,
  runtime,
  type TitleReply,
  type ViewModelCommands,
  type ViewModelState,
} from "../obj/runic-post-mvvm-discovery/Debug/net10.0/view-bridge.contract.js";

declare const state: ViewModelState;
declare const commands: ViewModelCommands;

const title: string = state.title;
const save: Promise<void> = commands.saveCommand();
const refresh: Promise<string> = commands.refreshCommand("again");
const identity: string = fingerprint;
const titleReadRoute: string = routeName("content1", routeIds.titleRead);
const titleReadRequest = encodeRouteRequest(routeIds.titleRead, { documentEpoch: "document", presentationId: "presentation" });
const titleReadReply: TitleReply = decodeRouteReply(routeIds.titleRead, { ok: true, title: "Draft" });
const mappedModel: string = runtime.routes.find(route => route.id === routeIds.titleRead)!.model;
void [title, save, refresh, identity, titleReadRoute, titleReadRequest, titleReadReply, mappedModel];

// @ts-expect-error Compiled Toolkit state is read-only on the snapshot side.
state.title = "front-end mutation";
// @ts-expect-error The compiled ReactiveUI command requires a string argument.
commands.refreshCommand(42);
// @ts-expect-error The compiled Toolkit Save command takes no frontend argument.
commands.saveCommand("unexpected");
// @ts-expect-error The generated wire request rejects fields outside its exact schema.
encodeRouteRequest(routeIds.titleRead, { documentEpoch: "document", presentationId: "presentation", extra: "unexpected" });
