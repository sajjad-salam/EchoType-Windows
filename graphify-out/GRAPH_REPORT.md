# Graph Report - EchoType-Windows  (2026-11-06)

## Corpus Check
- 78 files · ~112,371 words
- Verdict: corpus is large enough that graph structure adds value.
- Unclassified: 3 file(s) not represented in the graph (top: (none) 2, .ttf 1)

## Summary
- 2083 nodes · 5892 edges · 87 communities (59 shown, 28 thin omitted)
- Extraction: 94% EXTRACTED · 6% INFERRED · 0% AMBIGUOUS · INFERRED: 343 edges (avg confidence: 0.85)
- Token cost: 0 input · 0 output

## Community Hubs (Navigation)
- WebView2 Runtime
- Virtual Desktops
- Translation Parsing
- Dictation Jobs and Sounds
- Application Settings
- Hotkey Naming
- Application Window
- Tray Application Lifecycle
- Native Interop Structures
- Async and JSON Utilities
- Audio Session Metadata
- Hotkey Chords
- Window Event Handling
- Dictation Accessibility
- Paste Delivery
- Audio Capture Interfaces
- Status Overlay
- Web Selector Sets
- Keyboard Hook
- Application Profiles
- Native Input Injection
- Ask Reply UI
- UI Page Composition
- Media Session Control
- Text Expansion
- Prompt Dialogs
- Model Benchmarking
- Ask Job State
- ChatGPT Translation
- Application Phases
- Window and Focus Detection
- App Shortcut Settings
- Core Namespaces
- Word Replacements
- Selection Capture
- Product Capabilities
- Assistant Reply State
- Audio Endpoint Volume
- Core Audio Client
- Release Workflow
- Image Drag UI
- Text Shortcuts
- Command Editor Rows
- Quick Word Replacement
- Chat Provider Definitions
- CI Workflow
- Workspace Modes
- Model Shortcut Form
- Telegram and Translation
- Tray Icons
- Arabic Font Loading
- Audio Devices
- Custom Commands UI
- Custom Command Model
- Google Translation
- Application Models
- Audio Session Enumeration
- Model Selector UI
- WebView2 Locator
- Language and Provider Controls
- Workspace Modes UI
- Web Controller
- Chat Sites
- Toggle Controls
- Bitmap Interop
- Logging
- Command Picker
- Driver Errors
- Project Configuration
- Command Buttons
- Program Entry Point
- Selector Overrides
- Simple Audio Volume
- Button Kinds
- Overlay Modes
- Overlay Kinds
- Motion States

## God Nodes (most connected - your core abstractions)
1. `TrayAppContext` - 261 edges
2. `ChatGPTWebController` - 109 edges
3. `DictationDriver` - 105 edges
4. `AppWindow` - 96 edges
5. `Settings` - 88 edges
6. `HotkeyChord` - 77 edges
7. `NativeMethods` - 72 edges
8. `IAppWindowHost` - 60 edges
9. `StatusOverlay` - 57 edges
10. `AskReplyForm` - 55 edges

## Surprising Connections (you probably didn't know these)
- `CommandEditorForm` --calls--> `CommandButton`  [INFERRED]
  src/EchoType/Ui/CommandEditorForm.cs → src/EchoType/CustomCommand.cs
- `TrayAppContext` --references--> `AppPhase`  [EXTRACTED]
  src/EchoType/TrayAppContext.cs → src/EchoType/AppPhase.cs
- `IAppWindowHost` --references--> `AppPhase`  [EXTRACTED]
  src/EchoType/Ui/AppWindow.cs → src/EchoType/AppPhase.cs
- `Settings` --references--> `AppProfile`  [EXTRACTED]
  src/EchoType/Settings.cs → src/EchoType/AppProfile.cs
- `DictationJob` --references--> `AppProfile`  [EXTRACTED]
  src/EchoType/TrayAppContext.cs → src/EchoType/AppProfile.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Independent Provider Roles** — readme_transcription_provider, readme_auto_clean, readme_ask_model [EXTRACTED 1.00]
- **Original Input Context Restoration** — readme_targeted_paste, readme_clipboard, readme_virtual_desktop [EXTRACTED 1.00]

## Communities (87 total, 28 thin omitted)

### Community 0 - "WebView2 Runtime"
Cohesion: 0.06
Nodes (13): ChatGPTWebController, ChatThreadSends, Driver, ForceNewChatThread, HasWebView, IsInteractive, IsLoginWindowVisible, IsOnline (+5 more)

