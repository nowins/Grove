export const defaultBlameView = { revision: true, date: false, author: true, commitNumber: false };
export const blameFields = ['revision', 'date', 'author', 'commitNumber'];
export function blameFieldText(info, field) {
  if (field === 'revision') return info.sha.slice(0, 8);
  if (field === 'date') return info.when.replace(/-/g, '/');
  if (field === 'commitNumber') return info.commitNumber ? String(info.commitNumber) : '';
  return info.author;
}
