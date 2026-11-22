# Full feature implementation

Implementation of fourteen feature opportunities was requested on 2026-10-13.

## Scope and architecture

1. WASAPI endpoint selection persists an opaque endpoint ID; an empty ID follows
   the Windows default. Endpoint notifications refresh the picker. Losing a
   pinned endpoint ends capture and never silently switches microphones.
2. Rewrite responses require complete output. Technical mode validates protected
   content conservatively; ambiguity falls back to Clean. Fallback is visible.
3. An isolated whisper.cpp v1.9.2 child holds the native context. Requests use a
   current-user-only named pipe, bounded PCM chunks and immutable session settings.
   Cancellation kills the child when needed; a new child cannot deliver stale text.
   The child loads the CUDA backend and the single best CPU backend in ggml's own
   score order rather than mapping every variant, because Smart App Control rates
   each unsigned file separately. Load failures fail quietly and name App Control blocks.
4. Pinned Silero VAD gates speech processing. Recording always stops explicitly.
5. Local dictionary hints and whole-phrase aliases are separate. Corrections are
   learned only through an explicit action in Private Flow's editor.
6. Snippets expand once, with longest-match precedence, quote protection and a
   literal mode. Snippet bodies bypass rewriting and contain only text.
7. Explicit formatting/spelling mode composes newlines, bullets and identifiers;
   it does not emit an Enter key. Exact remains verbatim.
8. Task profiles snapshot language, style, vocabulary and formatting at start.
   Tray selection and profile shortcuts are explicit; executable matching cannot
   implicitly activate API rewriting.
9. The latest original/final result and diff remain in RAM, with tray recovery.
10. Audio file import uses the local pipeline and always opens a preview.
11. Setup/health checks and exported metadata-only diagnostics never contain
    audio, transcripts, prompts, credentials or private paths.
12. Optional local polish uses a pinned llama.cpp runtime and Qwen3-4B Q4 model.
    Setup downloads are explicit. CPU rewriting avoids competing with GPU ASR.
    Output integrity checks and deterministic Clean fallback still apply.
13. Dictation capture feeds a bounded processing queue in RAM. Backpressure ends
    an overloaded recording with a clear error rather than growing without bound.
    Only the finalized session is inserted, after explicit stop.
14. Meeting capture is a separate user-started microphone/system-audio workflow.
    Separate local channel chunks survive interruptions. Timestamped transcripts,
    editable speakers, local summaries, evidence-linked actions, archive search
    and grounded questions use local data. The first archive lives on the PC;
    explicit export supports owner-managed backups without a required server.

Persistent dictation history remains off. Meeting retention is configurable and
deletion acts only on entries in the app-owned meeting archive. Local models,
recordings and settings stay outside Git. Default API Concise stays opt-in.

## Validation

Use scripts/build.ps1 -Portable. Add behavior-level regressions for rewrite
integrity, phrase expansion, profile snapshots, chunk boundaries, archive path
containment, citations and retention. Use generated audio for native ASR/VAD,
warm/cold measurements, chunked transcription and meeting archive fixtures.
Diagnostic UI instances use an isolated data root and do not activate microphone
capture, global shortcuts or insertion. Real microphone and app-insertion trials
remain owner-initiated; report any unverified behavior plainly.

## Progress

- All fourteen opportunities have an implemented workflow. Version 0.4.0 adds
  the workspace UI, native worker, pinned VAD/local polish setup and local archive.
- Version 0.5.0 (2026-11-17) is the first beta; builds after it are 0.5.n, one
  number for each change to the shipped app (RELEASE_NOTES.md).
- 200 behavior/preference checks pass. Real synthetic speech verifies CPU and
  CUDA transcription, silence rejection, warm reuse, cancellation, long files,
  meeting recovery, speaker persistence and local polish. See VALIDATION.md.
- Meeting notes/questions use extractive evidence, lexical retrieval and editable
  channel labels. Automatic remote-speaker diarization, semantic generated answers
  and automatic server backups remain future extensions; they are not required
  for these evidence-based workflows.
- Live microphone/device events, loopback timing and actual shortcut/insertion
  trials remain manual. No real microphone or existing app received test
  input.
