# Linux-first joint candidate

## Preview 6 release preparation (2026-11-06)

Main `2e3bf00ac2996536e0b5b40060d933acdd7dc351` includes the merged lifecycle fix (PR #1), this joint integration (PR #2), and the subsequent macOS titlebar fix (PR #3). The exclusions below describe the original candidate; the merged titlebar fix remains included in Preview 6. No unmerged performance, Worktree-list or UI development is imported. The lifecycle regressions do not measure overall memory gains. Exact version-commit CI and release manifests supersede candidate build results for distribution; native GUI acceptance limits remain.

Original candidate base: `205678ea396c131d11ca5f08780d97bd6f847431`. The sections below retain the original candidate validation record.

## Main integration verification (2026-11-05)

Prepared against main `80d024d4e683e71b67d1766465b037faacc2ff57`, tree `f41d40d18d2eb92906f6a07ec12b4267e6382613`. The existing `5d5184f` memory-lifecycle changes remain unchanged; this integration adds only the remaining source differences. Fresh Linux verification passed locked restore, Release solution build with zero warnings/errors, Core 803/803 and Editor 131/131, with zero skips. Hosted cross-platform CI is required separately; no release or GUI acceptance is implied. Worktree creation and the separate Mac titlebar work remain excluded.

Source attribution: the joint source archive SHA-256 is `aba1d0a5adcb17ca06a516c825b63e5c980961d12e4193d8b392a075660c70d4`; all 350 source-file hashes matched its manifest before integration. The original inputs are recorded below. This is a source integration, not an import of private local Git history.

- base: `combined.patch`, SHA-256 `efec51b414560a66b7043c3755f9e46e8ae80fd1f65e238332fe9cb87ef1b074`.
- search: `grove-search-s1-s2.patch`, SHA-256 `bbd6380c23094d25749b7920b51dc46ab5220711745486583d01438638d3464e`.
- push: `Grove-Push-review.patch`, SHA-256 `47d28f2a6211e96a16451821c0ebe85aa45ca81f0b207390b5afa8a5fcd8f8b0`.
- ui: `grove-history-ui-candidate.patch`, SHA-256 `be3d86dcea61df46c39b6b734921a0e735821592306922d714b4ac8dcf44775f`.
- hunk: `grove-direct-hunk-revert.patch`, SHA-256 `64800fa305c0a8c919611c50f632fae7cbb4872f7d84b4e04228cfebb0c8774b`.
- draft: `Grove-draft-recovery.patch`, SHA-256 `5ff2b8d4dd5c8e12ae5131192a36b5211034afe9abf64935ed7e51320f683b12`.

## Included and excluded

Includes the recovered Linux + memory (`5d5184f`) + update diagnostics candidate, search S1/S2, responsive history/Diff UI (`9a21452`), Push review, direct hunk revert, and opt-in draft recovery. Worktree creation and Mac titlebar candidate patches are excluded. Baseline worktree browsing is unchanged.

The only non-document textual overlap was the Editor package test command. It was manually unioned to retain source-minimap, source-symbols-ui, and side-header tests once each. Development/status append-only sections were preserved as historical records. No conflict was silently chosen away.

## Integration fixes

1. Draft restoration now returns an object acknowledgement across the string-only WebView bridge, not a boolean primitive rejected by DecodeObject. Missing/invalid replies explicitly mean unknown: the editor may already have restored content, while disk and recovery copy remain untouched.
2. Direct hunk revert and undo recovery freeze source editing before testing dirty/busy/IME state. The lease includes newly opened tabs and lasts through write reconciliation. Multifile recovery uses actual Files paths, not its descriptive Path. Save/close cannot cross the lease.
3. Linux draft identity no longer depends on mutable .NET creation-time fallback. Read-only statx verifies device/inode/birthtime; unsupported birthtime fails closed. No repository marker is written. Prior unpublished Linux-candidate records are preserved and counted as unassigned when root matches but identity differs; they are not automatically migrated to a potentially replaced repository.

## Validation

See delivered result logs and TRX for exact final counts. Verification uses .NET 10.0.401, Node 24.19.0 and PowerShell 7.5.4 on Linux x64. Release solution build is warnings-as-errors. Linux x64 self-contained publish and package checks validate hashes, all extracted files and executable permission. PlatformProbe loads real libgit2 and verifies ELF architectures; it does not instantiate a native WebView.

Tests cover object acknowledgements, unknown replies, single/multiple-file dirty/busy guards, hunk recovery combined with persisted drafts, new-tab edit locking, search exclusion of private drafts and write commands, local bare-remote Push of committed content only, repository identity stability/replacement, and write-state lifetime. These are managed/Node tests, not native keyboard/UI acceptance.

Initial failed logs are retained. A genuine Linux draft identity defect was exposed by Push. Separate harness failures came from TMPDIR inside the checkout (ordinary folders were found within a parent repository) and a noncanonical `..` TMPDIR (path proof checks intentionally reject mismatches). Final runs use a canonical absolute temporary directory outside the checkout. Only the unrelated multiline CUA tool-documentation environment variable is removed for Core tests; Git/HOME security inputs and proof logic are unchanged.

## Limits before acceptance

- Native Linux GUI, WebView, physical keyboard/IME, DPI, dialogs, actual window close during Push, crash/power-loss recovery and askpass are **not accepted**. Prior environment restrictions deny required GUI/pipe operations; they were not bypassed or retried here.
- No current display session or installed WPE/WebKitGTK runtime. Package structure is not proof of desktop usability. `ldd` also reports optional .NET LTTng tracing provider dependency `liblttng-ust.so.0` missing.
- Linux arm64 and Windows/macOS native runtime/ACL behavior are untested here. No signatures or release upload.
- Linux updates remain manual through Releases, including preview discovery. No automatic check/download/install was re-enabled.
- Draft storage requires stable filesystem birthtime on Linux. Old unassigned records are retained; review/back up before explicitly clearing, because clear removes all stored copies.
