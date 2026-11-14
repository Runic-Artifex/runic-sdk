// Runic Inspect before the first click (#976): which event handlers of a frontend file reach which generated-client
// call, found in the file's source. The development server analyzes a file when Runic Inspect asks for it
// (`/__runic/handlers?file=…`); served modules are not changed, and the build never runs it.
//
// Per file, the analysis takes each event binding of the markup (JSX `onClick={…}`, Svelte `onclick={…}` and
// `on:click={…}`, Vue `@click="…"`, Angular `(click)="…"`) and follows its expression through the file: identifiers
// to their declarations (variables, functions, destructuring), `this.x` and template names to class members and
// object methods, into the callbacks of Runic's helpers (`useCommand(() => client.save())`, `injectCommand(…)`, …).
// Every `x.member(…)` call it reaches is a candidate; `x.command("member")` too. Runic Inspect keeps the candidates
// that are commands of a ViewModel in the Bridge source map. Names imported from other files are reported, not
// followed: their commands are learned from the first click, as before.

import { readFileSync, statSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, extname, isAbsolute, join, relative, resolve } from "node:path";
import type { IncomingMessage, ServerResponse } from "node:http";
import { pathToFileURL } from "node:url";
import { mayRead as mayReadRequest } from "./requests.js";
import type { AccessList } from "./access.js";
import type { parseSync } from "vite";

/** A position in a file: 1-based line and column. */
export interface RunicHandlerPosition {
  readonly file: string;
  readonly line: number;
  readonly column: number;
}

/** A generated-client call a handler reaches: `client.save()` is `save`. */
export interface RunicHandlerCall extends RunicHandlerPosition {
  readonly member: string;
  /**
   * `direct`: in the handler's expression; `helper`: in the callback of a Runic helper (`useCommand`, `injectCommand`,
   * …) the handler uses; `local`: through another declaration of the file.
   */
  readonly via: "direct" | "helper" | "local";
}

/** An event binding of the markup and what it reaches. */
export interface RunicHandlerBinding extends RunicHandlerPosition {
  /** The element or component tag: `button`, `MyButton`. */
  readonly tag: string;
  /** The event, lower case without `on`: `click`. */
  readonly event: string;
  /** The handler's source text, shortened. */
  readonly expression: string;
  readonly calls: readonly RunicHandlerCall[];
  /** Names the handler uses that are imported from another file; their calls are not followed. */
  readonly external: readonly { readonly name: string; readonly source: string }[];
}

export interface RunicHandlerAnalysis {
  readonly format: "runic-handlers/1";
  readonly file: string;
  readonly framework: "react" | "vue" | "svelte" | "angular" | "unknown";
  readonly bindings: readonly RunicHandlerBinding[];
  /** Why a file has no analysis: its framework's compiler was not found. */
  readonly note?: string;
}

// -------------------------------------------------------------------------------------------------------------------
// ESTree, as oxc (Vite's parser) and Svelte's parser give it.

interface Node {
  readonly type: string;
  readonly start: number;
  readonly end: number;
  readonly [key: string]: unknown;
}

function isNode(value: unknown): value is Node {
  return typeof value === "object" && value !== null && typeof (value as { type?: unknown }).type === "string";
}

const skippedKeys = new Set(["type", "start", "end", "loc", "range", "typeAnnotation", "typeArguments", "typeParameters",
  "returnType", "decorators", "superTypeArguments", "implements", "metadata"]);

function children(node: Node): Node[] {
  const result: Node[] = [];
  for (const key in node) {
    if (skippedKeys.has(key)) continue;
    const value = node[key];
    if (Array.isArray(value)) { for (const item of value) if (isNode(item)) result.push(item); }
    else if (isNode(value)) result.push(value);
  }
  return result;
}

/** Runic helpers whose result's methods (`run`, `cancel`, `commit`, `client`) are not commands. */
const helperNames = new Set([
  "useCommand", "useOperation", "useLatestOperation", "useField", "useTextField", "useForm", "useView", "useViewState",
  "useSuspenseView", "useText", "useQueryCollection", "useCollectionViewport", "useMenuBar",
  "injectCommand", "injectOperation", "injectLatestOperation", "injectField", "injectTextField", "injectForm",
  "injectView", "injectViewResource", "injectText", "injectQueryCollection", "injectCollectionViewport", "injectMenuBar",
  "createCommandController", "createOperationController", "createLatestOperationController", "createFieldController",
]);

/** Helpers whose arguments are the callbacks that call the client; the others' arguments are not followed. */
const callbackHelpers = new Set(["useCommand", "useOperation", "injectCommand", "injectOperation", "createCommandController",
  "createOperationController"]);

