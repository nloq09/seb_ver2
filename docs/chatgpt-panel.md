# ChatGPT panel prototype

This integration is based on the upstream `v3.10.0` tag, matching the SEB 3.10.0 base used by the installed official build. Its original configuration/defaults and blocked-application list are unchanged. Runtime and Client identify this custom build as `3.10.0`; it is not the signed official distribution.

This fork can host the ChatGPT website in a separate desktop browser panel. It uses the user's ChatGPT account, without an API key or a custom backend. ChatGPT starts loading in the background after the desktop browser window opens, with the panel initially hidden. Select a question and press **Ctrl + Shift + K** to send it after the website is ready. Press **Ctrl + Shift + J** to view the panel. Initialization failures are reported in the Client log and can be retried with either shortcut. The Mobile UI does not have this panel.

## Configuration compatibility

Use an existing, original `.seb` file unchanged. There are no custom ChatGPT settings or configuration keys. The fork does not change the original configuration parser, settings model, defaults, kiosk mode or service policy. It does not rewrite the supplied `.seb` file.

Launch the built application with the absolute path to the original configuration:

```powershell
& ".\SafeExamBrowser.Runtime\bin\x64\Debug\SafeExamBrowser.exe" "C:\path\to\original.seb"
```

The panel respects the original session's main/content URL filters. If these rules block ChatGPT or its required resources, the panel cannot load or authenticate. No allow rules are added automatically. Its own navigation restriction allows only HTTPS on `chatgpt.com`, `auth.chatgpt.com`, `chat.openai.com`, `auth.openai.com` and its subdomains, `auth0.openai.com`, `accounts.google.com`, `cdn.platform.openai.com`, and `challenges.cloudflare.com`. Allowed popup destinations are opened in the same embedded panel/request context instead of silently discarded. External destinations, file uploads and downloads remain blocked. Google authentication redirects are allowed, but successful account login still requires testing; provider restrictions on embedded browsers are not bypassed. Other social providers are not currently supported.

OpenAI's [login troubleshooting guidance](https://help.openai.com/en/articles/7426629-why-cant-i-log-in-to-chatgpt) identifies cookies and JavaScript for ChatGPT/OpenAI authentication domains as prerequisites. The panel does not bypass site verification or change the original session's filter rules.

## Use

1. Wait for ChatGPT to load in the background. Press **Ctrl + Shift + J** to inspect its loading status if needed.
2. Select the question and its choices on the practice page, then press **Ctrl + Shift + K**. SEB submits through the ChatGPT session and keeps the panel in its current open, collapsed, or hidden state.
3. Multiple-choice labels A–D appear at the bottom right. Press **Ctrl + Shift + H** to hide them. A new answer appears when the next question completes. Essay answers are converted to plain text and copied automatically when the session clipboard policy permits.
4. Use **Ctrl + V** in an allowed field. With an isolated clipboard, paste works inside SEB. If copying is blocked, the answer remains visible in the panel.

A small status badge at the bottom right acknowledges **Ctrl + Shift + K**, shows submission progress, and confirms when ChatGPT accepts the message. Completion and error feedback disappear after three seconds. Hover over the badge to read the detailed status.

The type selector lets you override automatic detection. To edit a question before sending, enter it in the preview and click **Dán & gửi**.

The panel starts at approximately 340 logical pixels wide (smaller on narrow windows). Drag the divider to resize. **−** collapses it to a 32-pixel strip; **›** restores it with the current draft and browser session intact. The question preview is 56 pixels high to leave more room for chat. Closing the panel hides it entirely; **Ctrl + Shift + J** opens it again.

## Session behavior and limits

- A separate in-memory request context keeps ChatGPT login cookies apart from the exam browser. Closing the panel only hides it; closing the SEB browser window disposes it and its request context.
- Question text is limited to 10,000 characters. The app does not log selected questions, prepared prompts, credentials, or answers.
- Selection is read directly from browser frames, with a focused-frame preference, including text inputs and textareas. Password inputs are excluded. Copying answers follows the session clipboard policy.
- Images, scanned PDFs and some math renderers do not expose useful selected text; manually enter or correct these questions. Cross-origin frame selection is attempted through CefSharp, rather than accessing the frame from page JavaScript.
- Pasting supports the fixed prompt ID, named textareas, and supported ProseMirror/role-based rich editors. Website changes can still require an update. Missing, hidden, disabled or read-only editors are not filled.
- Embedded login, verification challenges, responsive layout and real message submission are not guaranteed by compilation. They require the manual checks below. No authentication or verification bypass is attempted.

## Manual verification

The Debug x64 Runtime and Client targets build with Visual Studio MSBuild. This source build reports the upstream warning `Integrity module not included!` when the external native integrity module is absent; it is a development build.

All 110 Browser unit tests pass on the `v3.10.0` base, including navigation restrictions and keyboard shortcut handling. Twenty-six checks against a local Chromium 139 fixture cover text input, draft preservation, mobile/rich editors, selection, login popup routing, submitting only a matching draft, avoiding duplicate sends and avoiding cookie-consent buttons. Nine WPF checks cover the compact panel, the separate open and send actions, and answer display and copying.

A live check on the actual ChatGPT website used a 360-pixel viewport and the same SEB user agent, without signing in. It identified `#mobile-composer-prompt`, pasted the harmless test question `Trả lời đúng một từ: OK.`, submitted it, and received `OK` in the conversation. Two follow-up guest checks returned the expected multiple-choice label and a plain-text essay answer. Account login is still a separate manual check. Guest availability and limits remain controlled by ChatGPT.

NuGet reports `NU1902` for the release's pinned `CefSharp.Common` 139.0.280 dependency (a known moderate-severity vulnerability). This port keeps that upstream dependency version to match SEB 3.10.0. The native integrity module is still absent from this development build.

The built application is `SafeExamBrowser.Runtime/bin/x64/Debug/SafeExamBrowser.exe`. It uses the kiosk mode and service requirements from the original configuration. Launching the application is separate from building and testing its components.

- Verify that an existing `.seb` file is unchanged before and after use, and that its kiosk, service and URL-filter settings retain their original behavior.
- Verify text selection on a normal page, a text input and an iframe. Test Vietnamese text, quotes and JavaScript-looking text as plain data.
- Test multiple-choice and free-response prompts. Verify that an empty selection does not send a question, an oversized selection is rejected, and opening the panel alone does not send a message. Verify that **Dán & gửi** performs both steps.
- Verify the login flow and successful preparation in an empty editor. Verify that a nonempty draft is preserved and that a changed/missing editor reports failure.
- If login goes blank, inspect the status text and the Client log's `ChatGPT:` entries. Diagnostics record destination host names and error codes, not query strings, credentials, cookies or question text. HTTP errors and session-filter failures retain their error status rather than being replaced with a successful-load instruction.
- Block ChatGPT with the session URL filter and verify that it remains blocked. Check external navigation, popups, uploads and downloads.
- Disconnect the network, reload, resize the window, hide/reopen the panel, and close the session while a selection/preparation operation is pending.
- Start a new session and confirm that the previous account is signed out and question text is gone.
