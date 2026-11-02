// Pure display mapping. All changed ranges and inline spans arrive from C# / Git.
export function sourceLines(text) {
  if (!text.length) return [];
  const lines = text.split('\n');
  if (text.endsWith('\n')) lines.pop();
  return lines.map(line => line.endsWith('\r') ? line.slice(0, -1) : line);
}

export function buildAlignment(diff) {
  const oldLines = sourceLines(diff.old.text);
  const newLines = sourceLines(diff.new.text);
  const oldZones = [], newZones = [];
  let oldPadding = 0, newPadding = 0;
  const blocks = diff.blocks.map((block, index) => {
    const visualStart = block.oldStart + oldPadding;
    if (visualStart !== block.newStart + newPadding) throw new Error('Diff alignment mismatch');
    const rows = block.rows ?? Array.from({length: Math.max(block.oldCount,block.newCount)}, (_,i) => ({
      oldLine: i < block.oldCount ? block.oldStart + i + 1 : null, newLine: i < block.newCount ? block.newStart + i + 1 : null
    }));
    let oldAfter = block.oldStart, newAfter = block.newStart;
    function gap(zones, afterLineNumber) {
      const last = zones.at(-1);
      if (last?.afterLineNumber === afterLineNumber && last.index === index) last.height++;
      else zones.push({ afterLineNumber, height: 1, index });
    }
    for (const row of rows) {
      if (row.oldLine !== null) oldAfter = row.oldLine;
      else { gap(oldZones, oldAfter); oldPadding++; }
      if (row.newLine !== null) newAfter = row.newLine;
      else { gap(newZones, newAfter); newPadding++; }
    }
    return { ...block, rows, visualStart, height: rows.length, index };
  });
  // Monaco's empty model still occupies one unnumbered row. Balance that row at EOF.
  const oldHeight = Math.max(1, oldLines.length) + oldPadding;
  const newHeight = Math.max(1, newLines.length) + newPadding;
  if (oldHeight < newHeight) oldZones.push({ afterLineNumber: oldLines.length, height: newHeight - oldHeight, index: -1 });
  if (newHeight < oldHeight) newZones.push({ afterLineNumber: newLines.length, height: oldHeight - newHeight, index: -1 });
  return { oldLines, newLines, oldZones, newZones, blocks };
}

export function visualRow(lineNumber, zones) {
  return lineNumber - 1 + zones.filter(z => z.afterLineNumber < lineNumber).reduce((sum, z) => sum + z.height, 0);
}

// Retain context at both ends, including file edges. Anchors are always real,
// unchanged lines, so Monaco's public folding provider can keep source models intact.
export function unchangedRegions(plan, context = 3, minimumHidden = 4) {
  if (!plan.blocks.length) return [];
  const regions = [];
  let oldEnd = 0, newEnd = 0;
  for (const block of [...plan.blocks, { oldStart: plan.oldLines.length, newStart: plan.newLines.length, oldCount: 0, newCount: 0 }]) {
    const length = block.oldStart - oldEnd;
    if (length !== block.newStart - newEnd) throw new Error('Unchanged region alignment mismatch');
    const count = length - 2 * context;
    if (count >= minimumHidden) regions.push({ id: regions.length, count,
      oldStart: oldEnd + context + 1, newStart: newEnd + context + 1 });
    oldEnd = block.oldStart + block.oldCount;
    newEnd = block.newStart + block.newCount;
  }
  return regions;
}

export function visibleAnchor(line, side, regions, collapsed) {
  for (const region of regions) {
    const start = side === 0 ? region.oldStart : region.newStart;
    if (collapsed.has(region.id) && line >= start && line < start + region.count) return start - 1;
  }
  return line;
}

export function nextChange(blocks, current, direction, cursorRow = null) {
  if (!blocks.length) return -1;
  if (cursorRow !== null) {
    if (direction > 0) {
      const found = blocks.findIndex(block => block.visualStart > cursorRow);
      return found < 0 ? 0 : found;
    }
    for (let i = blocks.length - 1; i >= 0; i--) if (blocks[i].visualStart < cursorRow) return i;
    return blocks.length - 1;
  }
  if (current < 0) return direction > 0 ? 0 : blocks.length - 1;
  return (current + direction + blocks.length) % blocks.length;
}
