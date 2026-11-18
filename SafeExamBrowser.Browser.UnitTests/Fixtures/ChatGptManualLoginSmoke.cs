using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.OffScreen;

// Standalone Chromium/WPF integration checks. All pages and credentials are fixtures.
class ChatGptManualLoginSmoke
{
    static int checks;
    static Type assistantType;
    static Type loginType;
    static Type inputType;
    static int credentialReads;

    static T LoadNoCredentials<T>() { credentialReads++; return default(T); }

    [STAThread]
    static int Main(string[] args)
    {
        var runtime = args[0];
        var settings = new CefSettings {
            BrowserSubprocessPath = Path.Combine(runtime, "CefSharp.BrowserSubprocess.exe"),
            RootCachePath = Path.GetFullPath("artifacts/manual-login-smoke-cache"),
            LogSeverity = LogSeverity.Disable
        };
        if (!Cef.Initialize(settings)) return 1;
        var exitCode = 1;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Dispatcher.InvokeAsync(async () => {
            try {
                var assembly = Assembly.LoadFrom(Path.Combine(runtime, "SafeExamBrowser.Browser.dll"));
                assistantType = assembly.GetType("SafeExamBrowser.Browser.ChatGptAssistant");
                loginType = assembly.GetType("SafeExamBrowser.Browser.ChatGptLogin");
                inputType = assembly.GetType("SafeExamBrowser.Browser.ChatGptLoginInput");
                await Run();
                Console.WriteLine("Passed " + checks + " manual login checks.");
                exitCode = 0;
            } catch (Exception error) { Console.WriteLine(error); }
            finally { application.Shutdown(); }
        });
        application.Run();
        Cef.Shutdown();
        return exitCode;
    }

