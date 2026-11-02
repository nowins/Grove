import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { Parser, Language } from 'web-tree-sitter';
import { collectSymbols, filterSymbols } from './source-symbols-core.js';

await Parser.init({ locateFile: () => fileURLToPath(new URL('./node_modules/web-tree-sitter/tree-sitter.wasm', import.meta.url)) });
const languages = new Map();
async function parse(language, source, options) {
  if (!languages.has(language)) languages.set(language, await Language.load(fileURLToPath(new URL(`./node_modules/@vscode/tree-sitter-wasm/wasm/tree-sitter-${language}.wasm`, import.meta.url))));
  const parser = new Parser(); parser.setLanguage(languages.get(language));
  const tree = parser.parse(source);
  try { return collectSymbols(tree, { grammar: language, ...options }); } finally { tree.delete(); parser.delete(); }
}

test('Java multiline generics, constructors, overloads, nested interfaces and records use declarations', async () => {
  const source = `class Demo<T> {
    Demo() {}
    @Deprecated public <U> String work(
      int value, U item) { return "fake() {}"; }
    public void work(String value) {}
    // void imaginary() {}
    record Item(int value) { Item { } }
    interface Inner { void run(); }
  }`;
  const result = await parse('java', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(symbol => symbol.name), ['Demo', 'Demo', 'work', 'work', 'Item', 'Item', 'Inner', 'run']);
  assert.equal(result.symbols[2].label, 'work<U>( int value, U item)');
  assert.equal(result.symbols.at(-1).container, 'Demo.Inner');
  assert.equal(result.symbols[4].label, 'Item(int value)');
});

test('Java enum bodies and annotation members remain navigable', async () => {
  const result = await parse('java', 'enum State { ON; void run() {} } @interface Mark { String value(); }');
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(symbol => symbol.name), ['State', 'run', 'Mark', 'value']);
});

test('C# generic types, constructors, properties, local functions and delegates retain their containers', async () => {
  const source = `namespace Example;
    record Item(int Value);
    class Demo<T> {
      public Demo() {}
      public int Value { get; set; }
      public void Work(int value) { void Local() {} }
      public void Work(string value) {}
      public delegate int Map(int value);
      ~Demo() {}
      string text = "void Fake() {}";
    }`;
  const result = await parse('c-sharp', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(symbol => symbol.name), ['Item', 'Demo', 'Demo', 'Value', 'Work', 'Local', 'Work', 'Map', 'Demo']);
  assert.equal(result.symbols[0].label, 'Item(int Value)');
  assert.equal(result.symbols[1].label, 'Demo<T>');
  assert.equal(result.symbols[5].container, 'Demo.Work');
});

for (const language of ['java', 'c-sharp']) test(`${language}: CRLF and non-BMP text produce Monaco UTF-16 coordinates`, async () => {
  const source = 'class 示例 {\r\n  /* 😀 */ public void 处理订单() {}\r\n}';
  const result = await parse(language, source), method = result.symbols.find(symbol => symbol.name === '处理订单');
  assert.ok(method);
  const line = source.split('\n')[method.line - 1];
  assert.equal(method.line, 2);
  assert.equal(line.slice(method.column - 1, method.column - 1 + method.length), method.name);
  assert.equal(method.column, line.indexOf('处理订单') + 1);
});

test('Malformed code explicitly reports incomplete syntax while retaining valid declarations', async () => {
  const result = await parse('java', 'class Broken { void Valid() {} void unfinished( }');
  assert.equal(result.hasErrors, true);
  assert.ok(result.symbols.some(symbol => symbol.name === 'Valid'));
});

test('Symbol and traversal budgets report truncation without retaining an unbounded result', async () => {
  const source = 'class Demo { void One() {} void Two() {} void Three() {} }';
  const result = await parse('java', source, { maxSymbols: 2 });
  assert.equal(result.symbols.length, 2); assert.equal(result.truncated, true);
  const nodes = await parse('java', source, { maxNodes: 1 }); assert.equal(nodes.truncated, true); assert.equal(nodes.symbols.length, 0);
});