### Community 1 - "Virtual Desktops"
Cohesion: 0.05
Nodes (12): IApplicationView, IApplicationViewCollection, IServiceProvider10, IVirtualDesktop10, IVirtualDesktop11, IVirtualDesktop24H2, IVirtualDesktopManager, IVirtualDesktopManagerInternal10 (+4 more)

### Community 2 - "Translation Parsing"
Cohesion: 0.07
Nodes (17): ImageReferences, Result, LayoutPreservingTranslation, Part, PieceTranslator, Block, Kind, Bullet (+9 more)

### Community 3 - "Dictation Jobs and Sounds"
Cohesion: 0.08
Nodes (8): Sounds, CustomDir, AskReplyContent, WorkStage, Cleaning, Thinking, Transcribing, Translating

### Community 4 - "Application Settings"
Cohesion: 0.03
Nodes (58): Settings, ActiveCommands, AddWordReplacementChord, AddWordReplacementVks, AppProfiles, AskModelVk, AskProvider, AskProviderName (+50 more)

### Community 5 - "Hotkey Naming"
Cohesion: 0.07
Nodes (4): HotkeyNames, ShortcutPickerForm, Chord, HotkeyVk

### Community 6 - "Application Window"
Cohesion: 0.05
Nodes (19): AppWindow, IAppWindowHost, AskModelName, AskWindowVisible, BenchmarkRecording, BenchmarkRunning, BenchmarkStatus, CleanModelName (+11 more)

### Community 7 - "Tray Application Lifecycle"
Cohesion: 0.05
Nodes (26): TrayAppContext, AskModelName, AskSharesWindow, AskWeb, AskWindowVisible, BenchmarkRecording, BenchmarkRunning, BenchmarkStatus (+18 more)

### Community 8 - "Native Interop Structures"
Cohesion: 0.09
Nodes (8): BLENDFUNCTION, HARDWAREINPUT, LowLevelKeyboardProc, LowLevelMouseProc, NativeMethods, POINT, RECT, SIZE

### Community 9 - "Async and JSON Utilities"
Cohesion: 0.08
Nodes (13): GeminiWebTranscriber, TelegramBot, TelegramException, Code, TelegramIncoming, AudioFileId, AudioMime, AudioSize (+5 more)

### Community 10 - "Audio Session Metadata"
Cohesion: 0.08
Nodes (6): IAudioSessionControl, IAudioSessionControl2, IMMDevice, IMMDevice, IMMDevice, IMMDeviceEnumerator

### Community 11 - "Hotkey Chords"
Cohesion: 0.09
Nodes (8): HotkeyChord, Count, IsEmpty, K1, K2, K3, LegacyVk, HotkeyConflicts

### Community 13 - "Window Event Handling"
Cohesion: 0.07
Nodes (12): HudButton, StyleKind, HudHotkeyRow, CapturePreview, Capturing, MultiKey, Optional, Title (+4 more)

### Community 15 - "Dictation Accessibility"
Cohesion: 0.12
Nodes (4): AxBox, DictationDriver, SupportsLiveTranscript, UserScript

### Community 16 - "Paste Delivery"
Cohesion: 0.11
Nodes (8): DeliverResult, KeptUserFocus, Outcome, PasteTarget, AcceptsBackgroundPaste, Description, ProcessName, StillExists

### Community 17 - "Audio Capture Interfaces"
Cohesion: 0.10
Nodes (4): IAudioCaptureClient, IAudioMeterInformation, MicLevelMeter, Level

### Community 18 - "Status Overlay"
Cohesion: 0.13
Nodes (5): StatusOverlay, CreateParams, IsCancelable, ParkAfterMs, ShowWithoutActivation

### Community 19 - "Web Selector Sets"
Cohesion: 0.06
Nodes (31): SelectorSet, Assistant, AssistantMarkdown, Cancel, CancelCss, Composer, DictatingCss, GeneratingCss (+23 more)

### Community 23 - "Application Profiles"
Cohesion: 0.10
Nodes (10): AppProfile, Apps, AutoClean, CleanPrompt, DisplayName, PressEnter, TranslateTo, AppProfilesForm (+2 more)

### Community 24 - "Native Input Injection"
Cohesion: 0.15
Nodes (7): Outcome, CopiedToClipboard, Pasted, Paster, INPUT, InputUnion, KEYBDINPUT

### Community 25 - "Ask Reply UI"
Cohesion: 0.11
Nodes (4): AskReplyForm, CaptionText, CreateParams, ShowWithoutActivation

### Community 26 - "UI Page Composition"
Cohesion: 0.10
Nodes (14): Page, Blocks, Body, Nav, Subtitle, Title, HudCard, Accent (+6 more)

