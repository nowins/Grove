// Copied as a separate ES module by build.mjs, so the official runtime retains
// its import.meta.url and only initializes inside this dedicated worker.
import { Parser, Language } from './tree-sitter-runtime.js';
import { collectSymbols } from './source-symbols-core.js';
import { structureGrammarFor } from './language-info.js';

let ready;
const languages = new Map();
self.onmessage = async ({ data }) => {
  const { id, language, path, text } = data;
  let parser, tree;
  try {
    const grammar = structureGrammarFor(language, path);
    if (!grammar || typeof text !== 'string' || text.length > 1024 * 1024) throw new Error('此文件暂不支持结构定位。');
    ready ??= Parser.init({ locateFile: () => new URL('tree-sitter.wasm', self.location.href).href });
    await ready;
    if (!languages.has(grammar)) languages.set(grammar, await Language.load(new URL(`tree-sitter-${grammar}.wasm`, self.location.href).href));
    parser = new Parser(); parser.setLanguage(languages.get(grammar));
    const deadline = performance.now() + 1500;
    tree = parser.parse(text, null, { progressCallback: () => performance.now() > deadline });
    if (!tree) throw new Error('结构解析耗时过长，请重试或查看较小的文件。');
    const result = collectSymbols(tree, { grammar, deadline });
    self.postMessage({ id, ...result });
  } catch (error) { self.postMessage({ id, error: error instanceof Error ? error.message : '结构解析失败，请重试。' }); }
  finally { tree?.delete(); parser?.delete(); }
};
