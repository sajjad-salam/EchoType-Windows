# Graph Report - EchoType-Windows  (2026-11-06)

## Corpus Check
- cluster-only mode — file stats not available

## Summary
- 2118 nodes · 5900 edges · 90 communities (52 shown, 38 thin omitted)
- Extraction: 95% EXTRACTED · 5% INFERRED · 0% AMBIGUOUS · INFERRED: 307 edges (avg confidence: 0.85)
- Token cost: 19,430 input · 3,955 output

## Graph Freshness
- Built from commit: `3010bb5c`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- ChatGPT WebView Automation
- Layout-Preserving Translation
- Dictation Driver Waits
- Audio Session COM Interfaces
- Tray App Core
- Settings & Hotkey Preferences
- App Profiles Configuration
- Main Window Lifecycle
- Gemini Web Transcriber
- Transcript Delivery Pipeline
- Clean Prompt Dialog
- Virtual Desktop Window Grouping
- Background Audio Muting
- Win32 Native Interop
- Status Overlay HUD
- Mic Level Metering
- Text Expander Engine
- Assistant Reply Capture
- Keyboard Hook Monitor
- Window Layout Builder
- AI Site UI Selectors
- Hotkey Row Controls
- Hotkey Chord Model
- Custom Command Model
- Clipboard Copy Results
- Dictation Job State
- Workspace Modes Editor
- Settings Shortcut Fields
- Settings Editor Launchers
- Selection Capture & Background Paste
- Synthesized Input Injection
- Audio Device Enumeration
- Command Editor Form
- Command Reply Error Handling
- High-Level Feature Groups
- Paste Target Detection
- Shortcut Picker Forms
- Caption Buttons & Drag
- Audio Client Interfaces
- Accessibility Tree Reading
- Fonts & Colors
- UI Source Files
- Release Workflow
- Grid Editor Forms
- CI Workflow
- Sound Wave Generation
- Input & Native Files
- Translation Source Files
- Model Shortcuts Form
- Toggle & Nav Controls
- HUD Button Styles
- Custom Commands Form
- Web Selector Files
- Localization & RTL
- Virtual Desktop 24H2 API
- Language Combo & DPI
- Audio Module Files
- Tray Icons & Phases
- Virtual Desktop Win11 API
- Segmented Control
- Virtual Desktop Win10 API
- Visual Theme Files
- Chat Site Profiles
- Startup Model Benchmark
- Google Translation Engine
- Core Data Files
- Image References
- Transcription Providers
- Logging Utilities
- Driver Failure Reasons
- Arabic Font Loading
- Project & Dependencies
- Application View Collection
- Hotkey Conflict Detection
- Program Entry Point
- ChatGPT Translation
- Overlay Notification Kinds
- Motion States
- WebView Host Form
- Claude Selectors

## God Nodes (most connected - your core abstractions)
1. `TrayAppContext` - 267 edges
2. `ChatGPTWebController` - 115 edges
3. `DictationDriver` - 104 edges
4. `AppWindow` - 94 edges
5. `Settings` - 87 edges
6. `NativeMethods` - 72 edges
7. `HotkeyChord` - 59 edges
8. `IAppWindowHost` - 59 edges
9. `StatusOverlay` - 57 edges
10. `AskReplyForm` - 55 edges

## Surprising Connections (you probably didn't know these)
- `CommandEditorForm` --calls--> `CommandButton`  [INFERRED]
  src/EchoType/Ui/CommandEditorForm.cs → src/EchoType/CustomCommand.cs
- `GeminiWebTranscriber` --references--> `ChatGPTWebController`  [EXTRACTED]
  src/EchoType/Telegram/GeminiWebTranscriber.cs → src/EchoType/Web/ChatGPTWebController.cs
- `DictationJob` --references--> `ChatGPTWebController`  [EXTRACTED]
  src/EchoType/TrayAppContext.cs → src/EchoType/Web/ChatGPTWebController.cs
- `StartupModelCandidate` --references--> `ChatGPTWebController`  [EXTRACTED]
  src/EchoType/TrayAppContext.cs → src/EchoType/Web/ChatGPTWebController.cs
- `StartupModelProbe` --references--> `ChatGPTWebController`  [EXTRACTED]
  src/EchoType/TrayAppContext.cs → src/EchoType/Web/ChatGPTWebController.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Original Input Context Restoration** — readme_targeted_paste, readme_clipboard, readme_virtual_desktop [EXTRACTED 1.00]
- **Independent Provider Roles** — readme_transcription_provider, readme_auto_clean, readme_ask_model [EXTRACTED 1.00]