/** Packages whose imports are not application code in another file. */
function isLibrary(source: string): boolean {
  return !source.startsWith(".") && !source.startsWith("/") && !source.startsWith("$lib") && !source.startsWith("~")
    && !source.startsWith("@/") && !/^[a-z]:[\\/]/iu.test(source);
}

/** The declarations of a script, by name. Scopes are not separated: the analysis is per file. */
class Declarations {
  readonly bindings = new Map<string, Node[]>();
  readonly members = new Map<string, Node[]>();
  readonly imports = new Map<string, string>();

  add(program: Node): void {
    const visit = (node: Node): void => {
      switch (node.type) {
        case "ImportDeclaration": {
          const source = (node["source"] as Node | undefined)?.["value"];
          for (const specifier of (node["specifiers"] as Node[] | undefined) ?? []) {
            const local = (specifier["local"] as Node | undefined)?.["name"];
            if (typeof local === "string" && typeof source === "string") this.imports.set(local, source);
          }
          return;
        }
        case "VariableDeclarator": {
          const init = node["init"];
          if (isNode(init)) for (const name of patternNames(node["id"])) push(this.bindings, name, init);
          break;
        }
        case "FunctionDeclaration": {
          const name = (node["id"] as Node | undefined)?.["name"];
          if (typeof name === "string") push(this.bindings, name, node);
          break;
        }
        case "PropertyDefinition": case "MethodDefinition": case "Property": case "AccessorProperty": {
          const name = propertyName(node);
          const value = node["value"];
          if (name !== undefined && isNode(value)
            && (node.type !== "Property" || isFunction(value) || node["shorthand"] === true)) push(this.members, name, value);
          break;
        }
      }
      for (const child of children(node)) visit(child);
    };
    visit(program);
  }
}

function push(map: Map<string, Node[]>, name: string, node: Node): void {
  const list = map.get(name);
  if (list === undefined) map.set(name, [node]);
  else if (!list.includes(node)) list.push(node);
}

function isFunction(node: Node): boolean {
  return node.type === "ArrowFunctionExpression" || node.type === "FunctionExpression" || node.type === "FunctionDeclaration";
}

function propertyName(node: Node): string | undefined {
  const key = node["key"] as Node | undefined;
  if (key === undefined) return undefined;
  if (node["computed"] === true) return typeof key["value"] === "string" ? key["value"] : undefined;
  if (key.type === "Identifier" || key.type === "PrivateIdentifier") return key["name"] as string;
  return typeof key["value"] === "string" ? key["value"] : undefined;
}

function patternNames(pattern: unknown): string[] {
  if (!isNode(pattern)) return [];
  switch (pattern.type) {
    case "Identifier": return [pattern["name"] as string];
    case "ObjectPattern": return ((pattern["properties"] as Node[] | undefined) ?? []).flatMap((property) =>
      property.type === "RestElement" ? patternNames(property["argument"]) : patternNames(property["value"]));
    case "ArrayPattern": return ((pattern["elements"] as (Node | null)[] | undefined) ?? []).flatMap((element) => patternNames(element));
    case "AssignmentPattern": return patternNames(pattern["left"]);
    case "RestElement": return patternNames(pattern["argument"]);
    default: return [];
  }
}

/** Strips `(…)`, `!`, `as T`, `?.` wrappers. */
function unwrap(node: Node): Node {
  let current = node;
  for (;;) {
    if (current.type === "ParenthesizedExpression" || current.type === "ChainExpression" || current.type === "TSNonNullExpression"
      || current.type === "TSAsExpression" || current.type === "TSSatisfiesExpression" || current.type === "TSTypeAssertion") {
      current = current["expression"] as Node;
    } else {
      return current;
    }
  }
}

function calleeName(node: Node): string | undefined {
  const callee = unwrap(node["callee"] as Node);
  if (callee.type === "Identifier") return callee["name"] as string;
  if (callee.type === "MemberExpression" && callee["computed"] !== true) return (callee["property"] as Node)["name"] as string;
  return undefined;
}

/** Where offsets of the parsed nodes are in the file, and how to turn them into lines and columns. */
class SourceText {
  private readonly lineStarts: number[] = [0];
  constructor(readonly file: string, readonly text: string) {
    for (let index = 0; index < text.length; index++) if (text.charCodeAt(index) === 10) this.lineStarts.push(index + 1);
  }
  position(offset: number): { line: number; column: number } {
    let low = 0, high = this.lineStarts.length - 1;
    while (low < high) {
      const middle = (low + high + 1) >> 1;
      if (this.lineStarts[middle]! <= offset) low = middle; else high = middle - 1;
    }
    return { line: low + 1, column: offset - this.lineStarts[low]! + 1 };
  }
}

