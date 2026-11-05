export function sideHeader(element, side, other) {
  const state = !side.exists ? '不存在' : side.text.length === 0 ? '空文件' : `${side.lineEnding} · ${side.endsWithNewline ? '末尾有换行' : '末尾无换行'}`;
  const label = document.getElementById(element);
  const access = document.createElement('span');
  access.className = 'side-access'; access.textContent = '只读 · ';
  const revision = document.createElement('span');
  revision.className = 'side-revision'; revision.textContent = side.label;
  label.classList.add('side-revision-label'); label.replaceChildren(access, revision);
  label.title = `只读 · ${side.label}\n${side.path}\n${state}`;
  label.setAttribute('aria-label', label.title);
  const path = document.getElementById(`${element}-path`);
  const split = Math.max(side.path.lastIndexOf('/'), side.path.lastIndexOf('\\')) + 1;
  const directory = document.createElement('span');
  directory.className = 'side-directory'; directory.textContent = side.path.slice(0, split);
  const filename = document.createElement('span');
  filename.className = 'side-filename'; filename.textContent = side.path.slice(split);
  path.replaceChildren(directory, filename); path.title = side.path;
  const format = document.getElementById(element === 'old-label' ? 'old-format' : 'new-format');
  format.textContent = !side.exists ? '不存在' : side.text.length === 0 ? '空文件' : side.lineEnding.startsWith('混合') ? '混合' : side.lineEnding;
  format.title = state;
  format.classList.toggle('attention', side.exists && (side.lineEnding.startsWith('混合') || other?.exists && other.text.length > 0 && side.text.length > 0 && side.lineEnding !== other.lineEnding));
}
