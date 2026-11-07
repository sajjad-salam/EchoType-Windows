---
type: "query"
date: "2026-11-07T08:35:56.655765+00:00"
question: "Preload all transcription models at application startup and select the fastest-ready model as default"
contributor: "graphify"
outcome: "useful"
source_nodes: ["TrayAppContext", "SelectFastestStartupModelAsync", "TranscriptionProvider", "ChatGPTWebController"]
---

# Q: Preload all transcription models at application startup and select the fastest-ready model as default

## Answer

Expanded from original request via graph vocabulary: [model, preload, startup, transcriber, transcription, default, ready, provider, window]. TrayAppContext already raced ChatGPT, Gemini, and Claude at startup, but disposed every non-winning ChatGPTWebController after the first Ready outcome. The implementation now keeps each non-active controller in _modelWindows, promotes the first dictation-ready controller without disposing the others, reuses preloaded controllers when opening or selecting a model, and guards manual selection from a late race result.

## Outcome

- Signal: useful

## Source Nodes

- TrayAppContext
- SelectFastestStartupModelAsync
- TranscriptionProvider
- ChatGPTWebController