const maximumDepth = 12;

/**
 * Follows a handler expression through the declarations and returns the calls it reaches. `offset` moves the
 * handler's node positions into `source` (a template expression parsed on its own).
 */
function reach(
  handler: Node, declarations: Declarations, source: SourceText, offset: number, scriptSource: SourceText, scriptOffset: number,
): { calls: RunicHandlerCall[]; external: { name: string; source: string }[] } {
  const calls = new Map<string, RunicHandlerCall>();
  const external = new Map<string, string>();
  const followed = new Set<Node>();

  const helperResult = (object: Node): boolean => {
    const target = unwrap(object);
    let definitions: Node[] | undefined;
    if (target.type === "Identifier") definitions = declarations.bindings.get(target["name"] as string) ?? declarations.members.get(target["name"] as string);
    else if (target.type === "MemberExpression" && unwrap(target["object"] as Node).type === "ThisExpression" && target["computed"] !== true) {
      definitions = declarations.members.get((target["property"] as Node)["name"] as string);
    }
    return definitions !== undefined && definitions.length > 0 && definitions.every((definition) => {
      const value = unwrap(definition);
      return value.type === "CallExpression" && helperNames.has(calleeName(value) ?? "");
    });
  };

  const record = (member: string, node: Node, inScript: boolean, via: RunicHandlerCall["via"]): void => {
    if (calls.has(member)) return;
    const text = inScript ? scriptSource : source;
    const position = text.position(node.start + (inScript ? scriptOffset : offset));
    calls.set(member, { member, file: text.file, line: position.line, column: position.column, via });
  };

  const follow = (definitions: readonly Node[] | undefined, depth: number, helper: boolean): void => {
    for (const definition of definitions ?? []) {
      if (followed.has(definition)) continue;
      followed.add(definition);
      visit(definition, depth + 1, true, helper);
    }
  };

  const visit = (node: Node, depth: number, inScript: boolean, helper: boolean): void => {
    if (depth > maximumDepth) return;
    const via = (): RunicHandlerCall["via"] => depth === 0 ? "direct" : helper ? "helper" : "local";
    switch (node.type) {
      case "CallExpression": case "NewExpression": {
        const callee = unwrap(node["callee"] as Node);
        const name = calleeName(node);
        const insideHelper = helper || (name !== undefined && helperNames.has(name));
        // useView(page), useField(view, Fields.step): their arguments reach no command of the handler.
        if (name !== undefined && helperNames.has(name) && !callbackHelpers.has(name)) return;
        if (callee.type === "MemberExpression") {
          const property = callee["property"] as Node;
          const member = callee["computed"] === true ? (typeof property["value"] === "string" ? property["value"] : undefined)
            : property["name"] as string;
          const argument = (node["arguments"] as Node[] | undefined)?.[0];
          if (member === "command" && argument !== undefined && argument.type === "Literal" && typeof argument["value"] === "string") {
            record(argument["value"], argument, inScript, via());
          } else if (member !== undefined && node.type === "CallExpression" && !helperResult(callee["object"] as Node)) {
            record(member, property, inScript, via());
          }
        }
        for (const child of children(node)) visit(child, depth, inScript, insideHelper);
        return;
      }
      case "Identifier": {
        const name = node["name"] as string;
        const bindings = declarations.bindings.get(name);
        if (bindings !== undefined) return follow(bindings, depth, helper || bindings.some((binding) => isHelperCall(binding)));
        const members = declarations.members.get(name);
        if (members !== undefined) return follow(members, depth, helper || members.some((member) => isHelperCall(member)));
        const imported = declarations.imports.get(name);
        if (imported !== undefined && !isLibrary(imported) && !helperNames.has(name)) external.set(name, imported);
        return;
      }
      case "MemberExpression": {
        const object = unwrap(node["object"] as Node);
        if (object.type === "ThisExpression" && node["computed"] !== true) {
          const members = declarations.members.get((node["property"] as Node)["name"] as string);
          return follow(members, depth, helper || (members ?? []).some((member) => isHelperCall(member)));
        }
        // A method of an imported object is a candidate itself; the object is not code to follow.
        if (!(object.type === "Identifier" && declarations.imports.has(object["name"] as string))) {
          visit(node["object"] as Node, depth, inScript, helper);
        }
        if (node["computed"] === true) visit(node["property"] as Node, depth, inScript, helper);
        return;
      }
      case "Property": {
        if (node["computed"] === true) visit(node["key"] as Node, depth, inScript, helper);
        visit(node["value"] as Node, depth, inScript, helper);
        return;
      }
      case "PropertyDefinition": case "MethodDefinition": {
        if (isNode(node["value"])) visit(node["value"], depth, inScript, helper);
        return;
      }
      case "VariableDeclarator": {
        if (isNode(node["init"])) visit(node["init"], depth, inScript, helper);
        return;
      }
      case "FunctionDeclaration": case "FunctionExpression": case "ArrowFunctionExpression": {
        if (isNode(node["body"])) visit(node["body"], depth, inScript, helper);
        return;
      }
      case "LabeledStatement": case "BreakStatement": case "ContinueStatement": {
        if (isNode(node["body"])) visit(node["body"], depth, inScript, helper);
        return;
      }
      case "ImportExpression": case "TSTypeAliasDeclaration": case "TSInterfaceDeclaration": return;
    }
    for (const child of children(node)) visit(child, depth, inScript, helper);
  };

  visit(handler, 0, false, false);
  return { calls: [...calls.values()], external: [...external].map(([name, from]) => ({ name, source: from })) };
}

