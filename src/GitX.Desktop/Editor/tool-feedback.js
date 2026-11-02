// Transient toolbar feedback. A superseded document must never finish a newer read.
export class ToolFeedback {
  constructor(element) { this.element = element; this.version = null; this.timer = null; }
  clear() { clearTimeout(this.timer); this.timer = null; this.element.hidden = true; }
  begin(version) {
    this.clear(); this.version = version;
    this.timer = setTimeout(() => { this.element.textContent = '正在读取…'; this.element.hidden = false; }, 150);
  }
  end(version, message = '') {
    if (version !== this.version) return;
    this.clear(); this.version = null;
    if (message) this.show(message);
  }
  show(message) {
    this.clear(); this.element.textContent = message; this.element.hidden = false;
    this.timer = setTimeout(() => this.clear(), 2000);
  }
}