## Communities (90 total, 38 thin omitted)

### Community 0 - "ChatGPT WebView Automation"
Cohesion: 0.06
Nodes (13): ChatGPTWebController, ChatThreadSends, Driver, ForceNewChatThread, HasWebView, IsInteractive, IsLoginWindowVisible, IsOnline (+5 more)

### Community 1 - "Layout-Preserving Translation"
Cohesion: 0.05
Nodes (19): LayoutPreservingTranslation, Part, PieceTranslator, AskReplyForm, CaptionText, CreateParams, ShowWithoutActivation, Block (+11 more)

### Community 2 - "Dictation Driver Waits"
Cohesion: 0.10
Nodes (8): BenchmarkResult, DictationDriver, SupportsLiveTranscript, UserScript, DriverException, Kind, PageState, IsChatReady

### Community 3 - "Audio Session COM Interfaces"
Cohesion: 0.07
Nodes (8): IAudioSessionControl, IAudioSessionControl2, IAudioSessionManager2, IMMDevice, IAudioEndpointVolume, IMMDevice, IMMDevice, IMMDeviceEnumerator

### Community 4 - "Tray App Core"
Cohesion: 0.04
Nodes (25): TrayAppContext, AskModelName, AskWeb, AskWindowVisible, BenchmarkRecording, BenchmarkRunning, BenchmarkStatus, ClassicTranslationEngine (+17 more)

### Community 5 - "Settings & Hotkey Preferences"
Cohesion: 0.03
Nodes (58): Settings, ActiveCommands, AddWordReplacementChord, AddWordReplacementVks, AppProfiles, AskModelVk, AskProvider, AskProviderName (+50 more)

### Community 6 - "App Profiles Configuration"
Cohesion: 0.05
Nodes (14): AppProfile, Apps, AutoClean, CleanPrompt, DisplayName, PressEnter, TranslateTo, AppProfilesForm (+6 more)

### Community 7 - "Main Window Lifecycle"
Cohesion: 0.06
Nodes (19): AppWindow, IAppWindowHost, AskModelName, AskWindowVisible, BenchmarkRecording, BenchmarkRunning, BenchmarkStatus, CleanModelName (+11 more)

### Community 8 - "Gemini Web Transcriber"
Cohesion: 0.07
Nodes (13): GeminiWebTranscriber, TelegramBot, TelegramException, Code, TelegramIncoming, AudioFileId, AudioMime, AudioSize (+5 more)

### Community 12 - "Clean Prompt Dialog"
Cohesion: 0.10
Nodes (15): CleanPromptForm, Result, DialogTheme, OpenAiKeyForm, Result, QuickWordReplacementForm, From, OpenFullList (+7 more)

### Community 13 - "Virtual Desktop Window Grouping"
Cohesion: 0.11
Nodes (9): IApplicationView, IServiceProvider10, IVirtualDesktop10, IVirtualDesktop11, IVirtualDesktop24H2, IVirtualDesktopManager, SwitcherForm, CreateParams (+1 more)

### Community 14 - "Background Audio Muting"
Cohesion: 0.14
Nodes (5): BackgroundAudioMuter, IAudioSessionEnumerator, IMMDeviceCollection, ISimpleAudioVolume, PausedMedia

### Community 15 - "Win32 Native Interop"
Cohesion: 0.08
Nodes (14): BITMAPINFO, BITMAPINFOHEADER, BLENDFUNCTION, HARDWAREINPUT, KBDLLHOOKSTRUCT, LowLevelKeyboardProc, LowLevelMouseProc, MOUSEINPUT (+6 more)

### Community 16 - "Status Overlay HUD"
Cohesion: 0.10
Nodes (10): Mode, Hidden, Listening, Notice, Working, StatusOverlay, CreateParams, IsCancelable (+2 more)

### Community 17 - "Mic Level Metering"
Cohesion: 0.10
Nodes (4): IAudioCaptureClient, IAudioMeterInformation, MicLevelMeter, Level

### Community 18 - "Text Expander Engine"
Cohesion: 0.09
Nodes (5): Entry, TextExpander, TextShortcut, Text, Trigger

### Community 19 - "Assistant Reply Capture"
Cohesion: 0.10
Nodes (12): AssistantImage, CaptureState, CommandTurnSnapshot, Empty, ModelReply, Empty, HasLoadedImage, IsEmpty (+4 more)

### Community 21 - "Window Layout Builder"
Cohesion: 0.08
Nodes (16): Page, Blocks, Body, Nav, Subtitle, Title, HudCard, Accent (+8 more)