    static async Task Run()
    {
        // Block all navigation/resources; this instance never reads real login files.
        var loaderType = typeof(Func<>).MakeGenericType(loginType);
        var loader = Delegate.CreateDelegate(loaderType, typeof(ChatGptManualLoginSmoke)
            .GetMethod("LoadNoCredentials", BindingFlags.Static | BindingFlags.NonPublic).MakeGenericMethod(loginType));
        var gated = Activator.CreateInstance(assistantType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { new Func<string, bool>(url => false), new Func<string, bool>(url => false), null, loader }, null);
        try {
            Check(credentialReads == 0, "startup does not load credentials or begin authentication");
            assistantType.GetMethod("Reload").Invoke(gated, null);
            Check(credentialReads == 0, "reload does not load credentials or begin authentication");
            assistantType.GetMethod("BeginLogin").Invoke(gated, null);
            Check(credentialReads == 1, "explicit login reads credentials on demand");
            var pending = new TaskCompletionSource<bool>();
            Set(gated, "loginTask", pending.Task);
            assistantType.GetMethod("BeginLogin").Invoke(gated, null);
            Check(credentialReads == 1, "repeated login shortcut keeps the pending attempt");
            pending.SetResult(false);
            assistantType.GetMethod("BeginLogin").Invoke(gated, null);
            Check(credentialReads == 2, "retry shortcut rereads the saved settings");
        } finally { ((IDisposable)gated).Dispose(); }
        assistantType.GetMethod("BeginLogin").Invoke(gated, null);
        Check(credentialReads == 2, "a closed assistant ignores the login shortcut");

        using (var browser = new ChromiumWebBrowser("about:blank")) {
            var preview = new Image();
            preview.Measure(new System.Windows.Size(400, 300));
            preview.Arrange(new Rect(0, 0, 400, 300));
            var enabled = true;
            var interactions = 0;
            var assistant = FormatterServices.GetUninitializedObject(assistantType);
            Set(assistant, "control", browser);
            var lifecycle = new CancellationTokenSource();
            Set(assistant, "loginCancellation", lifecycle);
            Set(assistant, "questionOperation", new SemaphoreSlim(1, 1));
            var statuses = new List<string>();
            var diagnostics = new List<string>();
            Set(assistant, "StatusChanged", new Action<string>(message => statuses.Add(message)));
            Set(assistant, "diagnostic", new Action<string>(message => diagnostics.Add(message)));
            var forwarding = (IDisposable)Activator.CreateInstance(inputType, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { preview, browser, new Func<bool>(() => enabled), new Action(() => { interactions++; Set(assistant, "manualLogin", true); }) }, null);
            try {
                var form = "<div id=fixture></div><form onsubmit='event.preventDefault();window.confirmed=document.querySelector(\"input\").value===\"123456\"'><input name=totpPin autocomplete=one-time-code><button type=submit>Next</button></form>";
                await Load(browser, form, "https://accounts.google.com.vn/v3/signin/challenge/totp");
                using (var cancellation = new CancellationTokenSource()) {
                    var login = Login();
                    var task = Attempt(assistant, login, cancellation.Token);
                    await Task.Delay(800);
                    Check(!task.IsCompleted && statuses.Exists(s => s.Contains("10 phút")), "keep the login attempt active while waiting for verification");
                    browser.GetBrowserHost().SendFocusEvent(true);
                    await Evaluate(browser, "document.querySelector('input').focus()");
                    Text(preview, "123456");
                    await Task.Delay(150);
                    Check(Equals(await Evaluate(browser, "document.querySelector('input').value"), "123456"), "forward typed OTP characters through native browser input");
                    SendKey(preview, Key.Enter);
                    await Task.Delay(150);
                    Check(Equals(await Evaluate(browser, "window.confirmed"), true), "submit verification with Enter");
                    Check(interactions >= 2, "pause auto-fill when the user interacts with authentication");
                    await Load(browser, "<div id=fixture></div><aside><button title='Account menu'>Hi Hi <span>Free</span></button></aside><main><form><div id=prompt-textarea contenteditable=true role=textbox></div></form></main>", "https://chatgpt.com/");
                    Check(await task, "complete the same pending login after returning to ChatGPT");
                    Check(loginType.GetProperty("Password").GetValue(login, null) == null, "clear the configured password after login");
                }

                var chat = "<div id=fixture></div><script>window.fixtureSession='preserved'</script><aside><button title='Account menu'>Hi Hi <span>Free</span></button></aside><main><form onsubmit='event.preventDefault()'><textarea id=prompt-textarea></textarea><button id=composer-submit-button type=button onclick='window.sends=(window.sends||0)+1;var u=document.createElement(\"div\");u.setAttribute(\"data-message-author-role\",\"user\");u.innerText=document.querySelector(\"textarea\").value;document.body.append(u);var a=document.createElement(\"div\");a.setAttribute(\"data-message-author-role\",\"assistant\");a.setAttribute(\"data-message-complete\",\"true\");a.innerText=\"D\";document.body.append(a)'>Send</button></form></main>";
                await Load(browser, chat, "https://chatgpt.com/");
                Set(assistant, "loginFailure", "Chưa xác nhận được đăng nhập ChatGPT sau 90 giây.");
                Set(assistant, "loginTask", Task.FromResult(false));
                var prepare = (Task<bool>)assistantType.GetMethod("PreparePromptAsync").Invoke(assistant, new object[] { "Câu hỏi kiểm tra" });
                Check(await prepare, "recover a stale login timeout when the current composer is usable");
                Check(assistantType.GetField("loginFailure", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(assistant) == null, "clear the stale login failure after checking the current page");
                Check(Equals(await Evaluate(browser, "window.fixtureSession"), "preserved"), "retain the existing signed-in page and session during recovery");
                Check(Equals(await Evaluate(browser, "document.querySelector('textarea').value"), "Câu hỏi kiểm tra"), "prepare the question after recovering the login gate");

                await Load(browser, chat, "https://chatgpt.com/");
                Set(assistant, "loginFailure", "Chưa xác nhận được đăng nhập ChatGPT sau 90 giây.");
                Set(assistant, "loginTask", Task.FromResult(false));
                var sent = 0;
                Set(assistant, "QuestionSent", new Action(() => sent++));
                var askMethod = assistantType.GetMethod("AskQuestionAsync");
                var multipleChoice = Enum.Parse(askMethod.GetParameters()[1].ParameterType, "MultipleChoice");
                var ask = (Task)askMethod.Invoke(assistant, new object[] { "7 × 8 bằng bao nhiêu? A. 48 B. 49 C. 54 D. 56", multipleChoice, CancellationToken.None });
                await ask;
                var answer = ask.GetType().GetProperty("Result").GetValue(ask, null);
                Check(Equals(answer.GetType().GetProperty("IsValid").GetValue(answer, null), true) && Equals(answer.GetType().GetProperty("Text").GetValue(answer, null), "D"), "send and receive a normalized answer after a previous login timeout");
                Check(sent == 1 && Equals(await Evaluate(browser, "window.sends===1"), true), "confirm the recovered question once without duplicate submission");
                Check(diagnostics.Exists(d => d.StartsWith("ReplyReader ") && d.Contains("ReaderSucceeded") && d.Contains("Build")), "record the deployed reader build and completion flags");
                Check(!diagnostics.Exists(d => d.Contains("7 × 8") || d.Contains("fixture-password") || d.Contains("\"Text\":") || d.Contains("\"UserText\":")), "keep credentials, questions and answer text out of reader diagnostics");

                var finishedChat = chat.Replace("a.setAttribute(\"data-message-complete\",\"true\");", "");
                finishedChat = finishedChat.Replace("document.body.append(a)", "var t=document.createElement(\"div\");t.className=\"turn\";t.append(a);var c=document.createElement(\"button\");c.setAttribute(\"aria-label\",\"Copy response\");c.innerText=\"Copy\";t.append(c);document.body.append(t)");
                await Load(browser, finishedChat, "https://chatgpt.com/");
                var finished = (Task)askMethod.Invoke(assistant, new object[] { "7 × 8 bằng bao nhiêu? A. 48 B. 49 C. 54 D. 56", multipleChoice, CancellationToken.None });
                var completed = await Task.WhenAny(finished, Task.Delay(12000));
                Check(completed == finished, "finish waiting for an answer with a sibling copy button and no completion attribute");
                await finished;
                var finishedAnswer = finished.GetType().GetProperty("Result").GetValue(finished, null);
                Check(Equals(finishedAnswer.GetType().GetProperty("IsValid").GetValue(finishedAnswer, null), true) && Equals(finishedAnswer.GetType().GetProperty("Text").GetValue(finishedAnswer, null), "D"), "normalize only the answer text without sibling action labels");
                var legacyChat = finishedChat.Replace("a.setAttribute(\"data-message-author-role\",\"assistant\")", "a.setAttribute(\"data-message-role\",\"assistant\")");
                await Load(browser, legacyChat, "https://chatgpt.com/");
                var legacy = (Task)askMethod.Invoke(assistant, new object[] { "7 × 8 bằng bao nhiêu? A. 48 B. 49 C. 54 D. 56", multipleChoice, CancellationToken.None });
                Check(await Task.WhenAny(legacy, Task.Delay(12000)) == legacy, "release the next request after completing a legacy response without a completion flag");
                await legacy;

                var streamingChat = chat.Replace("a.setAttribute(\"data-message-complete\",\"true\");", "");
                streamingChat = streamingChat.Replace("a.innerText=\"D\";document.body.append(a)", "a.innerText=\"A\";document.body.append(a);var stop=document.createElement(\"button\");stop.setAttribute(\"data-testid\",\"stop-button\");document.body.append(stop);setTimeout(function(){a.innerText=\"D\";stop.remove();a.setAttribute(\"data-is-streaming\",\"false\")},6000)");
                await Load(browser, streamingChat, "https://chatgpt.com/");
                var streaming = (Task)askMethod.Invoke(assistant, new object[] { "7 × 8 bằng bao nhiêu? A. 48 B. 49 C. 54 D. 56", multipleChoice, CancellationToken.None });
                await Task.Delay(5000);
                Check(!streaming.IsCompleted, "keep waiting during a stable partial answer while the stop control remains");
                Check(await Task.WhenAny(streaming, Task.Delay(10000)) == streaming, "finish after the active stream actually ends");
                await streaming;
                var streamedAnswer = streaming.GetType().GetProperty("Result").GetValue(streaming, null);
                Check(Equals(streamedAnswer.GetType().GetProperty("Text").GetValue(streamedAnswer, null), "D"), "return the final answer instead of the stable partial answer");

                var pairedChat = @"<div id=fixture></div><main><form onsubmit='event.preventDefault()'><textarea id=prompt-textarea></textarea><button id=composer-submit-button type=button onclick='submitPairedFixture()'>Send</button></form></main><script>
                  function submitPairedFixture(){
                    const turn=document.createElement('div');turn.setAttribute('data-talvt-turn-state','');
                    const contents=document.createElement('div');contents.style.display='contents';
                    const userGroup=document.createElement('div');userGroup.className='group/user-message';
                    const user=document.createElement('div');user.setAttribute('data-user-message-bubble','');user.innerText=document.querySelector('textarea').value;
                    userGroup.append(user);contents.append(userGroup,document.createTextNode('D'));turn.append(contents);
                    const actions=document.createElement('div');actions.className='turn-action-controls';
                    const copy=document.createElement('button');copy.setAttribute('aria-label','Copy');copy.innerText='Copy';actions.append(copy);turn.append(actions);
                    document.body.append(turn);document.querySelector('textarea').value='';
                  }
                </script>";
                await Load(browser, pairedChat, "https://chatgpt.com/");
                for (var turnNumber=1;turnNumber<=2;turnNumber++) {
                    var paired = (Task)askMethod.Invoke(assistant, new object[] { "7 × 8 bằng bao nhiêu? A. 48 B. 49 C. 54 D. 56", multipleChoice, CancellationToken.None });
                    Check(await Task.WhenAny(paired,Task.Delay(12000))==paired, "complete observed paired-layout question " + turnNumber + " without legacy role attributes");
                    await paired;
                    var pairedAnswer=paired.GetType().GetProperty("Result").GetValue(paired,null);
                    Check(Equals(pairedAnswer.GetType().GetProperty("IsValid").GetValue(pairedAnswer,null),true) && Equals(pairedAnswer.GetType().GetProperty("Text").GetValue(pairedAnswer,null),"D"), "exclude the question and action labels from paired-layout answer " + turnNumber);
                }

                await Load(browser, "<div id=fixture></div><textarea id=prompt-textarea disabled></textarea>", "https://chatgpt.com/");
                Set(assistant, "loginFailure", "Fixture login timeout");
                Set(assistant, "loginTask", Task.FromResult(false));
                Check(!await (Task<bool>)assistantType.GetMethod("PreparePromptAsync").Invoke(assistant, new object[] { "Must not send" }), "keep the login failure when the composer is disabled");

                var input = "<div id=fixture></div><input name=totpPin>";
                await Load(browser, input, "https://accounts.google.com/v3/signin/challenge/totp");
                browser.GetBrowserHost().SendFocusEvent(true);
                await Evaluate(browser, "document.querySelector('input').focus()");
                Text(preview, "Aé");
                await Task.Delay(250);
                var unicodeValue = await Evaluate(browser, "document.querySelector('input').value");
                Check(Equals(unicodeValue, "Aé"), "preserve Unicode through authentication input");
                SendKey(preview, Key.Back);
                await Task.Delay(100);
                Check(Equals(await Evaluate(browser, "document.querySelector('input').value"), "A"), "forward editing keys to the authentication field");
                enabled = false;
                var before = interactions;
                Text(preview, "SHOULD_NOT_APPEAR");
                await Task.Delay(100);
                Check(Equals(await Evaluate(browser, "document.querySelector('input').value"), "A") && interactions == before, "disable native input after a login attempt ends");
                enabled = true;
                await Load(browser, input, "https://chatgpt.com/");
                browser.GetBrowserHost().SendFocusEvent(true);
                await Evaluate(browser, "document.querySelector('input').focus()");
                Text(preview, "SHOULD_NOT_APPEAR");
                await Task.Delay(100);
                Check(Equals(await Evaluate(browser, "document.querySelector('input').value"), ""), "keep ordinary ChatGPT pages outside authentication input scope");

                Set(assistant, "manualLogin", false);
                await Load(browser, input, "https://accounts.google.com/v3/signin/challenge/totp");
                using (var cancellation = new CancellationTokenSource()) {
                    var task = Attempt(assistant, Login(), cancellation.Token);
                    await Task.Delay(600);
                    cancellation.Cancel();
                    Check(!await task, "cancel an authentication attempt while waiting for human verification");
                }
                Set(assistant, "manualLogin", false);
                await Load(browser, "<div id=fixture></div>", "https://accounts.google.com/v3/signin/rejected");
                Check(!await Attempt(assistant, Login(), CancellationToken.None), "stop clearly when Google rejects the embedded browser");
                Check(statuses.Exists(s => s.Contains("Google từ chối trình duyệt")), "report browser rejection without a generic timeout");
            } finally { forwarding.Dispose(); lifecycle.Cancel(); lifecycle.Dispose(); }
            await Load(browser, "<div id=fixture></div><input>", "https://accounts.google.com/v3/signin/identifier");
            browser.GetBrowserHost().SendFocusEvent(true);
            await Evaluate(browser, "document.querySelector('input').focus()");
            Text(preview, "SHOULD_NOT_APPEAR");
            await Task.Delay(100);
            Check(Equals(await Evaluate(browser, "document.querySelector('input').value"), ""), "detach authentication input when disposed");
        }
    }

    static object Login()
    {
        var login = Activator.CreateInstance(loginType, true);
        loginType.GetProperty("Email").SetValue(login, "test@example.invalid", null);
        loginType.GetProperty("Password").SetValue(login, "fixture-password", null);
        loginType.GetProperty("Provider").SetValue(login, "google", null);
        return login;
    }

    static Task<bool> Attempt(object assistant, object login, CancellationToken token)
    {
        return (Task<bool>)assistantType.GetMethod("LogInAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(assistant, new object[] { login, token });
    }

    static void Set(object target, string field, object value)
    {
        assistantType.GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    }

    static void Text(Image preview, string text)
    {
        var args = new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, preview, text)) {
            RoutedEvent = TextCompositionManager.PreviewTextInputEvent
        };
        preview.RaiseEvent(args);
    }

    static void SendKey(Image preview, System.Windows.Input.Key key)
    {
        var source = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters("Authentication fixture") { Width = 1, Height = 1 });
        try {
            preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            preview.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        } finally { source.Dispose(); }
    }

    static async Task Load(ChromiumWebBrowser browser, string html, string url)
    {
        while (!browser.IsBrowserInitialized) await Task.Delay(50);
        browser.LoadHtml(html, url);
        for (var attempt = 0; attempt < 100; attempt++) {
            await Task.Delay(50);
            if (!browser.IsLoading && browser.Address == url && Equals(await Evaluate(browser, "!!document.getElementById('fixture')"), true)) return;
        }
        throw new Exception("Fixture failed to load.");
    }

    static async Task<object> Evaluate(ChromiumWebBrowser browser, string script)
    {
        using (var frame = browser.GetMainFrame()) {
            var response = await frame.EvaluateScriptAsync(script, timeout: TimeSpan.FromSeconds(3));
            if (!response.Success) throw new Exception("Fixture script failed.");
            return response.Result;
        }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
}
