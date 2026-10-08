# EchoType for Windows

Voice dictation for Windows powered by **ChatGPT, Gemini or Claude's own web dictation** — no API keys, no per-use cost.

Two recording modes (window or tray menu → **Recording**), both using **Right Ctrl** by default:

- **Hold to talk** (default): hold the key, speak, release to stop.
- **Press to start/stop**: press once to start, press the same key again to stop.

The transcript is typed into the app and field that had focus when you started. Telegram, WhatsApp, Obsidian, VS Code, browsers, terminals — any text field. You can switch windows or virtual desktops while waiting; EchoType switches back and pastes there. **Double-tap Right Ctrl** at any time to cancel the current task — nothing is pasted. In press-to-start/stop mode, a second press while listening stops and transcribes instead of cancelling; use **Esc** or a double-tap after listening has ended to cancel.

If you **select text first**, then record an instruction, EchoType sends the selection plus your spoken instruction to the selected model and pastes the model's reply over that selection. No selection means normal dictation: the transcript is pasted as-is.

Pick **ChatGPT**, **Gemini** or **Claude** from the window or the tray menu, or assign tap shortcuts to switch between them. Each model has its own login session. Custom-command prompts are shared by all of them and always use whichever model is selected.

**Claude shows a live transcript.** claude.ai writes your words into its prompt box while you are still speaking, so with Claude selected the Listening HUD grows to show the latest few lines as they arrive. When you stop recording, EchoType waits for Claude's final text and pastes that.

Add extra recording shortcuts that send the transcript to the selected model with your own prompt and paste the model's reply instead. A command can be one button or a group of buttons. Custom commands are shared across ChatGPT and Gemini, and they follow the same hold vs press-to-start/stop mode as dictation.