function isHelperCall(node: Node): boolean {
  const value = unwrap(node);
  return value.type === "CallExpression" && helperNames.has(calleeName(value) ?? "");
}

let parser: typeof parseSync | undefined;

/**
 * Loads the parser, Vite's `parseSync` (oxc). Imported on first use, so that `runicDevSessionProxy()` under a server
 * without Vite plugins loads without it. Returns whether it is available.
 */
export async function loadHandlerParser(): Promise<boolean> {
  if (parser !== undefined) return true;
  try { parser = (await import("vite")).parseSync; }
  catch { return false; }
  return typeof parser === "function";
}

/** The program, or undefined when it does not parse (`strict`: with any error). */
function parseProgram(file: string, code: string, lang: "js" | "jsx" | "ts" | "tsx", strict = false): Node | undefined {
  if (parser === undefined) return undefined;
  try {
    const result = parser(file, code, { lang, sourceType: "module" });
    if (result.errors.length > 0 && (strict || result.program.body.length === 0)) return undefined;
    return result.program as unknown as Node;
  } catch {
    return undefined;
  }
}

/** A template handler (`increment()`, `step.commit`, `a(); b()`), parsed on its own. */
function parseHandler(code: string, lang: "js" | "ts"): Node | undefined {
  const program = parseProgram(`handler.${lang}`, code, lang, true);
  if (program === undefined) return undefined;
  const body = program["body"] as Node[];
  return body.length === 1 && body[0]!.type === "ExpressionStatement" ? body[0]!["expression"] as Node : program;
}

function shorten(text: string): string {
  const collapsed = text.replace(/\s+/gu, " ").trim();
  return collapsed.length > 160 ? `${collapsed.slice(0, 159)}…` : collapsed;
}

function binding(
  tag: string, event: string, element: { line: number; column: number; file: string }, expression: string,
  reached: ReturnType<typeof reach>,
): RunicHandlerBinding {
  return { file: element.file, line: element.line, column: element.column, tag, event: event.toLowerCase(),
    expression: shorten(expression), calls: reached.calls, external: reached.external };
}

function scriptLang(file: string, lang: string | undefined): "js" | "jsx" | "ts" | "tsx" {
  const value = (lang ?? extname(file).slice(1)).toLowerCase();
  if (value === "tsx") return "tsx";
  if (value === "ts" || value === "mts" || value === "cts" || value === "typescript") return "ts";
  if (value === "jsx") return "jsx";
  return "js";
}

// React and JSX -----------------------------------------------------------------------------------------------------

function jsxName(node: Node): string {
  switch (node.type) {
    case "JSXIdentifier": return node["name"] as string;
    case "JSXMemberExpression": return `${jsxName(node["object"] as Node)}.${jsxName(node["property"] as Node)}`;
    case "JSXNamespacedName": return `${jsxName(node["namespace"] as Node)}:${jsxName(node["name"] as Node)}`;
    default: return "";
  }
}