### Community 27 - "Media Session Control"
Cohesion: 0.23
Nodes (3): BackgroundAudioMuter, IMMDeviceCollection, PausedMedia

### Community 29 - "Prompt Dialogs"
Cohesion: 0.13
Nodes (8): CleanPromptForm, Result, DialogTheme, OpenAiKeyForm, Result, WebViewHostForm, ShowWithoutActivation, SuppressActivation

### Community 30 - "Model Benchmarking"
Cohesion: 0.12
Nodes (5): TranscriptionProvider, ChatGpt, Claude, Gemini, BenchmarkResult

### Community 31 - "Ask Job State"
Cohesion: 0.08
Nodes (24): DictationJob, AskImages, AskModel, Cancel, Cancelled, Command, Delivered, Generating (+16 more)

### Community 32 - "ChatGPT Translation"
Cohesion: 0.11
Nodes (9): ChatGptTranslation, AssistantImage, CaptureState, DriverException, Kind, PageState, IsChatReady, SelectorHealth (+1 more)

### Community 33 - "Application Phases"
Cohesion: 0.11
Nodes (9): AppPhase, ChoosingAction, Engaging, Generating, Idle, Listening, Transcribing, Waking (+1 more)

### Community 34 - "Window and Focus Detection"
Cohesion: 0.15
Nodes (4): KBDLLHOOKSTRUCT, MOUSEINPUT, MSG, IVirtualDesktopManagerInternal11

### Community 35 - "App Shortcut Settings"
Cohesion: 0.11
Nodes (13): AppShortcut, AddWordReplacement, AskModel, AutoClean, ChatGpt, Claude, Dictation, Gemini (+5 more)

### Community 36 - "Core Namespaces"
Cohesion: 0.15
Nodes (3): EchoType.Input, EchoType.Audio, EchoType.Native

### Community 37 - "Word Replacements"
Cohesion: 0.17
Nodes (5): WordReplacementsForm, Result, WordReplacement, From, To

### Community 40 - "Assistant Reply State"
Cohesion: 0.18
Nodes (8): CommandTurnSnapshot, Empty, ModelReply, Empty, HasLoadedImage, IsEmpty, Videos, ReplyState

### Community 43 - "Release Workflow"
Cohesion: 0.11
Nodes (17): jobs, release, name, on, push, permissions, contents, tags (+9 more)

### Community 44 - "Image Drag UI"
Cohesion: 0.12
Nodes (5): Glyph, Close, Minimize, HudCaptionButton, Icon

### Community 45 - "Text Shortcuts"
Cohesion: 0.15
Nodes (6): Entry, TextShortcut, Text, Trigger, TextShortcutsForm, Result

### Community 46 - "Command Editor Rows"
Cohesion: 0.18
Nodes (3): ActionRow, CommandEditorForm, Result

### Community 47 - "Quick Word Replacement"
Cohesion: 0.15
Nodes (8): QuickWordReplacementForm, From, OpenFullList, To, TelegramVoiceForm, TelegramChatId, TelegramEnabled, TelegramToken

### Community 48 - "Chat Provider Definitions"
Cohesion: 0.12
Nodes (9): EchoType.Web, ClaudeSelectors, Claude, GeminiSelectors, Gemini, MicCapture, OfflinePage, Selectors (+1 more)

### Community 49 - "CI Workflow"
Cohesion: 0.12
Nodes (16): runs-on, steps, jobs, build, name, on, pull_request, push (+8 more)

### Community 50 - "Workspace Modes"
Cohesion: 0.19
Nodes (4): WorkspaceMode, Keywords, Name, Shortcut

### Community 52 - "Telegram and Translation"
Cohesion: 0.23
Nodes (4): EchoType.Telegram, EchoType.Translation, EchoType.Ui, EchoType.Hotkey

### Community 53 - "Tray Icons"
Cohesion: 0.16
Nodes (4): TrayIcons, Arabic, Rtl, Loc

### Community 59 - "Custom Command Model"
Cohesion: 0.16
Nodes (10): CustomCommand, Buttons, Chord, DisplayName, HotkeyVk, HotkeyVks, Id, Name (+2 more)

### Community 61 - "Application Models"
Cohesion: 0.22
Nodes (3): EchoType, StartupRegistration, Command

### Community 63 - "Model Selector UI"
Cohesion: 0.21
Nodes (4): HudSegmented, ArabicItemFont, Items, SelectedIndex

### Community 65 - "Language and Provider Controls"
Cohesion: 0.17
Nodes (3): LanguageItem, HudCommandList, Items

