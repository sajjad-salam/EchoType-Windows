# EchoType for Windows

Hold-to-talk voice dictation for Windows powered by **ChatGPT's own web dictation** — no API keys, no per-use cost.

Hold **Right Ctrl**, speak, release: the transcript is typed into whatever app has focus. Telegram, WhatsApp, Obsidian, VS Code, browsers, terminals — any text field.

This is the Windows port of [DevWizardHQ/EchoType](https://github.com/DevWizardHQ/EchoType) (macOS). Same idea, same ChatGPT-web-dictation engine, rebuilt natively in C# / WinForms / WebView2.

```text
Right Ctrl (hold)
      │
      ▼
Hidden WebView2 window ──► chatgpt.com (your logged-in session)
      │                        │
      │                        └── ChatGPT dictation (the page uses your mic)
      │
Transcript scraped from the composer
      │
      ▼
Clipboard + Ctrl+V ──► the app you were using
```

Your audio never touches this app: ChatGPT's own page captures it via `getUserMedia` inside the hidden browser. EchoType just presses the buttons and reads back the result.

## Requirements

- Windows 10 or 11, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) (only for the published build; `dotnet run` uses your SDK)
- [Microsoft Edge WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (preinstalled on Windows 11)
- A chatgpt.com account, logged in once through the app's login window
- **Windows Settings → Privacy & security → Microphone → "Let desktop apps access your microphone"** must be on (Windows has no per-app consent dialog for desktop apps — this is the switch)

## Usage

1. Start `EchoType.exe`. A tray icon appears (gray = ready).
2. First time only: tray menu → **Open ChatGPT Login…** → sign in to chatgpt.com (Google SSO works). The window hides itself once you're logged in, and the session persists across restarts.
3. Focus any text field anywhere, **hold Right Ctrl**, talk, **release**. The tray icon turns red while listening, blue while transcribing, then the text is pasted and you'll hear a confirmation sound.

Tray icon states: gray = ready · amber = waking/starting · **red = listening** · blue = transcribing · orange (slashed) = offline · red (ring) = logged out.

### Gestures

| Gesture | Result |
|---|---|
| Hold Right Ctrl (≥ 0.35 s), release | Transcribe and paste |
| Tap Right Ctrl (< 0.35 s) | Cancel (after dictation started) |
| **Esc while listening** | Cancel — this is the MVP's substitute for the macOS HUD's ✕ button. Esc is swallowed system-wide *only while you're actively dictating*; at all other times it behaves normally |

## Configuration

`%APPDATA%\EchoType\config.json` (created on first run; restart to apply — no settings UI yet):

```json
{
  "hotkeyVk": 165,
  "keepTranscriptOnClipboard": true
}
```

- `hotkeyVk` — virtual-key code of the hold-to-talk key. `165` = Right Ctrl. Other good options: `161` Right Shift, `162` Left Ctrl… any key works; it is swallowed while the app runs, so pick something you don't type with.
- `keepTranscriptOnClipboard` — `true` (default): the transcript stays on the clipboard after pasting. `false`: your previous clipboard content is restored ~0.7 s after the paste.

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

A hidden, focus-stealing-proof WebView2 window loads chatgpt.com using your persistent logged-in cookies. When you hold the hotkey it triggers ChatGPT's own dictation UI (its ⌃⇧D equivalent shortcut, with trusted CDP click and JS click fallbacks), waits for the transcript to appear in the composer, clears it, and pastes it into the foreground app via the clipboard + a synthesized Ctrl+V. All chatgpt.com DOM selectors live in one file (`src/EchoType/Web/Selectors.cs`) so a ChatGPT UI change is a one-file patch.

Global hotkey: a low-level keyboard hook (`WH_KEYBOARD_LL`) that swallows the hotkey key. Paste targeting: foreground-window inspection with a deliberate bias toward pasting rather than losing the transcript (desktop/taskbar/Explorer divert to clipboard-only).

## Troubleshooting

- **Log file**: tray menu → *Open log*, or `%LOCALAPPDATA%\EchoType\Logs\EchoType.log`. Every dictation step, failure, and the page's real button labels are logged there.
- **"Dictation didn't start"** — check the log: if the button dump shows different labels than `Selectors.cs`, ChatGPT changed its UI (patch the selectors).
- **Mic never picks up sound** (`gum=err:...` in the log) — check the Windows microphone privacy switch above, and that the right input device is the Windows default.
- **Login window shows "No internet connection"** — its Retry button works once the network is back.
- **Pasting into an elevated (admin) app doesn't work** — Windows blocks input injection from a non-elevated process (UIPI). Run EchoType as administrator if you dictate into admin apps.
- **Transcript pasted while I was typing elsewhere** — the paste goes to whatever is focused the moment you release the key; don't switch windows mid-dictation.

## Differences from the macOS version (MVP)

Ported: hidden ChatGPT webview with persistent login, hold-to-talk global hotkey, the full start/submit/cancel ladder with its fallbacks, transcript scraping with the same timings, paste-anywhere with clipboard restore, offline/logged-out handling, self-heal reload after repeated failures.

Not yet ported (follow-ups): HUD status pill, hands-free double-tap mode, history window, settings UI, keep-warm/unload webview policy, self-updater.

## Privacy

EchoType records nothing itself. Audio goes from your microphone directly to chatgpt.com inside its own web session; the app reads only the resulting text from the page and writes it to your clipboard. No keys, no accounts, no telemetry.

## License

MIT — see [LICENSE](LICENSE). Port of [DevWizardHQ/EchoType](https://github.com/DevWizardHQ/EchoType).
