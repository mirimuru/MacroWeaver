# Architecture and milestones

## Initial architecture

- `MainWindow.xaml`: WPF editor layout.
- `MainWindow.xaml.cs`: editor interactions, selection, property editing, and file dialogs.
- `Models/MacroStep.cs`: serializable macro action model.
- `Services/MacroDocumentService.cs`: JSON persistence.
- Future `Services/MacroRunner.cs`: cancellable execution engine for sequential actions and nested control blocks.
- Future `Services/InputService.cs`: Windows input abstraction, kept separate from the editor and runner.

## Design principles

1. The editor manipulates a macro document; it should not directly send OS input.
2. The runner must support cancellation and safe cleanup of held keys/buttons.
3. Repeat blocks should contain child steps so that a sequence can be repeated as a unit.
4. Waiting should be cancellation-aware, so Stop responds promptly.
5. The app must make the difference between foreground input and window-targeted/background input explicit. Background input is not universally supported by all applications.
6. File format changes should be versioned through `FormatVersion`.

## Milestones

### M0 — Editor foundation

- [x] WPF solution and initial layout
- [x] Add keyboard, mouse, wait, and repeat action entries
- [x] Edit basic action properties
- [x] Reorder, duplicate, and delete actions
- [x] Save and load JSON documents
- [x] Windows CI build workflow

### M1 — Macro model and runner

- [ ] Add nested child steps to repeat blocks
- [ ] Add validation and user-friendly validation errors
- [ ] Implement sequential runner with cancellation, pause, and resume
- [ ] Unit-test repeat counts, delays, nested blocks, and cancellation

### M2 — Windows input

- [ ] Implement keyboard and mouse input adapter
- [ ] Add emergency stop hotkey
- [ ] Ensure held inputs are released on cancellation and exceptions
- [ ] Add input recording and event editing

### M3 — Usability and advanced options

- [ ] Add random delays and per-step enable/disable
- [ ] Add variables and conditions if needed
- [ ] Add execution log and progress indicator
- [ ] Investigate supported target-window input modes