### Community 67 - "Web Controller"
Cohesion: 0.17
Nodes (9): ReadyOutcome, LoggedOut, NotReady, Offline, Ready, RuntimeMissing, Timeout, UiChanged (+1 more)

### Community 68 - "Chat Sites"
Cohesion: 0.17
Nodes (12): ChatSite, ChatGpt, ChatUrl, Claude, DisplayName, Gemini, Id, LoginTitle (+4 more)

### Community 71 - "Toggle Controls"
Cohesion: 0.22
Nodes (3): HudToggleRow, Checked, Description

### Community 72 - "Bitmap Interop"
Cohesion: 0.28
Nodes (3): BITMAPINFO, BITMAPINFOHEADER, LayeredSurface

### Community 73 - "Logging"
Cohesion: 0.25
Nodes (5): Log, AppDataDirectory, ExeDirectoryFilePath, FilePath, ProjectRootFilePath

### Community 75 - "Driver Errors"
Cohesion: 0.25
Nodes (8): DriverFailure, ButtonNotFound, JavaScript, LoggedOut, NotReady, Offline, Timeout, UiChanged

### Community 76 - "Project Configuration"
Cohesion: 0.33
Nodes (4): net10.0-windows10.0.19041.0, GTranslate (2.4.0), Microsoft.Web.WebView2 (1.0.2792.45), Microsoft.NET.Sdk

### Community 77 - "Command Buttons"
Cohesion: 0.33
Nodes (5): CommandButton, DisplayName, Id, Name, Prompt

### Community 83 - "Button Kinds"
Cohesion: 0.40
Nodes (5): Kind, Danger, Ghost, Inverse, Primary

### Community 84 - "Overlay Modes"
Cohesion: 0.40
Nodes (5): Mode, Hidden, Listening, Notice, Working

### Community 85 - "Overlay Kinds"
Cohesion: 0.40
Nodes (5): OverlayKind, Error, Info, Success, Warning

### Community 86 - "Motion States"
Cohesion: 0.50
Nodes (4): Motion, Idle, In, Out

## Knowledge Gaps
- **367 isolated node(s):** `Idle`, `Waking`, `Engaging`, `Listening`, `Transcribing` (+362 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 517 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **28 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `TrayAppContext` connect `Tray Application Lifecycle` to `WebView2 Runtime`, `Dictation Jobs and Sounds`, `Application Settings`, `Hotkey Naming`, `Application Window`, `Async and JSON Utilities`, `Hotkey Chords`, `Dictation Driver Operations`, `Settings Actions`, `Dictation Accessibility`, `Paste Delivery`, `Audio Capture Interfaces`, `Keyboard Hook`, `Cancellation Flow`, `Application Profiles`, `UI Page Composition`, `Media Session Control`, `Text Expansion`, `Prompt Dialogs`, `Model Benchmarking`, `Ask Job State`, `Application Phases`, `Text Shortcuts`, `Telegram and Translation`, `Tray Icons`, `Custom Command Model`, `Google Translation`, `Command Retry Policy`, `Command Picker`, `Program Entry Point`, `Translation Settings`?**
  _High betweenness centrality (0.331) - this node is a cross-community bridge._
- **Are the 6 inferred relationships involving `TrayAppContext` (e.g. with `Program.Main()` and `HotkeyMonitor.Start()`) actually correct?**
  _`TrayAppContext` has 6 INFERRED edges - model-reasoned connections that need verification._
- **What connects `Idle`, `Waking`, `Engaging` to the rest of the system?**
  _367 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `WebView2 Runtime` be split into smaller, more focused modules?**
  _Cohesion score 0.056910569105691054 - nodes in this community are weakly interconnected._
- **Why does `Settings` connect `Application Settings` to `Hotkey Naming`, `Word Replacements`, `Tray Application Lifecycle`, `Application Window`, `Async and JSON Utilities`, `Custom Command Model`, `Hotkey Chords`, `Text Shortcuts`, `Settings Actions`, `Command Editor Rows`, `Workspace Modes`, `Model Shortcut Form`, `Application Profiles`, `Custom Commands UI`, `Media Session Control`, `Google Translation`, `Application Models`, `Model Benchmarking`?**
  _High betweenness centrality (0.099) - this node is a cross-community bridge._
- **Are the 4 inferred relationships involving `ChatGPTWebController` (e.g. with `GeminiWebTranscriber.TranscribeAsync()` and `TrayAppContext.ApplyAskSite()`) actually correct?**
  _`ChatGPTWebController` has 4 INFERRED edges - model-reasoned connections that need verification._
- **Should `Virtual Desktops` be split into smaller, more focused modules?**
  _Cohesion score 0.05179982440737489 - nodes in this community are weakly interconnected._