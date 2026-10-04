// Line conditionals of the .NET template engine, as used by runic-app:
// `#if (...)` in code and `<!--#if (...) -->` in markup.
export type TemplateSymbols = Readonly<Record<string, string>>;

const directive =
  /^\s*(?:#|<!--#)(if|elif|else|endif)\b\s*(?:\((.*)\))?\s*(?:-->)?\s*$/;

/** Applies `#if (...)` and `<!--#if (...) -->` line conditionals. */
export function renderTemplate(
  source: string,
  selection: TemplateSymbols,
): string {
  const output: string[] = [];
  // Each frame: whether the enclosing block emits, and whether a branch matched.
  const stack: { parent: boolean; taken: boolean; active: boolean }[] = [];
  const emitting = () => stack.at(-1)?.active ?? true;
  for (const line of source.split('\n')) {
    const match = directive.exec(line);
    if (!match) {
      if (emitting()) output.push(line);
      continue;
    }
    const [, keyword, condition] = match;
    if (keyword === 'if') {
      const parent = emitting();
      const active = parent && evaluate(condition ?? '', selection);
      stack.push({ parent, taken: active, active });
      continue;
    }
    const frame = stack.at(-1);
    if (!frame) throw new Error(`Unmatched #${keyword}`);
    if (keyword === 'endif') {
      stack.pop();
    } else if (keyword === 'elif') {
      frame.active =
        frame.parent && !frame.taken && evaluate(condition ?? '', selection);
      frame.taken ||= frame.active;
    } else {
      frame.active = frame.parent && !frame.taken;
      frame.taken = true;
    }
  }
  if (stack.length > 0) throw new Error('Unclosed #if');
  return output.join('\n');
}

/** Evaluates `==`, `!=`, `&&`, `||`, and parentheses over template symbols. */
export function evaluate(
  expression: string,
  selection: TemplateSymbols,
): boolean {
  const tokens =
    expression.match(/"[^"]*"|'[^']*'|==|!=|&&|\|\||[()]|[A-Za-z_]\w*/g) ?? [];
  let index = 0;
  const next = () => tokens[index++];
  const peek = () => tokens[index];

  function value(): string {
    const token = next();
    if (token === undefined)
      throw new Error(`Incomplete condition: ${expression}`);
    if (/^["']/.test(token)) return token.slice(1, -1);
    if (!(token in selection))
      throw new Error(`Unknown template symbol: ${token}`);
    return selection[token];
  }

  function comparison(): boolean {
    if (peek() === '(') {
      next();
      const result = or();
      if (next() !== ')')
        throw new Error(`Unbalanced condition: ${expression}`);
      return result;
    }
    const left = value();
    const operator = next();
    const right = value();
    if (operator === '==') return left === right;
    if (operator === '!=') return left !== right;
    throw new Error(`Unsupported operator ${operator} in ${expression}`);
  }

  function and(): boolean {
    let result = comparison();
    while (peek() === '&&') {
      next();
      result = comparison() && result;
    }
    return result;
  }

  function or(): boolean {
    let result = and();
    while (peek() === '||') {
      next();
      result = and() || result;
    }
    return result;
  }

  const result = or();
  if (index !== tokens.length)
    throw new Error(`Unexpected token in ${expression}`);
  return result;
}