/** The JSX event bindings of a script (`onClick={…}`), at the opening element's `<`. */
export function analyzeJsx(file: string, code: string): RunicHandlerBinding[] {
  const lang = scriptLang(file, undefined);
  // Plain .js and .ts may hold JSX too (Vite's React plugin accepts it in .js); a type assertion `<T>x` in .ts does not.
  const program = parseProgram(file, code, lang === "js" ? "jsx" : lang === "ts" ? "tsx" : lang) ?? (lang === "ts" ? parseProgram(file, code, "ts") : undefined);
  if (program === undefined) return [];
  const declarations = new Declarations();
  declarations.add(program);
  const source = new SourceText(file, code);
  const bindings: RunicHandlerBinding[] = [];
  const visit = (node: Node): void => {
    if (node.type === "JSXOpeningElement") {
      const tag = jsxName(node["name"] as Node);
      for (const attribute of (node["attributes"] as Node[] | undefined) ?? []) {
        if (attribute.type !== "JSXAttribute") continue;
        const name = jsxName(attribute["name"] as Node);
        const value = attribute["value"] as Node | null;
        if (!/^on[A-Z]/u.test(name) || value?.type !== "JSXExpressionContainer") continue;
        const expression = value["expression"] as Node;
        if (expression.type === "JSXEmptyExpression") continue;
        const position = source.position(node.start);
        bindings.push(binding(tag, name.slice(2).replace(/Capture$/u, ""), { file, ...position },
          code.slice(expression.start, expression.end), reach(expression, declarations, source, 0, source, 0)));
      }
    }
    for (const child of children(node)) visit(child);
  };
  visit(program);
  return bindings;
}

// Angular -----------------------------------------------------------------------------------------------------------

/** The `(event)="…"` and `on-event="…"` bindings of an Angular template, with their element's tag and position. */
export function angularTemplateBindings(template: string): { tag: string; event: string; expression: string; offset: number; expressionOffset: number }[] {
  const found: { tag: string; event: string; expression: string; offset: number; expressionOffset: number }[] = [];
  const tags = /<!--[\s\S]*?-->|<([a-zA-Z][\w.:-]*)/gu;
  for (let match = tags.exec(template); match !== null; match = tags.exec(template)) {
    if (match[1] === undefined) continue;
    const tag = match[1];
    const offset = match.index;
    // The attributes up to the tag's `>`, honoring quotes.
    let index = tags.lastIndex;
    let quote: string | undefined;
    for (; index < template.length; index++) {
      const character = template[index]!;
      if (quote !== undefined) { if (character === quote) quote = undefined; }
      else if (character === "\"" || character === "'") quote = character;
      else if (character === ">") break;
    }
    const attributes = template.slice(tags.lastIndex, index);
    const attribute = /(?:\(([\w.:-]+)\)|\bon-([\w.:-]+))\s*=\s*(?:"([^"]*)"|'([^']*)')/gu;
    for (let item = attribute.exec(attributes); item !== null; item = attribute.exec(attributes)) {
      const expression = item[3] ?? item[4] ?? "";
      const event = (item[1] ?? item[2] ?? "").split(".", 1)[0]!;
      const expressionOffset = tags.lastIndex + item.index + item[0].length - expression.length - 1;
      found.push({ tag, event, expression, offset, expressionOffset });
    }
    tags.lastIndex = index;
  }
  return found;
}

function stringValue(node: Node | undefined): { text: string; offset: number } | undefined {
  if (node === undefined) return undefined;
  if (node.type === "Literal" && typeof node["value"] === "string") return { text: node["value"], offset: node.start + 1 };
  if (node.type === "TemplateLiteral" && (node["expressions"] as unknown[]).length === 0) {
    const quasi = (node["quasis"] as Node[])[0]!;
    const value = quasi["value"] as { cooked?: string; raw: string };
    // After the backtick; parsers differ in where the template element starts.
    return { text: value.raw, offset: node.start + 1 };
  }
  return undefined;
}

/**
 * The template bindings of the Angular components of a script: `@Component({ template })` or `templateUrl`, which
 * `readTemplate` reads (relative to the script).
 */
