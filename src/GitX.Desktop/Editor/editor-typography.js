import './typography.css';

// Read the same tokens used by headers, comments and code snippets. Both Monaco
// entry points use these options, so their font metrics cannot drift apart.
const style = getComputedStyle(document.documentElement);
export const editorTypography = {
  fontFamily: style.getPropertyValue('--code-font').trim(),
  fontSize: Number.parseFloat(style.getPropertyValue('--code-font-size')),
  lineHeight: Number.parseFloat(style.getPropertyValue('--code-line-height'))
};
