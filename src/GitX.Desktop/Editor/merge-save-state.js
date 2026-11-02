// The generated candidate needs saving, but merely opening it creates no edits
// to lose. Resolution decisions are edits even when the text stays unchanged.
export class MergeSaveState {
  constructor(text, states, existingResult) {
    this.closeBaseline = { text, states };
    this.saved = existingResult ? this.closeBaseline : null;
  }
  changed(baseline, text, states) { return !baseline || baseline.text !== text || baseline.states !== states; }
  dirty(text, states) { return this.changed(this.saved, text, states); }
  edited(text, states) { return this.changed(this.closeBaseline, text, states); }
  markSaved(text, states) { this.saved = this.closeBaseline = { text, states }; }
}