export function analyzeAngular(file: string, code: string, readTemplate: (path: string) => string | undefined): RunicHandlerBinding[] {
  const program = parseProgram(file, code, "ts");
  if (program === undefined) return [];
  const bindings: RunicHandlerBinding[] = [];
  const scriptSource = new SourceText(file, code);
  const visit = (node: Node): void => {
    if (node.type === "ClassDeclaration" || node.type === "ClassExpression") {
      const decorator = ((node["decorators"] as Node[] | undefined) ?? []).map((item) => unwrap(item["expression"] as Node))
        .find((expression) => expression.type === "CallExpression" && calleeName(expression) === "Component");
      const metadata = decorator === undefined ? undefined : (decorator["arguments"] as Node[])[0];
      if (metadata?.type === "ObjectExpression") {
        const properties = new Map(((metadata["properties"] as Node[]) ?? []).flatMap((property) => {
          const name = property.type === "Property" ? propertyName(property) : undefined;
          return name === undefined ? [] : [[name, property["value"] as Node] as const];
        }));
        let template: { text: string; offset: number; source: SourceText } | undefined;
        const inline = stringValue(properties.get("template"));
        if (inline !== undefined) template = { ...inline, source: scriptSource };
        const url = stringValue(properties.get("templateUrl"))?.text;
        if (template === undefined && url !== undefined) {
          const path = resolve(dirname(file), url);
          const text = readTemplate(path);
          if (text !== undefined) template = { text, offset: 0, source: new SourceText(path, text) };
        }
        if (template !== undefined) {
          const declarations = new Declarations();
          declarations.add(node["body"] as Node);
          const fileDeclarations = new Declarations();
          fileDeclarations.add(program);
          for (const [name, from] of fileDeclarations.imports) declarations.imports.set(name, from);
          for (const [name, list] of fileDeclarations.bindings) if (!declarations.bindings.has(name)) declarations.bindings.set(name, list);
          for (const entry of angularTemplateBindings(template.text)) {
            const handler = parseHandler(entry.expression, "ts");
            const position = template.source.position(template.offset + entry.offset);
            const reached = handler === undefined ? { calls: [], external: [] }
              : reach(handler, declarations, template.source, template.offset + entry.expressionOffset, scriptSource, 0);
            bindings.push(binding(entry.tag, entry.event, { file: template.source.file, ...position }, entry.expression, reached));
          }
        }
      }
    }
    for (const child of children(node)) visit(child);
  };
  visit(program);
  return bindings;
}

// Svelte ------------------------------------------------------------------------------------------------------------

interface SvelteCompiler { parse(source: string, options: { modern: true; filename?: string }): Record<string, unknown> }

/** The event bindings of a Svelte 5 component (`onclick={…}`, `on:click={…}`), at the element's `<`. */
export function analyzeSvelte(file: string, code: string, compiler: SvelteCompiler): RunicHandlerBinding[] {
  let ast: Record<string, unknown>;
  try { ast = compiler.parse(code, { modern: true, filename: file }); }
  catch { return []; }
  const declarations = new Declarations();
  for (const key of ["module", "instance"]) {
    const script = ast[key] as { content?: Node } | null | undefined;
    if (isNode(script?.content)) declarations.add(script.content);
  }
  const source = new SourceText(file, code);
  const bindings: RunicHandlerBinding[] = [];
  const visit = (value: unknown): void => {
    if (Array.isArray(value)) { for (const item of value) visit(item); return; }
    if (typeof value !== "object" || value === null) return;
    const node = value as Record<string, unknown>;
    if (Array.isArray(node["attributes"]) && typeof node["name"] === "string" && typeof node["start"] === "number") {
      for (const attribute of node["attributes"] as Record<string, unknown>[]) {
        let event: string | undefined;
        let expression: Node | undefined;
        if (attribute["type"] === "OnDirective" && isNode(attribute["expression"])) {
          event = attribute["name"] as string;
          expression = attribute["expression"];
        } else if (attribute["type"] === "Attribute" && typeof attribute["name"] === "string" && /^on[a-z]/u.test(attribute["name"])) {
          const raw = attribute["value"];
          const tag = (Array.isArray(raw) ? raw.length === 1 ? raw[0] : undefined : raw) as Record<string, unknown> | undefined;
          if (tag?.["type"] === "ExpressionTag" && isNode(tag["expression"])) {
            event = attribute["name"].slice(2);
            expression = tag["expression"];
          }
        }
        if (event === undefined || expression === undefined) continue;
        const position = source.position(node["start"] as number);
        bindings.push(binding(node["name"] as string, event, { file, ...position }, code.slice(expression.start, expression.end),
          reach(expression, declarations, source, 0, source, 0)));
      }
    }
    for (const [key, child] of Object.entries(node)) {
      if (key === "instance" || key === "module" || key === "css" || key === "expression" || key === "attributes") continue;
      if (typeof child === "object" && child !== null) visit(child);
    }
  };
  visit(ast["fragment"]);
  return bindings;
}

// Vue ---------------------------------------------------------------------------------------------------------------

