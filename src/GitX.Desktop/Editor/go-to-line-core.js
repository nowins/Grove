export function parseLineInput(value, lineCount) {
  if (!Number.isSafeInteger(lineCount) || lineCount < 1) return { error: '当前文档暂不可定位。' };
  const text = String(value ?? '').trim();
  if (!text) return { error: '请输入行号。' };
  if (!/^[0-9]+$/.test(text)) return { error: '请输入正整数行号。' };
  const line = Number(text);
  if (!Number.isSafeInteger(line) || line < 1 || line > lineCount) return { error: `行号范围是 1–${lineCount}。` };
  return { line };
}
export function goToLineShortcut(event, mac) {
  if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat || event.shiftKey || event.altKey) return false;
  const key = event.key.toLowerCase();
  return key === 'g' && event.ctrlKey && !event.metaKey || mac && key === 'l' && event.metaKey && !event.ctrlKey;
}
export function sameLineContext(captured, current) {
  return !!captured?.available && !!current?.available && captured.version === current.version && captured.model === current.model && captured.path === current.path && captured.modelVersion === current.modelVersion;
}
