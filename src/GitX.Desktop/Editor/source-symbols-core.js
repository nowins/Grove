const kinds = {
  class_declaration: ['类', 'C'], interface_declaration: ['接口', 'I'], enum_declaration: ['枚举', 'E'],
  record_declaration: ['记录', 'R'], struct_declaration: ['结构体', 'S'], annotation_type_declaration: ['注解类型', 'A'],
  method_declaration: ['方法', 'm'], constructor_declaration: ['构造函数', 'c'], compact_constructor_declaration: ['构造函数', 'c'],
  destructor_declaration: ['析构函数', 'd'], local_function_statement: ['局部函数', 'f'],
  property_declaration: ['属性', 'p'], delegate_declaration: ['委托', 'D'], annotation_type_element_declaration: ['注解成员', 'm']
};
export { symbolLanguages } from './language-info.js';

const functions = new Set(['arrow_function', 'function_expression', 'generator_function', 'function_declaration', 'generator_function_declaration']);
const wrappers = new Set(['parenthesized_expression', 'as_expression', 'satisfies_expression', 'type_assertion', 'non_null_expression']);
const nameTypes = new Set(['identifier', 'type_identifier', 'property_identifier', 'private_property_identifier', 'string', 'nested_identifier']);
function unwrap(node) {
  for (let depth = 0; node && wrappers.has(node.type) && depth < 16; depth++) node = node.childForFieldName('expression') ?? node.namedChildren[0];
  return node;
}
function signature(node) {
  const generics = node?.childForFieldName('type_parameters') ?? node?.namedChildren.find(child => child.type === 'type_parameter_list');
  const parameters = node?.childForFieldName('parameters') ?? node?.childForFieldName('parameter') ??
    (node?.type === 'record_declaration' ? node.namedChildren.find(child => child.type === 'parameter_list') : null);
  const parameterText = parameters?.type === 'identifier' ? `(${parameters.text})` : parameters?.text ?? '';
  return (generics?.text ?? '') + parameterText;
}
function declaration(node, parents, grammar) {
  const name = node.childForFieldName('name');
  const basic = kinds[node.type];
  if (basic) return { name, kind: basic, suffix: signature(node) };
  if (grammar === 'python') {
    if (node.type === 'class_definition') return { name, kind: ['类', 'C'], suffix: node.childForFieldName('superclasses')?.text ?? '' };
    if (node.type === 'function_definition') {
      const method = parents.at(-1)?.kind === '类';
      const property = method && node.parent?.type === 'decorated_definition' && node.parent.namedChildren
        .some(child => child.type === 'decorator' && child.text.replace(/\s/g, '') === '@property');
      return { name, kind: property ? ['属性', 'p'] : method ? ['方法', 'm'] : ['函数', 'f'], suffix: signature(node) };
    }
    return null;
  }
  if (!['javascript', 'typescript', 'tsx'].includes(grammar)) return null;
  if (['function_declaration', 'generator_function_declaration', 'function_signature'].includes(node.type))
    return { name, kind: ['函数', 'f'], suffix: signature(node) };
  if (node.type === 'abstract_class_declaration') return { name, kind: ['类', 'C'], suffix: signature(node) };
  if (node.type === 'type_alias_declaration') return { name, kind: ['类型', 'T'], suffix: signature(node) };
  if (node.type === 'internal_module' || node.type === 'module') return { name, kind: ['命名空间', 'N'], suffix: '' };
  if (['method_definition', 'method_signature', 'abstract_method_signature'].includes(node.type)) {
    const property = node.children.some(child => child.type === 'get' || child.type === 'set');
    return { name, kind: property ? ['属性', 'p'] : name?.text === 'constructor' ? ['构造函数', 'c'] : ['方法', 'm'], suffix: signature(node) };
  }
  if (node.type === 'property_signature') return { name, kind: ['属性', 'p'], suffix: '' };
  let anchor, value, member = false;
  if (node.type === 'variable_declarator') { anchor = name; value = node.childForFieldName('value'); }
  else if (node.type === 'pair') { anchor = node.childForFieldName('key'); value = node.childForFieldName('value'); member = true; }
  else if (node.type === 'field_definition' || node.type === 'public_field_definition') {
    anchor = name ?? node.childForFieldName('property'); value = node.childForFieldName('value'); member = true;
    if (anchor && !functions.has(unwrap(value)?.type)) return { name: anchor, kind: ['字段', 'p'], suffix: '' };
  } else if (node.type === 'assignment_expression') {
    const left = node.childForFieldName('left');
    // CommonJS exports have a stable source name; arbitrary object mutations do not become declarations.
    if (!left || !/^(?:module\.exports|exports\.[\p{L}_$][\p{L}\p{N}_$]*)$/u.test(left.text)) return null;
    anchor = left; value = node.childForFieldName('right');
  } else if (node.type === 'export_statement') {
    value = node.childForFieldName('value') ?? node.childForFieldName('declaration');
    if (value?.childForFieldName('name')) return null;
    anchor = node.children.find(child => child.type === 'default');
  }
  if (!anchor) return null;
  value = unwrap(value);
  if (functions.has(value?.type)) return { name: anchor, kind: member ? ['方法', 'm'] : ['函数', 'f'], suffix: signature(value) };
  if (value?.type === 'class') return { name: anchor, kind: ['类', 'C'], suffix: signature(value) };
  if (value?.type === 'object') return { name: anchor, kind: ['对象', 'O'], suffix: '' };
  return null;
}

// Positions from the web binding are UTF-16 code units, matching Monaco. The
// tests use the actual WASM grammars, including non-ASCII text and CRLF.
export function collectSymbols(tree, { grammar = 'java', deadline = Infinity, now = () => performance.now(), maxSymbols = 5000, maxNodes = 200000 } = {}) {
  const symbols = [], pending = [{ node: tree.rootNode, parents: [] }];
  let visited = 0, truncated = false;
  while (pending.length) {
    if (++visited > maxNodes) { truncated = true; break; }
    if (visited % 128 === 0 && now() > deadline) throw new Error('结构解析耗时过长，请重试或查看较小的文件。');
    const { node, parents } = pending.pop();
    const descriptor = declaration(node, parents, grammar), nameNode = descriptor?.name, kind = descriptor?.kind;
    let nextParents = parents;
    if (nameNode && !nameNode.isMissing && nameNode.endIndex > nameNode.startIndex &&
        (nameTypes.has(nameNode.type) || nameNode.type === 'default' || nameNode.type === 'member_expression')) {
      if (symbols.length === maxSymbols) { truncated = true; break; }
      const name = nameNode.text;
      const label = (name + descriptor.suffix).replace(/\s+/g, ' ').slice(0, 240);
      const point = nameNode.startPosition;
      symbols.push({ name, label, kind: kind[0], glyph: kind[1], container: parents.map(parent => parent.name).join('.'), depth: parents.length,
        line: point.row + 1, column: point.column + 1, length: nameNode.endIndex - nameNode.startIndex });
      nextParents = [...parents, { name, kind: kind[0] }];
    }
    const children = node.namedChildren;
    for (let index = children.length - 1; index >= 0; index--) pending.push({ node: children[index], parents: nextParents });
  }
  return { symbols, truncated, hasErrors: tree.rootNode.hasError };
}

export function filterSymbols(symbols, query) {
  const parts = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  return parts.length ? symbols.filter(symbol => parts.every(part => `${symbol.label} ${symbol.container} ${symbol.kind}`.toLocaleLowerCase().includes(part))) : symbols;
}
