import definitions from '../../GitX.Core/SourceLanguages.json' with { type: 'json' };

const extensions = new Map(definitions.flatMap(definition => definition.extensions.map(extension => [extension, definition])));
const definitionForPath = path => {
  const name = String(path ?? '').split(/[\\/]/).at(-1);
  return extensions.get(name.slice(name.lastIndexOf('.')).toLowerCase());
};
export const languageForPath = path => definitionForPath(path)?.language ?? 'plaintext';
export const grammarNames = [...new Set(definitions.map(d => d.grammar).filter(Boolean))];
export const symbolLanguages = Object.fromEntries(definitions.filter(d => d.grammar).map(d => [d.language, d.language === 'typescript' ? 'typescript' : d.grammar]));
export function structureGrammarFor(language, path) {
  const definition = definitionForPath(path);
  return definition?.language === language ? definition.grammar : symbolLanguages[language];
}
