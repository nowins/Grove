export function initialChoice(block) {
  return { left: block.leftChanged === false ? 'none' : 'pending', right: block.rightChanged === false ? 'none' : 'pending' };
}
export const resolvedChoice = choice => choice.left !== 'pending' && choice.right !== 'pending';
export const canApplyAutomatically = (block, choice, candidate = block.baseText) => !block.isConflict && candidate === block.baseText && [choice.left, choice.right].every(s => s === 'pending' || s === 'none') && !resolvedChoice(choice);
function both(block) {
  if (block.leftChanged === false) return block.rightText;
  if (block.rightChanged === false) return block.leftText;
  if (block.leftText === block.rightText) return block.leftText;
  return block.leftText + (block.leftText && block.rightText && !block.leftText.endsWith('\n') ? '\n' : '') + block.rightText;
}
export function decide(block, current, action) {
  const choice = { ...current };
  let text;
  if (action === 'automatic') {
    if (!canApplyAutomatically(block, choice)) return null;
    text = block.automaticText;
    for (const side of ['left', 'right']) if (choice[side] !== 'none') choice[side] = 'accepted';
  } else if (action === 'keep') {
    for (const side of ['left', 'right']) if (choice[side] === 'pending') choice[side] = 'ignored';
  } else if (action === 'both' || action.startsWith('resolve-')) {
    const selected = action.slice(8);
    for (const side of ['left', 'right']) if (choice[side] !== 'none') choice[side] = action === 'both' || side === selected ? 'accepted' : 'ignored';
    text = action === 'both' ? both(block) : block[selected + 'Text'];
  } else {
    const ignore = action.startsWith('ignore-'); const side = ignore ? action.slice(7) : action;
    if (!['left', 'right'].includes(side) || choice[side] !== 'pending') return null;
    choice[side] = ignore ? 'ignored' : 'accepted';
    if (!ignore) text = choice[side === 'left' ? 'right' : 'left'] === 'accepted' ? both(block) : block[side + 'Text'];
  }
  return { choice, text };
}
