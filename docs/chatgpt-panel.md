# ChatGPT panel prototype

This integration is based on the upstream `v3.10.0` tag, matching the SEB 3.10.0 base used by the installed official build. Its original configuration/defaults and blocked-application list are unchanged. Runtime and Client identify this custom build as `3.10.0`; it is not the signed official distribution.

This fork can host the ChatGPT website in a separate desktop browser panel. It uses the user's ChatGPT account, without an API key or a custom backend. ChatGPT starts loading in the background after the desktop browser window opens, with the panel initially hidden. Select a question and press **Ctrl + Shift + K** for multiple choice or **Ctrl + Shift + Y** for an essay after the website is ready. Press **Ctrl + Shift + J** to view the panel and **Ctrl + Shift + U** to begin account login. Startup, opening the panel, asking a guest question and reloading do not read credentials or begin login. Initialization failures are reported in the Client log and can be retried with any of these shortcuts. The Mobile UI does not have this panel.

## Configuration compatibility

Use an existing, original `.seb` file unchanged. There are no custom ChatGPT settings or configuration keys. The fork does not change the original configuration parser, settings model, defaults, kiosk mode or service policy. It does not rewrite the supplied `.seb` file.

Launch the built application with the absolute path to the original configuration:

```powershell
& ".\SafeExamBrowser.Runtime\bin\x64\Debug\SafeExamBrowser.exe" "C:\path\to\original.seb"
```

The panel respects the original session's main/content URL filters. If these rules block ChatGPT or its required resources, the panel cannot load or authenticate. No allow rules are added automatically. Its own navigation restriction allows only HTTPS on `chatgpt.com`, `auth.chatgpt.com`, `chat.openai.com`, `auth.openai.com` and its subdomains, `auth0.openai.com`, `accounts.google.com`, `cdn.platform.openai.com`, `sentinel.openai.com`, `challenges.cloudflare.com`, and the restricted CAPTCHA URLs described below. Allowed popup destinations are opened in the same embedded panel/request context instead of silently discarded. External destinations, file uploads and downloads remain blocked. Google authentication redirects are allowed, but successful account login still requires testing; provider restrictions on embedded browsers are not bypassed. Other social providers are not currently supported.

OpenAI's [login troubleshooting guidance](https://help.openai.com/en/articles/7426629-why-cant-i-log-in-to-chatgpt) identifies cookies and JavaScript for ChatGPT/OpenAI authentication domains as prerequisites. The panel does not bypass site verification or change the original session's filter rules.

## Use

1. Wait for ChatGPT to load in guest mode in the background. Press **Ctrl + Shift + J** to inspect it, or **Ctrl + Shift + U** to read the saved login and start authentication. The login shortcut opens the panel without selecting or sending a question. It waits for any current question to finish and reuses an already pending login attempt.
2. Select the question on the practice page. Press **Ctrl + Shift + K** to send it with the multiple-choice prompt, which asks for answer labels such as B or A, C. Press **Ctrl + Shift + Y** to send it with the essay prompt, which asks for a complete plain-text answer in Vietnamese. Each shortcut selects its question type explicitly. SEB keeps the panel in its current open, collapsed, or hidden state.
3. Multiple-choice labels A–D appear at the bottom right. Press **Ctrl + Shift + H** to hide them. A new answer appears when the next question completes. Essay answers are converted to plain text and copied automatically when the session clipboard policy permits.
4. Use **Ctrl + V** in an allowed field. With an isolated clipboard, paste works inside SEB. If copying is blocked, the answer remains visible in the panel.

A small status badge at the bottom right acknowledges **Ctrl + Shift + K** and **Ctrl + Shift + Y**, shows submission progress, and confirms when ChatGPT accepts the message. Completion and error feedback disappear after three seconds. Hover over the badge to read the detailed status.

The type selector controls **Dán & gửi**, including automatic detection when selected. To edit a question before sending, enter it in the preview and click **Dán & gửi**. Keyboard shortcuts use their assigned type.

The panel starts at approximately 340 logical pixels wide (smaller on narrow windows). Drag the divider to resize. **−** collapses it to a 32-pixel strip; **›** restores it with the current draft and browser session intact. The question preview is 56 pixels high to leave more room for chat. Closing the panel hides it entirely; **Ctrl + Shift + J** opens it again.

## Session behavior and limits

For automatic email/password login, close SEB and copy `.env.example` to `.env` beside `SafeExamBrowser.exe`. Set `CHATGPT_EMAIL` and `CHATGPT_PASSWORD` in the copied file, then press **Ctrl + Shift + U** in SEB. The file uses UTF-8 and supports blank lines, lines beginning with `#`, and values enclosed in matching single or double quotes. Quoted values are literal, including backslashes, `$`, `#` and `=`. Keep comments on separate lines. The `.env` file contains the credentials as plain text and is excluded from Git.

When `.env` is absent, SEB reads the encrypted login saved by `ConfigureChatGptLogin.ps1`. Run that script under the same Windows account that runs SEB to save `%LOCALAPPDATA%\SafeExamBrowser\ChatGpt\credentials.dat`, protected with Windows DPAPI for the current user. Configure that file separately inside a VM. Press **Ctrl + Shift + U** to read either login file after changing it.