test('Extraction deadlines stop pathological syntax traversal', async () => {
  const source = 'class Demo {' + Array.from({ length: 80 }, (_, i) => `void M${i}() {}`).join(' ') + '}';
  await assert.rejects(parse('java', source, { deadline: -1 }), /耗时过长/);
});

test('Filtering distinguishes overloads and containers without searching method bodies', async () => {
  const { symbols } = await parse('java', 'class Demo { void Work(int x) {} void Work(String x) {} class Inner { void Work() {} } }');
  assert.equal(filterSymbols(symbols, 'work string').length, 1);
  assert.equal(filterSymbols(symbols, 'inner work')[0].container, 'Demo.Inner');
  assert.equal(filterSymbols(symbols, '不存在').length, 0);
});

function verifyPositions(source, symbols) {
  for (const symbol of symbols) {
    const line = source.split('\n')[symbol.line - 1];
    assert.equal(line.slice(symbol.column - 1, symbol.column - 1 + symbol.length), symbol.name, `${symbol.name} @ ${symbol.line}:${symbol.column}`);
  }
}

test('Python decorators, async methods, properties and nested definitions retain their real containers', async () => {
  const source = `@dataclass
class Service(Base):
    @property
    def value(self) -> int: return 1
    async def load(self, item: str, /, *, force=False):
        def nested(x): return x
        return nested(item)
def outer():
    class Inner:
        def run(self): pass
text = "def Fake(): pass"
# class NotADeclaration: pass
`;
  const result = await parse('python', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['Service', 'value', 'load', 'nested', 'outer', 'Inner', 'run']);
  assert.equal(result.symbols[0].label, 'Service(Base)');
  assert.equal(result.symbols[1].kind, '属性');
  assert.equal(result.symbols[2].label, 'load(self, item: str, /, *, force=False)');
  assert.equal(result.symbols[3].container, 'Service.load');
  assert.equal(result.symbols.at(-1).container, 'outer.Inner');
  verifyPositions(source, result.symbols);
});

test('Python stubs and overloads remain separate navigable signatures', async () => {
  const source = `from typing import overload
class Client:
    @overload
    def send(self, value: str) -> str: ...
    @overload
    def send(self, value: bytes) -> bytes: ...
    async def close(self) -> None: ...
`;
  const result = await parse('python', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['Client', 'send', 'send', 'close']);
  assert.equal(filterSymbols(result.symbols, 'send bytes')[0].container, 'Client');
  verifyPositions(source, result.symbols);
});

test('JavaScript functions, object methods, class fields and assigned functions do not duplicate internal expression names', async () => {
  const source = `export async function load(x) {}
const double = x => x * 2;
const named = function internal(y) { function nested() {} };
const api = { run() {}, save: async (item) => item, nested: { read: function(x) {} } };
export class View {
  #value = 1;
  constructor() {}
  async render(x) {}
  onClick = (event) => event;
  get title() { return 'x'; }
}
const text = "function Fake() {}";
`;
  const result = await parse('javascript', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['load', 'double', 'named', 'nested', 'api', 'run', 'save', 'nested', 'read', 'View', '#value', 'constructor', 'render', 'onClick', 'title']);
  assert.equal(result.symbols[1].label, 'double(x)');
  assert.equal(result.symbols[3].container, 'named');
  assert.equal(result.symbols[8].container, 'api.nested');
  assert.equal(result.symbols[13].container, 'View');
  assert.equal(result.symbols.at(-1).kind, '属性');
  verifyPositions(source, result.symbols);
});

test('CommonJS exports and anonymous default JSX exports have real source anchors', async () => {
  const source = `module.exports = { work() {} };
exports.save = (x) => x;
export default () => <div title="function Fake() {}" />;
`;
  const result = await parse('javascript', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['module.exports', 'work', 'exports.save', 'default']);
  assert.equal(result.symbols[1].container, 'module.exports');
  assert.equal(result.symbols.at(-1).label, 'default()');
  verifyPositions(source, result.symbols);
});