### Community 22 - "AI Site UI Selectors"
Cohesion: 0.06
Nodes (31): SelectorSet, Assistant, AssistantMarkdown, Cancel, CancelCss, Composer, DictatingCss, GeneratingCss (+23 more)

### Community 24 - "Hotkey Row Controls"
Cohesion: 0.09
Nodes (10): HudHotkeyRow, CapturePreview, Capturing, MultiKey, Optional, Title, Value, HudNavItem (+2 more)

### Community 25 - "Hotkey Chord Model"
Cohesion: 0.12
Nodes (7): HotkeyChord, Count, IsEmpty, K1, K2, K3, LegacyVk

### Community 26 - "Custom Command Model"
Cohesion: 0.09
Nodes (17): CommandButton, DisplayName, Id, Name, Prompt, CustomCommand, Buttons, Chord (+9 more)

### Community 29 - "Clipboard Copy Results"
Cohesion: 0.16
Nodes (7): DeliverResult, KeptUserFocus, Outcome, Outcome, CopiedToClipboard, Pasted, Paster

### Community 30 - "Dictation Job State"
Cohesion: 0.08
Nodes (24): DictationJob, AskImages, AskModel, Cancel, Cancelled, Command, Delivered, Generating (+16 more)

### Community 31 - "Workspace Modes Editor"
Cohesion: 0.16
Nodes (6): WorkspaceModesForm, Result, WorkspaceMode, Keywords, Name, Shortcut

### Community 32 - "Settings Shortcut Fields"
Cohesion: 0.11
Nodes (13): AppShortcut, AddWordReplacement, AskModel, AutoClean, ChatGpt, Claude, Dictation, Gemini (+5 more)

### Community 35 - "Synthesized Input Injection"
Cohesion: 0.25
Nodes (3): INPUT, InputUnion, KEYBDINPUT

### Community 36 - "Audio Device Enumeration"
Cohesion: 0.13
Nodes (3): IMMDeviceEnumerator, IMMDeviceEnumerator, MicInputVolume

### Community 37 - "Command Editor Form"
Cohesion: 0.15
Nodes (3): ActionRow, CommandEditorForm, Result

### Community 38 - "Command Reply Error Handling"
Cohesion: 0.12
Nodes (9): ReadyOutcome, LoggedOut, NotReady, Offline, Ready, RuntimeMissing, Timeout, UiChanged (+1 more)

### Community 40 - "Paste Target Detection"
Cohesion: 0.16
Nodes (5): PasteTarget, AcceptsBackgroundPaste, Description, ProcessName, StillExists

### Community 41 - "Shortcut Picker Forms"
Cohesion: 0.12
Nodes (4): HotkeyNames, ShortcutPickerForm, Chord, HotkeyVk

### Community 42 - "Caption Buttons & Drag"
Cohesion: 0.11
Nodes (5): Glyph, Close, Minimize, HudCaptionButton, Icon

### Community 45 - "Accessibility Tree Reading"
Cohesion: 0.18
Nodes (3): AxBox, SelectorOverrides, FilePath

### Community 47 - "UI Source Files"
Cohesion: 0.14
Nodes (3): EchoType.Telegram, EchoType.Ui, EchoType.Hotkey

### Community 48 - "Release Workflow"
Cohesion: 0.11
Nodes (17): jobs, release, name, on, push, permissions, contents, tags (+9 more)

### Community 50 - "CI Workflow"
Cohesion: 0.12
Nodes (16): runs-on, steps, jobs, build, name, on, pull_request, push (+8 more)

### Community 55 - "Toggle & Nav Controls"
Cohesion: 0.14
Nodes (3): HudToggleRow, Checked, Description

### Community 56 - "HUD Button Styles"
Cohesion: 0.15
Nodes (7): HudButton, StyleKind, Kind, Danger, Ghost, Inverse, Primary

### Community 58 - "Web Selector Files"
Cohesion: 0.14
Nodes (7): EchoType.Web, GeminiSelectors, Gemini, MicCapture, OfflinePage, Selectors, ChatGpt

### Community 59 - "Localization & RTL"
Cohesion: 0.15
Nodes (3): Arabic, Rtl, Loc

### Community 63 - "Tray Icons & Phases"
Cohesion: 0.19
Nodes (9): AppPhase, ChoosingAction, Engaging, Generating, Idle, Listening, Transcribing, Waking (+1 more)

### Community 65 - "Segmented Control"
Cohesion: 0.21
Nodes (4): HudSegmented, ArabicItemFont, Items, SelectedIndex

