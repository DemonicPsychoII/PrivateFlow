# Test coverage

How Private Flow's features are checked. What no check covers is in [VALIDATION.md](VALIDATION.md#not-verified-and-why).

## Levels, cheapest first

- **Pure**: `tests/PrivateFlow.Tests`, run by `scripts/build.ps1` and `scripts/build.sh`. The checks run in named groups, one file per area; `dotnet run --project tests/PrivateFlow.Tests -c Release -- --list` names them. A failed check is reported and the run goes on; the run ends with every failure and exit code 1. `scripts/verify.ps1 -Tier pure` runs every group as its own job and process, in parallel, each with its own temporary and output folders.
- **Engine**: gated modes against the real whisper.cpp worker in isolated data folders (`PRIVATE_FLOW_DATA_DIR`): the app's `--transcribe-file` (`scripts/smoke-test.ps1`) and the modes of the verification host `PrivateFlow.Verification.exe`, which the build puts beside the package and never into it: `--verify-workspace`, `--evaluate-dictation`, `--evaluate-meeting`, `--verify-imports` and `--verify-worker-faults`.
- **Desktop**: `--verify-overlay` (both pills with synthetic levels, its own fixture window) and `scripts/ui-smoke-test.ps1` (UI Automation patterns, no keys). `--verify-insert`, the paste check into its own fixture process, runs only on a disposable Windows test machine.
- `scripts/full-eval.ps1` runs the levels above and prints one PASS/FAIL line and the accuracy headline.

## What the checks cover

- **Recognition**: the native worker in a child process with no temporary audio left; cold and warm starts; speech detection, live windows and language detection restricted to the offered languages; accuracy per language and source (generated speech and FLEURS) with word recall, error rate and critical items such as times, numbers, names and negations; speech worker failures and their messages.
- **Output styles**: Exact, Clean, Concise and Local polish on the same transcripts; Concise through a loopback OpenAI-compatible stub, its prompt, guards, quote protection and time limit; Local polish falling back to Clean with a notice.
- **Dictation and insertion**: the dictation state table (press, release, Escape, Cancel and Quit in every state), hold to talk, the insertion policy (password fields, Private Flow itself, held modifiers, the start window) and the clipboard snapshot and restore rules.
- **Meetings**: two-channel capture with synthetic sources, the final pass, echo removal, search of times and numbers, the archive and its retention, rule-based and model-written notes against invented meetings with their citations.
- **Settings and setup**: persistence and bounds, unreadable and locked settings files, profiles, the model list and its install and remove rules, the setup scripts' shared settings step in Windows PowerShell 5.1 and PowerShell 7.
- **Updates and installation**: release selection, manifest, checksum and signature rules of the in-app update and `install.ps1`, with fake HTTP responses and isolated data; `scripts/uninstall-test.ps1` runs the uninstaller against fake installations.
- **Publication**: `scripts/export-public.ps1` and `scripts/publish-public.ps1 -DryRun` against throwaway repositories, and a scan of every tracked text file for private hosts, private addresses, email addresses and profile paths.

## Test-only assets

`scripts/eval-common.ps1` downloads them into `artifacts\test-assets` and checks every SHA-256 on each run. They are never shipped with the app or redistributed; licences and pinned sources are in THIRD_PARTY_NOTICES.md.

| Asset | Version | Licence |
|---|---|---|
| Piper text-to-speech (`piper_windows_amd64.zip`) | rhasspy/piper 2023.11.14-2 | MIT |
| Piper voice `en_GB-vctk-medium` | rhasspy/piper-voices `6b9cfaac` | VCTK, CC BY 4.0 |
| `en_US-l2arctic-medium` | same | L2-ARCTIC, CC BY-NC 4.0 (test use only) |
| `es_AR-daniela-high`, `es_MX-ald-medium`, `es_MX-claude-high` | same | CC BY-SA 4.0, Unlicense, Apache 2.0 |
| `de_DE-kerstin-low`, `de_DE-thorsten-high`, `de_DE-karlsson-low` | same | CC0, CC0, M-AILABS terms (see THIRD_PARTY_NOTICES.md) |
| FLEURS dev sets `en_us`, `es_419`, `de_de` | google/fleurs `70bb2e84` | CC BY 4.0 |