interface VueBlock { readonly content: string; readonly lang?: string; readonly loc: { readonly start: { readonly offset: number } } }
interface VueTemplateNode {
  readonly type: number;
  readonly tag?: string;
  readonly loc: { readonly start: { readonly offset: number } };
  readonly props?: readonly {
    readonly type: number; readonly name: string;
    readonly arg?: { readonly content?: string; readonly isStatic?: boolean };
    readonly exp?: { readonly content: string; readonly loc: { readonly start: { readonly offset: number } } };
  }[];
  readonly children?: readonly VueTemplateNode[];
  readonly branches?: readonly VueTemplateNode[];
}
interface VueCompiler {
  parse(source: string, options: { filename: string; sourceMap: false }): {
    descriptor: { template: { ast?: VueTemplateNode; lang?: string } | null; script: VueBlock | null; scriptSetup: VueBlock | null };
  };
}

/** The `v-on` bindings of a Vue single-file component, at the element's `<`. */
export function analyzeVue(file: string, code: string, compiler: VueCompiler): RunicHandlerBinding[] {
  let descriptor: ReturnType<VueCompiler["parse"]>["descriptor"];
  try { descriptor = compiler.parse(code, { filename: file, sourceMap: false }).descriptor; }
  catch { return []; }
  const source = new SourceText(file, code);
  const declarations = new Declarations();
  let lang: "js" | "ts" = "js";
  // The script blocks parsed in place: a copy of the file with everything else blanked keeps the offsets.
  const blocks = [descriptor.script, descriptor.scriptSetup].filter((block): block is VueBlock => block !== null);
  for (const block of blocks) {
    const blockLang = scriptLang(file, block.lang ?? "js");
    if (blockLang === "ts" || blockLang === "tsx") lang = "ts";
    const start = block.loc.start.offset;
    const padded = code.slice(0, start).replace(/[^\n]/gu, " ") + block.content;
    const program = parseProgram(`${file}.${blockLang}`, padded, blockLang);
    if (program !== undefined) declarations.add(program);
  }
  const bindings: RunicHandlerBinding[] = [];
  const visit = (node: VueTemplateNode): void => {
    if (node.type === 1 && node.tag !== undefined) {
      for (const prop of node.props ?? []) {
        if (prop.type !== 7 || prop.name !== "on" || prop.arg?.content === undefined || prop.arg.isStatic === false || prop.exp === undefined) continue;
        const handler = parseHandler(prop.exp.content, lang);
        const position = source.position(node.loc.start.offset);
        const reached = handler === undefined ? { calls: [], external: [] }
          : reach(handler, declarations, source, prop.exp.loc.start.offset, source, 0);
        bindings.push(binding(node.tag, prop.arg.content, { file, ...position }, prop.exp.content, reached));
      }
    }
    for (const child of node.children ?? []) visit(child);
    for (const branch of node.branches ?? []) visit(branch);
  };
  if (descriptor.template?.ast !== undefined) visit(descriptor.template.ast);
  return bindings;
}

// Files -------------------------------------------------------------------------------------------------------------

const maximumFileBytes = 2 * 1024 * 1024;
const analyzedExtensions = new Set([".js", ".jsx", ".mjs", ".ts", ".tsx", ".mts", ".vue", ".svelte"]);

/** Whether a file may be analyzed: a frontend source file below `root`, outside `node_modules`. */
export function mayAnalyze(file: string, root: string): boolean {
  return isAbsolute(file) && !file.includes("\0") && analyzedExtensions.has(extname(file).toLowerCase()) && mayRead(file, root);
}

function readText(path: string): string | undefined {
  try {
    const stat = statSync(path);
    return stat.isFile() && stat.size <= maximumFileBytes ? readFileSync(path, "utf8") : undefined;
  } catch {
    return undefined;
  }
}

async function loadCompiler<T>(specifier: string, from: readonly string[]): Promise<T | undefined> {
  for (const directory of from) {
    try {
      const path = createRequire(join(directory, "package.json")).resolve(specifier);
      return await import(pathToFileURL(path).href) as T;
    } catch { /* next */ }
  }
  return undefined;
}

/**
 * Analyzes files on request, each again only when it (or an Angular component's template) changed. The Svelte and Vue
 * compilers are the application's own (`svelte/compiler`, `vue/compiler-sfc`), loaded from `root`.
 */