test('TypeScript namespace, interfaces, type aliases, enums, abstract methods and overloads retain nesting and generics', async () => {
  const source = `export namespace Api {
  export interface Result<T> { value: T; load(input: string): Promise<T> }
  export type Id<T> = T | string;
  export enum State { Ready, Done }
}
export abstract class Service<T> {
  abstract load(input: T): void;
  public run = (x: T): T => x;
  get value(): T { return null!; }
}
export function build(x: string): string;
export function build(x: number): number;
export function build(x: any): any { return x; }
export const identity = <T>(x: T): T => x;
`;
  const result = await parse('typescript', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['Api', 'Result', 'value', 'load', 'Id', 'State', 'Service', 'load', 'run', 'value', 'build', 'build', 'build', 'identity']);
  assert.equal(result.symbols[3].container, 'Api.Result');
  assert.equal(result.symbols[4].label, 'Id<T>');
  assert.equal(result.symbols.at(-1).label, 'identity<T>(x: T)');
  assert.equal(filterSymbols(result.symbols, 'build number').length, 1);
  verifyPositions(source, result.symbols);
});

test('TypeScript wrapped callable values and anonymous default classes use assigned names', async () => {
  const source = `const run = ((x: number) => x) satisfies Handler;
const Factory = class Named { create() {} };
export default class { render() {} }
`;
  const result = await parse('typescript', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['run', 'Factory', 'create', 'default', 'render']);
  assert.equal(result.symbols[2].container, 'Factory');
  assert.equal(result.symbols.at(-1).container, 'default');
  verifyPositions(source, result.symbols);
});

test('TSX components, nested callbacks and JSX syntax are parsed with the TSX grammar', async () => {
  const source = `export interface Props { label: string; onClick?(): void }
export const Card = ({ label }: Props) => <section>{label}</section>;
export default function Page() {
  const onClick = () => {};
  return <Card label="function Fake() {}" />;
}
`;
  const result = await parse('tsx', source);
  assert.equal(result.hasErrors, false);
  assert.deepEqual(result.symbols.map(s => s.name), ['Props', 'label', 'onClick', 'Card', 'Page', 'onClick']);
  assert.equal(result.symbols.at(-1).container, 'Page');
  verifyPositions(source, result.symbols);
});

for (const [language, source] of [
  ['python', '# 😀\r\nclass 示例:\r\n    def 处理订单(self): pass\r\n'],
  ['javascript', '/* 😀 */ const 处理订单 = x => x;\r\n'],
  ['typescript', '/* 😀 */ export function 处理订单(x: string): string { return x; }\r\n'],
  ['tsx', '/* 😀 */ export const 处理订单 = () => <span>中文</span>;\r\n']
]) test(`${language}: new language declarations use UTF-16 source positions with CRLF and emoji`, async () => {
  const result = await parse(language, source);
  assert.equal(result.hasErrors, false);
  assert.ok(result.symbols.some(s => s.name === '处理订单'));
  verifyPositions(source, result.symbols);
});

for (const [language, source] of [
  ['python', 'def valid(): pass\ndef unfinished(\n'],
  ['javascript', 'function valid() {} function unfinished( {'],
  ['typescript', 'interface Valid {} function unfinished( {'],
  ['tsx', 'const Valid = () => <div/>; function unfinished( {']
]) test(`${language}: malformed code retains valid structure and reports syntax errors`, async () => {
  const result = await parse(language, source);
  assert.equal(result.hasErrors, true);
  assert.ok(result.symbols.some(s => s.name.toLowerCase() === 'valid'));
});

test('New language grammars obey the same extraction budgets as existing languages', async () => {
  for (const language of ['python', 'javascript', 'typescript', 'tsx']) {
    const source = language === 'python' ? 'def first(): pass\ndef second(): pass\n' : 'function first() {} function second() {}';
    const result = await parse(language, source, { maxSymbols: 1 });
    assert.equal(result.symbols.length, 1); assert.equal(result.truncated, true);
    const nodes = await parse(language, source, { maxNodes: 1 });
    assert.equal(nodes.truncated, true);
  }
});
