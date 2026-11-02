import test from 'node:test';
import assert from 'node:assert/strict';
import { languageForPath, structureGrammarFor, grammarNames } from './language-info.js';

test('Shared path detection covers Python, CommonJS/ESM and JSX/TSX without depending on case or directory separators', () => {
  for (const extension of ['py', 'pyw', 'pyi']) assert.equal(languageForPath(`src/example.${extension.toUpperCase()}`), 'python');
  for (const extension of ['js', 'mjs', 'cjs', 'jsx']) assert.equal(languageForPath(`C:\\src\\example.${extension.toUpperCase()}`), 'javascript');
  for (const extension of ['ts', 'mts', 'cts', 'tsx', 'd.ts', 'd.mts', 'd.cts']) assert.equal(languageForPath(`src/example.${extension}`), 'typescript');
  assert.equal(languageForPath('folder.py/README'), 'plaintext');
  assert.equal(languageForPath('unknown.txt'), 'plaintext');
  assert.equal(languageForPath('View.axaml'), 'xml');
});

test('Structure selects actual JS/JSX, TS or TSX grammar while keeping Monaco language IDs stable', () => {
  assert.equal(structureGrammarFor('javascript', 'View.JSX'), 'javascript');
  assert.equal(structureGrammarFor('javascript', 'server.cjs'), 'javascript');
  assert.equal(structureGrammarFor('typescript', 'View.TSX'), 'tsx');
  assert.equal(structureGrammarFor('typescript', 'types.d.mts'), 'typescript');
  assert.equal(structureGrammarFor('python', 'types.PYI'), 'python');
  assert.equal(structureGrammarFor('plaintext', 'notes.txt'), undefined);
  assert.deepEqual(new Set(grammarNames), new Set(['java', 'c-sharp', 'javascript', 'typescript', 'tsx', 'python']));
});