### Community 67 - "Visual Theme Files"
Cohesion: 0.18
Nodes (5): WorkStage, Cleaning, Thinking, Transcribing, Translating

### Community 68 - "Chat Site Profiles"
Cohesion: 0.17
Nodes (12): ChatSite, ChatGpt, ChatUrl, Claude, DisplayName, Gemini, Id, LoginTitle (+4 more)

### Community 73 - "Transcription Providers"
Cohesion: 0.22
Nodes (4): TranscriptionProvider, ChatGpt, Claude, Gemini

### Community 74 - "Logging Utilities"
Cohesion: 0.25
Nodes (5): Log, AppDataDirectory, ExeDirectoryFilePath, FilePath, ProjectRootFilePath

### Community 76 - "Driver Failure Reasons"
Cohesion: 0.25
Nodes (8): DriverFailure, ButtonNotFound, JavaScript, LoggedOut, NotReady, Offline, Timeout, UiChanged

### Community 78 - "Project & Dependencies"
Cohesion: 0.33
Nodes (4): net10.0-windows10.0.19041.0, GTranslate (2.4.0), Microsoft.Web.WebView2 (1.0.2792.45), Microsoft.NET.Sdk

### Community 85 - "Overlay Notification Kinds"
Cohesion: 0.40
Nodes (5): OverlayKind, Error, Info, Success, Warning

### Community 86 - "Motion States"
Cohesion: 0.50
Nodes (4): Motion, Idle, In, Out

### Community 87 - "WebView Host Form"
Cohesion: 0.67
Nodes (3): WebViewHostForm, ShowWithoutActivation, SuppressActivation

## Knowledge Gaps
- **366 isolated node(s):** `ChatThreadSends`, `Driver`, `ForceNewChatThread`, `HasWebView`, `IsInteractive` (+361 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 536 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **38 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `TrayAppContext` connect `Tray App Core` to `ChatGPT WebView Automation`, `Dictation Driver Waits`, `Settings & Hotkey Preferences`, `Gemini Web Transcriber`, `Dictation Session Flow`, `Hotkey Registration`, `Transcript Delivery Pipeline`, `Clean Prompt Dialog`, `Background Audio Muting`, `Win32 Native Interop`, `Mic Level Metering`, `Text Expander Engine`, `Keyboard Hook Monitor`, `Site & Model Binding`, `Foreground Window Control`, `Clipboard Copy Results`, `Dictation Job State`, `Settings Editor Launchers`, `Command Reply Error Handling`, `Model Shortcuts Form`, `Localization & RTL`, `Audio Module Files`, `Tray Icons & Phases`, `Startup Model Benchmark`, `Google Translation Engine`, `Program Entry Point`, `Job Cancellation`?**
  _High betweenness centrality (0.162) - this node is a cross-community bridge._
- **Are the 6 inferred relationships involving `TrayAppContext` (e.g. with `Program.Main()` and `HotkeyMonitor.Start()`) actually correct?**
  _`TrayAppContext` has 6 INFERRED edges - model-reasoned connections that need verification._
- **What connects `ChatThreadSends`, `Driver`, `ForceNewChatThread` to the rest of the system?**
  _366 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `ChatGPT WebView Automation` be split into smaller, more focused modules?**
  _Cohesion score 0.05942571785268414 - nodes in this community are weakly interconnected._
- **Why does `AppWindow` connect `Main Window Lifecycle` to `Hotkey Registration`, `Clean Prompt Dialog`, `Background Audio Muting`, `Window Layout Builder`, `Custom Control Painting`, `Hotkey Row Controls`, `Foreground Window Control`, `Settings Shortcut Fields`, `Settings Editor Launchers`, `Shortcut Picker Forms`, `Caption Buttons & Drag`, `Translation Source Files`, `Model Shortcuts Form`, `Toggle & Nav Controls`, `HUD Button Styles`, `Localization & RTL`, `Language Combo & DPI`, `Segmented Control`, `Transcription Providers`, `Animation Ticks`?**
  _High betweenness centrality (0.091) - this node is a cross-community bridge._
- **Are the 5 inferred relationships involving `ChatGPTWebController` (e.g. with `GeminiWebTranscriber.TranscribeAsync()` and `TrayAppContext.ApplyAskSite()`) actually correct?**
  _`ChatGPTWebController` has 5 INFERRED edges - model-reasoned connections that need verification._
- **Should `Layout-Preserving Translation` be split into smaller, more focused modules?**
  _Cohesion score 0.050543637966500146 - nodes in this community are weakly interconnected._