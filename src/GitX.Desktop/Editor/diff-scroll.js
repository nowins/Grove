// Source boundaries, not filler rows, connect the two continuous documents.
// Scroll anchors use the upper third of the viewport, as observed in IDEA.
export function sourceBoundary(pane, line, count, lineHeight, padding = 4) {
  if (!count) return padding;
  return line >= count ? pane.getTopForLineNumber(count) + lineHeight : pane.getTopForLineNumber(line + 1);
}

export class DiffScrollMap {
  constructor(plan, lineHeight, padding = 4) {
    Object.assign(this, { plan, lineHeight, padding });
    this.points = [[0, 0], ...plan.blocks.flatMap(block => [[block.oldStart, block.newStart],
      [block.oldStart + block.oldCount, block.newStart + block.newCount]]), [plan.oldLines.length, plan.newLines.length]];
  }
  invalidate() { this.geometry = null; }
  layout(panes) {
    this.geometry ??= this.points.map(point => point.map((line, side) => sourceBoundary(panes[side], line,
      side === 0 ? this.plan.oldLines.length : this.plan.newLines.length, this.lineHeight, this.padding)));
  }
  transfer(side, y) {
    const points = this.geometry, other = 1 - side;
    let lo = 0, hi = points.length - 1;
    while (lo < hi) { const mid = (lo + hi) >>> 1; if (points[mid][side] < y - .01) lo = mid + 1; else hi = mid; }
    const end = points[lo], start = points[Math.max(0, lo - 1)];
    if (y <= start[side]) return start[other];
    if (y >= end[side]) return end[other] + y - end[side];
    return Math.min(start[other] + y - start[side], end[other]);
  }
  target(panes, side, top, current) {
    this.layout(panes);
    const height = this.lineHeight, shift = panes[side].getLayoutInfo().height / 3;
    const anchor = top + shift;
    const row = Math.floor((anchor - this.padding) / height) * height + this.padding;
    const phase = anchor - row, mapped = this.transfer(side, row);
    let target = mapped - shift + phase;
    // A long insertion can map several rows to one boundary. Do not let its
    // fractional line phase pull the follower back and forth by a few pixels.
    if (Math.abs(target - current) < height &&
      (phase < height / 2 && target < current && this.transfer(side, row - height) === mapped ||
       phase > height / 2 && target > current && this.transfer(side, row + height) === mapped)) target = current;
    const follower = panes[1 - side];
    return Math.max(0, Math.min(target, Math.max(0, follower.getScrollHeight() - follower.getLayoutInfo().height)));
  }
}

// Preserve true endpoints even outside the viewport; the SVG clips the curve.
// Clamping each endpoint to the viewport would change its shape while scrolling.
export function connectorPath(width, oldTop, oldBottom, newTop, newBottom) {
  const a = width * .3, b = width * .7;
  if (oldBottom - oldTop < 1) oldTop = oldBottom - 1;
  if (newBottom - newTop < 1) newTop = newBottom - 1;
  return `M0 ${oldTop} C${a} ${oldTop} ${b} ${newTop} ${width} ${newTop} L${width} ${newBottom} C${b} ${newBottom} ${a} ${oldBottom} 0 ${oldBottom} Z`;
}
