import { build } from 'esbuild';
import { mkdir, copyFile, writeFile } from 'node:fs/promises';
import { grammarNames } from './language-info.js';
await mkdir('dist', { recursive: true });
await build({
  entryPoints: { editor: 'editor.js', merge: 'merge.js', 'editor.worker': 'node_modules/monaco-editor/esm/vs/editor/editor.worker.js' },
  bundle: true, format: 'iife', outdir: 'dist', minify: true,
  loader: { '.ttf': 'file', '.svg': 'file' }, logLevel: 'info'
});
await copyFile('index.html', 'dist/index.html');
await build({ entryPoints: { 'source-symbols.worker': 'source-symbols.worker.js' }, bundle: true, format: 'esm', outdir: 'dist',
  external: ['./tree-sitter-runtime.js'], minify: true, logLevel: 'info' });
await copyFile('node_modules/web-tree-sitter/tree-sitter.js', 'dist/tree-sitter-runtime.js');
await copyFile('node_modules/web-tree-sitter/tree-sitter.wasm', 'dist/tree-sitter.wasm');
for (const grammar of grammarNames) await copyFile(`node_modules/@vscode/tree-sitter-wasm/wasm/tree-sitter-${grammar}.wasm`, `dist/tree-sitter-${grammar}.wasm`);
await copyFile('node_modules/web-tree-sitter/LICENSE', 'dist/TREE-SITTER-LICENSE.txt');
await copyFile('node_modules/@vscode/tree-sitter-wasm/LICENSE', 'dist/VSCODE-TREE-SITTER-LICENSE.txt');
await copyFile('tree-sitter-notices.txt', 'dist/TREE-SITTER-GRAMMAR-NOTICES.txt');
await copyFile('merge.html', 'dist/merge.html');
await copyFile('node_modules/monaco-editor/LICENSE', 'dist/MONACO-LICENSE.txt');
await copyFile('node_modules/monaco-editor/ThirdPartyNotices.txt', 'dist/ThirdPartyNotices.txt');
await copyFile('fonts/OFL.txt', 'dist/JETBRAINS-MONO-OFL.txt');
await copyFile('../Assets/Fonts/Inter/OFL.txt', 'dist/INTER-OFL.txt');
await copyFile('node_modules/marked/LICENSE', 'dist/MARKED-LICENSE.txt');
await copyFile('node_modules/dompurify/LICENSE', 'dist/DOMPURIFY-LICENSE.txt');
await writeFile('dist/.buildstamp', new Date().toISOString());
