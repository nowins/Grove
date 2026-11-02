// Join two native pairwise alignments on their shared result lines. No diff is
// calculated in the browser; null cells become view zones, never source text.
export function threeWayRows(left, right) {
  function projection(diff) {
    const before = new Map(), at = new Map(); let old = 0, result = 0;
    const oldCount = lineCount(diff.old.text), newCount = lineCount(diff.new.text);
    for (const block of [...diff.blocks, { oldStart: oldCount, newStart: newCount, oldCount: 0, newCount: 0, rows: [] }]) {
      while (result < block.newStart) at.set(++result, ++old);
      for (const row of block.rows) {
        if (row.newLine === null) {
          const key = result + 1;
          if (!before.has(key)) before.set(key, []);
          before.get(key).push(row.oldLine);
        } else { result = row.newLine; at.set(result, row.oldLine); }
        if (row.oldLine !== null) old = row.oldLine;
      }
      old = block.oldStart + block.oldCount; result = block.newStart + block.newCount;
    }
    return { before, at, count: newCount };
  }
  const a = projection(left), b = projection(right);
  if (a.count !== b.count) throw Error('合并布局的结果版本不一致');
  const rows = [];
  for (let line = 1; line <= a.count + 1; line++) {
    const l = a.before.get(line) ?? [], r = b.before.get(line) ?? [];
    for (let i = 0; i < Math.max(l.length, r.length); i++) rows.push([l[i] ?? null, null, r[i] ?? null]);
    if (line <= a.count) rows.push([a.at.get(line) ?? null, line, b.at.get(line) ?? null]);
  }
  return rows;
}
export function lineCount(text) { return text.length ? text.split('\n').length - (text.endsWith('\n') ? 1 : 0) : 0; }
export function sourceLines(rows, side, startLine, endLine, empty = false) {
  let at = rows.findIndex(row => row[1] === startLine);
  if (at < 0) at = rows.length;
  let first = at;
  while (first > 0 && rows[first - 1][1] === null) first--;
  const after = empty ? at : rows.findIndex(row => row[1] === endLine + 1);
  return rows.slice(first, after < 0 ? rows.length : after).map(row => row[side]).filter(line => line !== null);
}
export function alignmentZones(rows, counts) {
  const zones = [[], [], []], after = [0, 0, 0];
  rows.forEach(row => row.forEach((line, side) => {
    if (line !== null) after[side] = line;
    else {
      const previous = zones[side].at(-1);
      if (previous?.afterLineNumber === after[side]) previous.height++;
      else zones[side].push({ afterLineNumber: after[side], height: 1 });
    }
  }));
  const heights = counts.map((count, side) => count + zones[side].reduce((sum, zone) => sum + zone.height, 0));
  const maximum = Math.max(...heights);
  heights.forEach((height, side) => { if (height < maximum) zones[side].push({ afterLineNumber: counts[side], height: maximum - height }); });
  return zones;
}
