import Cocoa
import WebKit

// Native WKWebView return-value regression, not a visual desktop acceptance
// test. Run on each macOS CI architecture against the actual shared JS module.
@MainActor
final class Probe: NSObject, WKNavigationDelegate {
    var view: WKWebView!
    var done = false
    var failed = false
    var index = 0
    var records: [[String: Any]] = []
    let output: String
    let tests: [(String, String, String)] = [
        ("document Promise", "window.groveNative.command(() => fakeApi.load())", "ok"),
        ("comment refresh Promise", "window.groveNative.command(() => fakeApi.comments())", "ok"),
        ("merge layout Promise", "window.groveNative.command(() => fakeApi.layout())", "ok"),
        ("DOM focus result", "window.groveNative.command(() => document.body)", "ok"),
        ("undefined command", "window.groveNative.command(() => undefined)", "ok"),
        ("view state JSON", "window.groveNative.json(() => fakeApi.capture())", "json"),
        ("rejected Promise dispatch", "window.groveNative.command(() => Promise.reject(new Error('expected async failure')))", "ok"),
        ("rejected Promise report", "JSON.stringify(reports)", "error")
    ]
    init(source: String, output: String) {
        self.output = output
        super.init()
        view = WKWebView(frame: NSRect(x: 0, y: 0, width: 640, height: 480))
        view.navigationDelegate = self
        let classic = source.replacingOccurrences(of: "export function installNativeBridge", with: "function installNativeBridge")
        let fixture = """
        window.reports = [];
        window.fakeApi = {
          load: async () => ({ unsupported: () => {} }),
          comments: async () => undefined,
          layout: async () => ({ layout: true }),
          capture: () => ({ version: 8, path: '目录/a.ts', views: [{ scrollTop: 120 }], side: 1 })
        };
        installNativeBridge(window, () => error => reports.push(String(error)));
        """
        view.loadHTMLString("<html><body><script>\(classic)\n\(fixture)</script></body></html>", baseURL: nil)
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // Reproduce the old call on the actual OS. New WebKit versions may
        // evolve, so record whether they reject it; every fixed call must pass.
        view.evaluateJavaScript("fakeApi.load()") { value, error in
            self.records.append(["name": "unwrapped Promise baseline", "unsupportedTypeObserved": (error as NSError?)?.code == 5, "errorCode": (error as NSError?)?.code ?? 0])
            self.next()
        }
    }
    func next() {
        if index == tests.count { finish(); return }
        let item = tests[index]; index += 1
        view.evaluateJavaScript(item.1) { value, error in
            var passed = error == nil
            let text = value as? String
            if item.2 == "ok" { passed = passed && text == "ok" }
            else if item.2 == "json" {
                if let text = text, let data = text.data(using: .utf8), let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
                    passed = passed && object["version"] as? Int == 8 && object["path"] as? String == "目录/a.ts"
                } else { passed = false }
            } else {
                passed = passed && (text?.contains("expected async failure") ?? false)
            }
            self.records.append(["name": item.0, "passed": passed, "nativeResultIsString": text != nil, "errorCode": (error as NSError?)?.code ?? 0])
            self.failed = self.failed || !passed
            self.next()
        }
    }
    func finish() {
        let report: [String: Any] = ["nativeEngine": "WKWebView", "nativeVisualGuiAcceptance": false, "passed": !failed, "cases": records]
        do { let data = try JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]); try data.write(to: URL(fileURLWithPath: output)); print(String(data: data, encoding: .utf8)!) }
        catch { failed = true; print("Cannot write native probe report") }
        done = true
    }
}

@main
struct Main {
    @MainActor static func main() {
        guard CommandLine.arguments.count == 3,
              let source = try? String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8) else { exit(2) }
        _ = NSApplication.shared
        NSApplication.shared.setActivationPolicy(.prohibited)
        let probe = Probe(source: source, output: CommandLine.arguments[2])
        let deadline = Date().addingTimeInterval(30)
        while !probe.done && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.02)) }
        if !probe.done { print("WKWebView native probe timed out"); exit(3) }
        exit(probe.failed ? 1 : 0)
    }
}