export function createHandlerAnalyzer(root: () => string): (file: string) => Promise<RunicHandlerAnalysis | undefined> {
  const cache = new Map<string, { key: string; templates: string[]; analysis: RunicHandlerAnalysis }>();
  const compilers = new Map<string, Promise<unknown>>();
  const compiler = <T>(specifier: string, file: string): Promise<T | undefined> => {
    let pending = compilers.get(specifier);
    if (pending === undefined) compilers.set(specifier, pending = loadCompiler<T>(specifier, [root(), dirname(file)]));
    return pending as Promise<T | undefined>;
  };
  const stamp = (path: string): string => {
    try { const stat = statSync(path); return `${stat.mtimeMs}:${stat.size}`; } catch { return "missing"; }
  };
  return async (file) => {
    const base = root();
    if (!mayAnalyze(file, base)) return undefined;
    if (!await loadHandlerParser()) {
      return { format: "runic-handlers/1", file, framework: "unknown", bindings: [], note: "vite (its parser) was not found." };
    }
    const cached = cache.get(file);
    if (cached !== undefined && cached.key === [file, ...cached.templates].map(stamp).join("|")) return cached.analysis;
    const code = readText(file);
    if (code === undefined) return undefined;
    const templates: string[] = [];
    const key = stamp(file);
    const extension = extname(file).toLowerCase();
    const result = (framework: RunicHandlerAnalysis["framework"], bindings: RunicHandlerBinding[], note?: string): RunicHandlerAnalysis =>
      ({ format: "runic-handlers/1", file, framework, bindings, ...(note === undefined ? {} : { note }) });
    let analysis: RunicHandlerAnalysis;
    if (extension === ".svelte") {
      const svelte = await compiler<SvelteCompiler>("svelte/compiler", file);
      analysis = svelte === undefined ? result("svelte", [], "svelte/compiler was not found.") : result("svelte", analyzeSvelte(file, code, svelte));
    } else if (extension === ".vue") {
      const vue = await compiler<VueCompiler>("vue/compiler-sfc", file);
      analysis = vue === undefined ? result("vue", [], "vue/compiler-sfc was not found.") : result("vue", analyzeVue(file, code, vue));
    } else if (/@Component\s*\(/u.test(code)) {
      const readTemplate = (path: string): string | undefined => {
        if (!mayRead(path, base) || !/\.html?$/iu.test(path)) return undefined;
        templates.push(path);
        return readText(path);
      };
      analysis = result("angular", analyzeAngular(file, code, readTemplate));
    } else {
      analysis = result("react", analyzeJsx(file, code));
    }
    cache.set(file, { key: [key, ...templates.map(stamp)].join("|"), templates, analysis });
    if (cache.size > 300) cache.delete(cache.keys().next().value!);
    return analysis;
  };
}

function mayRead(path: string, root: string): boolean {
  const relativePath = relative(root, path);
  return relativePath !== "" && !relativePath.startsWith("..") && !isAbsolute(relativePath)
    && !relativePath.split(/[\\/]/u).includes("node_modules");
}

/**
 * `file` as the analyzer takes it: an absolute path below `root` as it is, a `/@fs/` URL path, a root-relative URL
 * path (`/src/App.tsx`), or a path relative to `root` (`src/app/counter.ts`, as Angular's debug info names it).
 */
export function handlerFile(value: string, root: string): string {
  const path = value.startsWith("/@fs/") ? value.slice(4) : value;
  if (!isAbsolute(path)) return resolve(root, path);
  if (mayRead(path, root) || !path.startsWith("/")) return path;
  return resolve(root, path.slice(1));
}

function answerJson(response: ServerResponse, status: number, body?: unknown): void {
  if (response.writableEnded) return;
  response.statusCode = status;
  response.setHeader("Cache-Control", "no-store");
  if (body === undefined) {
    response.end();
    return;
  }
  response.setHeader("Content-Type", "application/json; charset=utf-8");
  response.end(JSON.stringify(body));
}

/**
 * Answers `GET /__runic/handlers?file=…` with the file's analysis: for a page of this server on this machine or a
 * client the allow list admits (access.ts), and a
 * frontend source file below the root (not `node_modules`); 404 otherwise.
 */
export async function answerHandlers(
  request: IncomingMessage, response: ServerResponse, analyze: (file: string) => Promise<RunicHandlerAnalysis | undefined>,
  root: string, access?: AccessList,
): Promise<void> {
  request.resume();
  if (request.method !== "GET" && request.method !== "HEAD") {
    response.setHeader("Allow", "GET, HEAD");
    return answerJson(response, 405);
  }
  if (!(await mayReadRequest(request, access))) return answerJson(response, 403);
  let file: string | null;
  try { file = new URL(request.url ?? "", "http://localhost").searchParams.get("file"); }
  catch { file = null; }
  if (file === null || file.length === 0 || file.length > 4096) return answerJson(response, 400);
  const analysis = await analyze(handlerFile(file, root));
  if (analysis === undefined) return answerJson(response, 404);
  answerJson(response, 200, analysis);
}