This is the Windows port of [DevWizardHQ/EchoType](https://github.com/DevWizardHQ/EchoType) (macOS). Same idea, rebuilt natively in C# / WinForms / WebView2. Settings live in the EchoType window — the same dark, rounded HUD language as the on-screen notices — and the tray icon stays for quick access while you dictate.

```text
Right Ctrl (hold, or press to start/stop)
      │
      ▼
Hidden WebView2 window ──► chatgpt.com  or  gemini.google.com
      │                        │              (your logged-in session)
      │                        └── that site's dictation (the page uses your mic)
      │
Transcript scraped from the composer
      │
      ▼
Clipboard + Ctrl+V ──► the app you were using
```

Your audio never touches this app: the selected model's page captures it via `getUserMedia` inside the hidden browser. EchoType just presses the buttons and reads back the result.

## Requirements

- Windows 10 or 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (only for the published build; `dotnet run` uses your SDK)
- [Microsoft Edge WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11)
- A chatgpt.com and/or gemini.google.com account, logged in once through the app's login window for each model you use
- **Windows Settings → Privacy & security → Microphone → "Let desktop apps access your microphone"** must be on (Windows has no per-app consent dialog for desktop apps — this is the switch)

## Usage

1. Start `EchoType.exe`. A tray icon appears (**black** while ChatGPT or Gemini loads, **gray = ready**) and EchoType stays in the tray. Double-click the tray icon (or **Open EchoType**) to open the window. Close the window to hide it again; EchoType keeps running in the tray. By default EchoType also starts automatically when you sign in to Windows; turn off **Start EchoType with Windows** under **Options** to stop that.
2. In the window (or tray menu) choose **ChatGPT**, **Gemini** or **Claude**. Optional: set a tap key for each model so you can switch without opening the window.
3. First time for that model: **Log in to …** in the window (or tray) → sign in (Google SSO works), or bind a tap shortcut via **Model window**. The login window hides itself once you're logged in, and the session persists across restarts. Each model keeps its own cookies.
4. Optional: **Recording** → **Hold to talk** (default) or **Press to start/stop**. The choice applies to dictation, selection rewrite, Ask model, and custom commands, and is saved.
5. Focus any text field anywhere. In hold mode, **hold Right Ctrl**, talk, **release**. In press-to-start/stop mode, **press Right Ctrl**, talk, **press it again**. The tray icon turns red while listening, blue while transcribing, then EchoType switches back to that window (and virtual desktop, if you left it) and pastes. You'll hear a confirmation sound. If Gemini or ChatGPT cuts the recording off while you are still speaking (silence detection or a max clip length), EchoType plays a warning tone, shows a “Recording stopped” toast, and transcribes only what was captured. Other apps that are playing sound (YouTube, Spotify, …) are paused when they support it, or muted otherwise, so they don't leak into the microphone; they resume when you stop. **Pause or mute other apps while dictating** turns this off. Highlight text first, then record an instruction (for example “make this shorter” or “translate to Arabic”) to rewrite that selection instead of pasting the raw transcript.
6. Optional: set a **Translate** shortcut of one to three keys, for example **Ctrl+Shift+T** (window → **Recording**: click the box, press the keys together, then release; or tray → **Set Translate shortcut…**) and pick the language under **Translate to**. Record with that key the same way as dictation: ChatGPT or Gemini only transcribes, then EchoType translates the transcript with free Google Translate (the [GTranslate](https://github.com/d4n3436/GTranslate) package, no API key) and pastes the translation. This skips waiting for a model reply, so it is faster than a custom "translate" prompt. If translation fails, the original transcript is left on the clipboard.
7. Optional: **Custom commands → Add or edit…** to add more recording keys. Each command can be a single button or a group of buttons (for example three rewrite actions on one key). EchoType dictates the same way (hold or press-to-start/stop), then runs a single button immediately or shows the group so you can pick. The chosen prompt plus the transcript go to the selected model, and the reply is pasted.

Tray icon states: **black = loading ChatGPT/Gemini** · gray = ready · amber = waking/starting · **red = listening** · blue = transcribing / waiting for a reply · orange (slashed) = offline · red (ring) = logged out.

### Gestures

Hold to talk is the default. Switch to press-to-start/stop from the EchoType window or tray menu → **Recording**. The same mode is used for dictation, selection rewrite, Ask model, and custom commands.

| Gesture | Result |
|---|---|
| **Hold to talk:** hold Right Ctrl (≥ 0.35 s), release | Transcribe and paste |
| **Press to start/stop:** press Right Ctrl, speak, press again | Transcribe and paste |
| Model ends the recording while you are still speaking | Warning sound + on-screen toast; only the captured audio is transcribed |
| Record with text selected | Transcribe the instruction, send it with the selected text to the model, paste the reply over the selection |
| Record with a custom-command key | Transcribe, then run that command: one button is sent immediately; a group of buttons asks you to pick first. The reply is pasted |
| Tap a model-switch shortcut | Switch to ChatGPT or Gemini (whichever key you assigned). Custom commands keep using that model. |
| Tap the model-window shortcut | Open the ChatGPT/Gemini window (same as tray Login). Tap again to close it. |
| **Hold to talk:** tap the hold key (< 0.35 s) | Cancel (after dictation started) |
| **Press to start/stop:** press the same key again while the mic is opening | Cancel the start |
| **Double-tap Right Ctrl** | Cancel the current task — listening (hold mode), transcribing, choosing a command button, or waiting for a reply. In press-to-start/stop mode a second press *while listening* stops and transcribes instead. Nothing is pasted when a cancel applies. |
| **Esc while listening or choosing a button** | Cancel — this is the MVP's substitute for the macOS HUD's ✕ button. Esc is swallowed system-wide *only while you're actively dictating or picking a command button*; at all other times it behaves normally |

### Custom commands

Tray menu → **Custom Commands → Add or edit…**, or the same button in the EchoType window. Commands are **global**: the same list is used for ChatGPT and Gemini. Switch models and the shortcuts still work — the prompt is sent to whichever model is selected.

Each command is a recording key plus one or more **buttons**. A command can be a single button (the original behaviour) or a group — for example Proofread, Shorten, and Expand on the same key. EchoType transcribes as usual (hold or press-to-start/stop, matching **Recording mode**). One button is sent immediately; two or more open a small chooser (click, or press 1–9). EchoType then sends that button’s `prompt + transcript` to the selected model, waits for the reply, and pastes that reply into the field where you started — even if you switched apps or virtual desktops while waiting. ChatGPT reuses the same chat for up to 25 successful requests, then rotates (or immediately if a send times out or fails). Gemini still starts a new chat each time.

If the prompt contains `{transcript}`, that placeholder is replaced with the spoken text. Otherwise the transcript is appended after the prompt.

The command’s key is reserved (swallowed) while EchoType is running, same as the dictation key — pick something you don’t type with. Right Alt and function keys (F8, F9, …) are good choices. Esc or a double-tap of the dictation key cancels the chooser without sending anything.

## Configuration

`%APPDATA%\EchoType\config.json` (created on first run). Changes made in the EchoType window or tray UI apply immediately.

```json
{
  "hotkeyVk": 163,
  "keepTranscriptOnClipboard": true,
  "toggleRecording": false,
  "transcriptionProvider": "chatgpt",
  "chatgptSwitchVk": 112,
  "geminiSwitchVk": 113,
  "openModelWindowVk": 114,
  "customCommands": [
    {
      "id": "…",
      "name": "Proofread",
      "hotkeyVk": 165,
      "prompt": "Fix grammar and spelling. Return only the corrected text.",
      "buttons": [
        {
          "id": "…",
          "name": "Proofread",
          "prompt": "Fix grammar and spelling. Return only the corrected text."
        }
      ]
    },
    {
      "id": "…",
      "name": "Rewrite",
      "hotkeyVk": 119,
      "prompt": "Make this shorter. Return only the rewritten text.",
      "buttons": [
        {
          "id": "…",
          "name": "Shorten",
          "prompt": "Make this shorter. Return only the rewritten text."
        },
        {
          "id": "…",
          "name": "Expand",
          "prompt": "Make this longer and more detailed. Return only the rewritten text."
        },
        {
          "id": "…",
          "name": "Neutral tone",
          "prompt": "Rewrite in a neutral professional tone. Return only the rewritten text."
        }
      ]
    }
  ]
}
```

- `hotkeyVk` — virtual-key code of the dictation key. `163` = Right Ctrl. Other good options: `161` Right Shift, `162` Left Ctrl… any key works; it is swallowed while the app runs, so pick something you don't type with. Also set from the EchoType window.
- `keepTranscriptOnClipboard` — `true` (default): the pasted text stays on the clipboard after pasting. `false`: your previous clipboard content is restored ~0.7 s after the paste.
- `toggleRecording` — `false` (default): hold the recording key to talk, release to stop. `true`: press once to start, press the same key again to stop. Also switched from the window or tray menu → **Recording**. Applies to dictation, selection rewrite, Ask model, and custom commands.
- `transcriptionProvider` — `"chatgpt"` (default) or `"gemini"`. Also switched from the window, the tray menu, or a model-switch shortcut.
- `chatgptSwitchVk` / `geminiSwitchVk` — tap keys that switch to that model. `0` (default) = none. `112` = F1, `113` = F2. Set from the window or tray menu → **Transcription model → Set model shortcuts…**. Reserved while EchoType is running, same as other shortcuts.
- `openModelWindowVk` — tap key that opens the ChatGPT/Gemini window (same as Login). `0` (default) = none. Set from the window or tray menu → **Set model window shortcut…**. Tap again to close the window. Reserved while EchoType is running.
- `muteOtherAppsWhileDictating` — `true` (default): other apps that are playing sound are paused when they support it, or muted otherwise, then restored when you stop. Also toggled from the window or tray menu.
- `customCommands` — global recording-key commands, managed from the window or tray menu. Used with whichever model is selected. `hotkeyVk` `165` = Right Alt. They follow `toggleRecording`. Each command has a `buttons` list (1–8). A single button is sent right after dictation; two or more show a chooser. The top-level `prompt` is kept in sync with the first button so older builds still load the file.

## Build

```powershell
dotnet build src/EchoType -c Release
dotnet run --project src/EchoType
```

Release build (single-file exe, framework-dependent):

```powershell
dotnet publish src/EchoType -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

In DEBUG builds the tray menu has a "Start dictation (debug)" item that simulates a 5 s hold, so you can test the pipeline without the hotkey.

## How it works

A hidden, focus-stealing-proof WebView2 window loads chatgpt.com or gemini.google.com using that model's persistent cookies. When you start recording it triggers the page's own dictation UI (ChatGPT: ⌃⇧D plus click fallbacks; Gemini: the microphone button), waits for the transcript to appear in the composer, clears it, and pastes it into the app you were using via the clipboard + a synthesized Ctrl+V. ChatGPT DOM selectors live in `src/EchoType/Web/Selectors.cs`; Gemini's live in `src/EchoType/Web/GeminiSelectors.cs`; Claude's in `src/EchoType/Web/ClaudeSelectors.cs` (claude.ai: the dictation mic button, clicked again to stop).

Global hotkey: a low-level keyboard hook (`WH_KEYBOARD_LL`) that swallows the dictation key, custom-command keys, and any tap shortcuts (Auto Enter toggle, model switch). Hold-to-talk stops on key-up; press-to-start/stop ignores key-up and stops on the next press of the same key. Paste targeting: the window, focused control, and virtual desktop from key-down are restored before paste, so you can switch apps (or desktops) while waiting for a reply. Desktop/taskbar/Explorer at key-down still divert to clipboard-only. If text was selected at key-down, EchoType copies that selection, sends it to the model with the transcript as the instruction, and pastes the reply. Custom-command shortcuts reuse the same dictation path, then send the transcript plus the chosen button’s prompt and scrape that model's reply.

## Troubleshooting

- **Log file**: window → *Open log*, or tray menu → *Open log*. Also written to `EchoType.log` in the project root, next to the EXE, and `%LOCALAPPDATA%\EchoType\Logs\EchoType.log`. Every dictation step, failure, and the page's real button labels are logged there.
- **"Dictation didn't start"** — check the log: if the button dump shows different labels than `Selectors.cs` (ChatGPT) or `GeminiSelectors.cs` (Gemini) or `ClaudeSelectors.cs` (Claude), that site changed its UI (patch the selectors).
- **Mic never picks up sound** (`gum=err:...` in the log) — check the Windows microphone privacy switch above, and that the right input device is the Windows default.
- **Login window shows "No internet connection"** — its Retry button works once the network is back.
- **Pasting into an elevated (admin) app doesn't work** — Windows blocks input injection from a non-elevated process (UIPI). Run EchoType as administrator if you dictate into admin apps.
- **Paste went to the wrong window** — EchoType remembers the window, input field, and virtual desktop from when you started recording, then switches back there before pasting. If that window was closed, the text stays on the clipboard.

## Differences from the macOS version (MVP)

Ported: hidden ChatGPT/Gemini webview with persistent login, hold-to-talk and press-to-start/stop recording, the full start/submit/cancel ladder with its fallbacks, transcript scraping with the same timings, paste-anywhere with clipboard restore, offline/logged-out handling, self-heal reload after repeated failures.

Not yet ported (follow-ups): history window, keep-warm/unload webview policy, self-updater.

## Privacy

EchoType records nothing itself. Audio goes from your microphone directly to chatgpt.com or gemini.google.com inside that site's own web session; the app reads only the resulting text from the page and writes it to your clipboard. No keys, no accounts, no telemetry.

## License

MIT — see [LICENSE](LICENSE). Port of [DevWizardHQ/EchoType](https://github.com/DevWizardHQ/EchoType).
