// Native WebView APIs differ in their treatment of Promise, undefined and
// objects. The host boundary returns strings only; work/errors keep using
// the existing versioned message channel. No eval or synchronous waiting.
export function installNativeBridge(target, captureErrorReporter) {
  target.groveNative = Object.freeze({
    command(action) {
      const result = action(); // Keep synchronous exceptions visible to host.
      const report = captureErrorReporter(); // action may advance document version synchronously.
      if (result && typeof result.then === 'function') {
        Promise.resolve(result).catch(error => {
          try { report(error); } catch { /* Do not create a second rejection. */ }
        });
      }
      return 'ok';
    },
    json(action) {
      const result = action();
      if (result && typeof result.then === 'function') throw new TypeError('Native JSON queries must be synchronous.');
      return JSON.stringify(result) ?? 'null';
    }
  });
  return target.groveNative;
}
