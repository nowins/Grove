export class DoubleShiftGesture {
  reset() { this.firstRelease = null; this.down = this.clean = false; }
  press(shift, isolated, now) {
    if (!shift || !isolated) { this.reset(); return; }
    if (this.down) { this.clean = false; this.firstRelease = null; return; }
    this.down = this.clean = true; this.pressedAt = now;
    if (this.firstRelease != null && (now < this.firstRelease || now - this.firstRelease > 500)) this.firstRelease = null;
  }
  release(shift, isolated, now) {
    if (!shift || !isolated || !this.down || !this.clean || now < this.pressedAt || now - this.pressedAt > 300) { this.reset(); return false; }
    this.down = this.clean = false;
    if (this.firstRelease != null && now >= this.firstRelease && now - this.firstRelease <= 500) { this.reset(); return true; }
    this.firstRelease = now; return false;
  }
}
