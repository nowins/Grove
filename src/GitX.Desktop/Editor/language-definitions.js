// Shared lexical highlighters; no language server, diagnostics worker or external service.
import * as monaco from 'monaco-editor/editor/editor.api.js';
import 'monaco-editor/languages/definitions/csharp/register.js';
import 'monaco-editor/languages/definitions/java/register.js';
import 'monaco-editor/languages/definitions/javascript/register.js';
import 'monaco-editor/languages/definitions/typescript/register.js';
import 'monaco-editor/languages/definitions/xml/register.js';
import 'monaco-editor/languages/definitions/markdown/register.js';
import 'monaco-editor/languages/definitions/python/register.js';
import 'monaco-editor/languages/definitions/rust/register.js';
import 'monaco-editor/languages/definitions/powershell/register.js';
import 'monaco-editor/languages/definitions/yaml/register.js';
import 'monaco-editor/languages/definitions/css/register.js';
import 'monaco-editor/languages/definitions/html/register.js';

const ready = new Map();
export function prepareLanguage(language) {
  if (!ready.has(language)) {
    // Await the public asynchronous token factory before installing a model.
    const loading = monaco.editor.colorize('', language, {});
    ready.set(language, loading);
    loading.catch(() => ready.delete(language));
  }
  return ready.get(language);
}