Set `CHATGPT_LOGIN_PROVIDER=google` in `.env` when the saved password belongs to Google. The default provider is `openai`. The Google flow selects **Continue with Google** directly, chooses an existing account only when it matches `CHATGPT_EMAIL`, and fills Google's email/password steps once. It verifies the displayed account before entering the password. Each provider receives only its own configured password. `ConfigureChatGptLogin.ps1` also prompts for the provider when storing encrypted credentials. Google may reject automated or embedded sign-ins; browser rejection and OAuth errors produce a status message. See [Google supported-browser guidance](https://support.google.com/accounts/answer/7675428?hl=en).

Only **Ctrl + Shift + U** reads a saved login file, opens the login page and starts the selected provider's flow. Without that shortcut, even an existing login file leaves the panel in guest mode. Questions wait for login confirmation. Normal automatic login has a 90-second timeout. Verification, consent, account mismatch or a rejected credential step instead pauses automatic form actions and allows up to ten minutes for the user to finish. Press **Ctrl + Shift + J** and click/type directly in the authentication page to enter an OTP, select another account, or review consent. Phone approvals can be completed on the phone while SEB keeps waiting. Mouse scrolling, Unicode text, Tab, Enter and editing keys are supported. Clipboard shortcuts are not forwarded. Clicking or typing on the authentication page also pauses automatic form actions. Human input is limited to HTTPS authentication pages and is disabled after login completes or the attempt ends. **Tải lại** cancels the active attempt and returns to ChatGPT without reading credentials or restarting authentication. It retains the current in-memory cookies. Press **Ctrl + Shift + U** to retry login. Real account login still needs verification in the target environment. Without a saved login, the existing guest workflow remains available. Delete `.env` and `credentials.dat` to remove the saved account.

The existing request context is retained throughout authentication. Allowed popup URLs navigate within the same panel. CAPTCHA frames are allowed only under the HTTPS `/recaptcha/` paths on `www.google.com` and `www.recaptcha.net`, in addition to the existing Cloudflare domain. Session URL filters continue to apply to every navigation and resource.

Google account navigation, credential steps and human verification share an exact-host allowlist containing `accounts.google.com` and `accounts.google.com.vn`. The Vietnamese domain supports regional redirects during login. Lookalike hosts and HTTP URLs remain blocked.

Login completion also checks for a usable composer on the main ChatGPT content page, so a changed profile-button layout does not leave questions waiting behind a login timeout. Visible login prompts, blocking dialogs, hidden/disabled/read-only editors and authentication pages do not satisfy this check. Before rejecting a later question because of an earlier login failure, SEB rechecks the current page and clears that failure when the composer is ready. This recovery retains the current page, cookies and draft instead of restarting authentication.

With `CHATGPT_LOGIN_PROVIDER=openai`, an email-code screen offering **Continue with password** selects that option once before entering the configured OpenAI password. An email-code screen without that option, or verification requested after password submission, still requires verification. Google credentials do not select the OpenAI password alternative. The embedded navigation allowlist includes the exact HTTPS host `sentinel.openai.com` for authentication frames; original session URL filters still apply. OpenAI's [network guidance](https://help.openai.com/en/articles/9247338-network-recommendations-for-chatgpt-errors-on-web-and-apps) includes OpenAI subdomains among the domains required for ChatGPT.

After building Debug x64 Runtime, Client and Browser unit tests, run `SafeExamBrowser.Browser.UnitTests/Run-ChatGptLoginChecks.ps1` for standalone Chromium and WPF authentication checks. These checks use local HTML fixtures and fake credentials; they do not read `.env` or log in to a real account.

Response extraction supports message-role and turn-role layouts. It finds completion controls within the latest assistant turn even when they are siblings of the message body, and reads the answer without action labels. Explicit streaming indicators and visible stop controls keep the request pending. Without an explicit completion flag, response actions or an idle composer plus four seconds of unchanged text allow completion. The latest assistant message must follow the current user message. The fixture runner also checks extraction, consecutive questions and stable partial answers that are still streaming.

The observed newer layout uses `data-talvt-turn-state` for a paired turn and `data-user-message-bubble` for the prompt, without the older message-role attributes or Markdown container. The reader recognizes the paired turn, removes the user-message groups and action controls from a cloned DOM before reading its answer, and associates a contained prompt with that turn. This also supports direct answer text nodes inside `display: contents` containers. Built-in support for this layout takes precedence over obsolete local debug selectors. Regression fixtures cover two consecutive paired-turn questions, pending turns with only a user prompt, and explicit streaming state.

For the VMware lab, `sites/seb-quiz-lab/Update-SEBTest.ps1` stages a verified local build, preserves the saved login and restarts the test configuration. It opts in to writing `chatgpt-reply-diagnostics.json` to the lab share by creating `ChatGptDiagnostics.target` beside the executable. Diagnostics contain the assembly build ID, marker counts, text length and boolean reader/completion state. Credentials, question text, answer text and conversation identifiers are omitted. Delete the target file to disable the shared diagnostic output; state transitions are also recorded in the existing Client log.

The updater entry point now delegates to `Update-SEBTest-v2.ps1`. It selects the newest manifest under the lab share's `updates` directory and verifies all three Browser, UI Contracts and Desktop DLL hashes; legacy Browser-only packages remain a fallback when no bundle exists. It copies a complete runtime to a fresh `%LOCALAPPDATA%\SEB-Test\Builds\<hash>` directory and verifies the DLL hash before starting it. This avoids executing an older DLL from the HGFS share or reusing a locked installation. The lab updater retains the account locally in `%LOCALAPPDATA%\SEB-Test\login.env` while moving between build directories; remove that file and the build-directory `.env` files when removing test credentials. Versioned update packages and local diagnostic JSON files are excluded from Git.

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
