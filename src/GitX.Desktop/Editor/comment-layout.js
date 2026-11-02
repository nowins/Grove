import { visualRow } from './diff-layout.js';

export function selectedLines(selection) {
  return { start: selection.startLineNumber, end: selection.endLineNumber -
    (selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber ? 1 : 0) };
}

// Insert a paired view zone after the selected SOURCE line. If the opposite
// side is an alignment gap, split that gap, keeping its rows and ordering.
export function commentZones(plan, side, line, height) {
  const boundary = visualRow(line, side === 0 ? plan.oldZones : plan.newZones) + 1;
  return [plan.oldZones, plan.newZones].map(zones => {
    const result = []; let padding = 0, inserted = false;
    const addComment = after => { result.push({ afterLineNumber: after, height, comment: true }); inserted = true; };
    for (const zone of zones) {
      const start = zone.afterLineNumber + padding, end = start + zone.height;
      if (!inserted && boundary < start) addComment(boundary - padding);
      if (!inserted && boundary >= start && boundary <= end) {
        if (boundary > start) result.push({ ...zone, height: boundary - start });
        addComment(zone.afterLineNumber);
        if (boundary < end) result.push({ ...zone, height: end - boundary });
      } else result.push(zone);
      padding += zone.height;
    }
    if (!inserted) addComment(boundary - padding);
    return result.map((zone, index) => ({ ...zone, ordinal: 100 + index }));
  });
}
