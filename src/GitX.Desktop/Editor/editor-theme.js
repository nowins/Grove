// Shared dark code palette for browsing, diff and the three-way merge window.
export const editorTheme = {
  base: 'vs-dark', inherit: true, rules: [
    { token: 'keyword', foreground: 'cf8e6d' }, { token: 'string', foreground: '6aab73' },
    { token: 'number', foreground: '2aacb8' }, { token: 'comment', foreground: '7a7e85', fontStyle: '' }
  ],
  colors: {
    'editor.background': '#1e1f22', 'editor.foreground': '#bcbec4',
    'editorLineNumber.foreground': '#707782', 'editorLineNumber.activeForeground': '#a1a3ab',
    'editor.lineHighlightBackground': '#26282d', 'editor.selectionBackground': '#2e436e',
    'editorWidget.background': '#2b2d30', 'editorWidget.foreground': '#dfe1e5',
    'editorWidget.border': '#393b40', 'widget.border': '#393b40', 'focusBorder': '#3574f0',
    'input.background': '#1b1c1f', 'input.foreground': '#dfe1e5', 'input.border': '#393b40',
    'inputOption.activeBackground': '#2e436e', 'inputOption.activeBorder': '#3574f0'
  }
};
