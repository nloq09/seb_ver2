using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;

class ChatGptLoginSmoke
{
    static Type loginType;
    static int checks;

    static int Main(string[] args)
    {
        var runtime = args[0];
        var settings = new CefSettings {
            BrowserSubprocessPath = Path.Combine(runtime, "CefSharp.BrowserSubprocess.exe"),
            RootCachePath = Path.GetFullPath("artifacts/login-smoke-cache"),
            LogSeverity = LogSeverity.Disable
        };
        if (!Cef.Initialize(settings)) return 1;
        try {
            loginType = Assembly.LoadFrom(Path.Combine(runtime, "SafeExamBrowser.Browser.dll")).GetType("SafeExamBrowser.Browser.ChatGptLogin");
            Run().GetAwaiter().GetResult();
            Console.WriteLine("Passed " + checks + " login form checks.");
            return 0;
        } catch (Exception error) { Console.WriteLine(error); return 1; }
        finally { Cef.Shutdown(); }
    }

    static async Task Run()
    {
        var login = Activator.CreateInstance(loginType, true);
        var password = "fixture\"'\\Tiếng Việt";
        loginType.GetProperty("Email").SetValue(login, "test@example.invalid", null);
        loginType.GetProperty("Password").SetValue(login, password, null);
        var method = loginType.GetMethod("BuildFormScript", BindingFlags.NonPublic | BindingFlags.Instance);
        var gatewayMethod = loginType.GetMethod("BuildGatewayScript", BindingFlags.NonPublic | BindingFlags.Instance);
        var gateway = (string)gatewayMethod.Invoke(login, new object[] { false });
        var gatewayDone = (string)gatewayMethod.Invoke(login, new object[] { true });
        var initial = (string)method.Invoke(login, new object[] { false, false });
        var emailDone = (string)method.Invoke(login, new object[] { true, false });
        var allDone = (string)method.Invoke(login, new object[] { true, true });
        var form = "<div id=fixture></div><form method=post onsubmit=\"event.preventDefault();window.sends=(window.sends||0)+1\"><input name=username type=email oninput=\"document.querySelector('button').disabled=false\"><button type=submit disabled>Continue</button></form><div role=alert></div>";
        using (var browser = new ChromiumWebBrowser("about:blank")) {
            var assistantType = loginType.Assembly.GetType("SafeExamBrowser.Browser.ChatGptAssistant");
            var readyScript = (string)assistantType.GetMethod("BuildChatReadyScript", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            var readyPage = "<div id=fixture></div><aside><button title='Account menu'>Hi Hi <span>Free</span></button></aside><main><form><div id=prompt-textarea contenteditable=true role=textbox></div></form></main>";
            await Load(browser, readyPage, "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), true, "recognize the usable signed-in layout without legacy profile test IDs");
            await Load(browser, "<div id=fixture></div><textarea id=mobile-composer-prompt></textarea>", "https://chatgpt.com/c/fixture");
            Check(await Evaluate(browser, readyScript), true, "recognize a usable mobile composer on an existing conversation");
            await Load(browser, readyPage.Replace("contenteditable=true", "contenteditable=false"), "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "reject a composer that is not editable");
            await Load(browser, "<div id=fixture></div><textarea id=prompt-textarea disabled></textarea>", "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "reject a disabled composer");
            await Load(browser, "<div id=fixture></div><textarea id=prompt-textarea readonly></textarea>", "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "reject a read-only composer");
            await Load(browser, readyPage.Replace("<main>", "<main style='display:none'>"), "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "reject a hidden composer");
            await Load(browser, readyPage.Replace("<main>", "<main inert>"), "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "reject an inert composer");
            await Load(browser, readyPage + "<button data-testid=login-button>Log in</button>", "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "keep waiting when a visible login requirement remains");
            await Load(browser, readyPage + "<div role=dialog aria-modal=true>Complete verification</div>", "https://chatgpt.com/");
            Check(await Evaluate(browser, readyScript), false, "keep waiting for a modal that blocks the composer");
            await Load(browser, readyPage, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, readyScript), false, "never treat an authentication page as the chat editor");
            await Load(browser, readyPage, "https://chatgpt.com.example.invalid/");
            Check(await Evaluate(browser, readyScript), false, "never accept an editor on a lookalike ChatGPT origin");
            var gatewayForm = form.Replace("method=post", "method=get") + "<button onclick='window.social=true'>Continue with Google</button><button onclick='window.consent=true'>Accept all</button>";
            await Load(browser, gatewayForm, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, gateway), "email", "submit the email on the actual ChatGPT login gateway layout");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "test@example.invalid", "fill the gateway email");
            Check(await Evaluate(browser, gatewayDone), "waiting", "never repeat the gateway submission");
            Check(await Evaluate(browser, "window.sends===1 && !window.social && !window.consent"), true, "leave social login and cookie buttons untouched");
            Check(gateway.Contains(password), false, "keep the password out of the gateway script");
            var mixedForm = "<div id=fixture></div><form method=post onsubmit='event.preventDefault();window.sends=(window.sends||0)+1'><input type=email name=username><button type=submit onclick='window.social=true'>Continue with Google</button><button type=submit>Continue</button></form>";
            await Load(browser, mixedForm, "https://auth.openai.com/log-in");
            Check(await Evaluate(browser, initial), "email", "choose email continuation when a social submit appears first");
            Check(await Evaluate(browser, "window.sends===1 && !window.social"), true, "never select Google in the email form");
            await Load(browser, mixedForm.Replace("method=post", "method=get"), "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, gateway), "email", "choose the email continuation on a mixed gateway form");
            Check(await Evaluate(browser, "window.sends===1 && !window.social"), true, "never select Google on a mixed gateway form");
            await Load(browser, mixedForm.Replace("<button type=submit>Continue</button>", ""), "https://auth.openai.com/log-in");
            Check(await Evaluate(browser, initial), "waiting", "wait when only social login is offered");
            Check(await Evaluate(browser, "window.sends===undefined && !window.social"), true, "do not submit a social-only form");
            await Load(browser, gatewayForm, "https://chatgpt.com.example.invalid/auth/login");
            Check(await Evaluate(browser, gateway), "blocked", "block lookalike gateway origins");
            await Load(browser, gatewayForm.Replace("<form ", "<form action='https://example.invalid/' "), "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, gateway), "blocked", "block external gateway form actions");
            await Load(browser, "<div id=fixture></div><button data-testid=profile-button>Profile</button>", "https://chatgpt.com/");
            Check(await Evaluate(browser, gateway), "signed-in", "recognize the signed-in profile");
            await Load(browser, "<div id=fixture></div><input autocomplete=one-time-code>", "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, gateway), "verification", "stop for gateway verification");
            await Load(browser, form, "https://auth.openai.com/log-in");
            Check(await Evaluate(browser, initial), "email", "fill and submit email after enabling the submit button");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "test@example.invalid", "preserve the exact email");
            Check(await Evaluate(browser, emailDone), "waiting", "never repeat email submission");
            Check(await Evaluate(browser, "window.sends === 1"), true, "submit the email once");

            var passwordForm = "<div id=fixture></div><form method=post onsubmit=\"event.preventDefault();window.sends=(window.sends||0)+1\"><input type=password autocomplete=current-password><button type=submit>Continue</button></form>";
            await Load(browser, passwordForm, "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, emailDone), "password", "submit the password step");
            Check(await Evaluate(browser, "document.querySelector('input').value"), password, "preserve special characters in a password");
            Check(await Evaluate(browser, allDone), "waiting", "never repeat password submission");
            Check(await Evaluate(browser, "window.sends === 1"), true, "submit the password once");

            loginType.GetProperty("Password").SetValue(login, "line1\nline2", null);
            var invalidPassword = (string)method.Invoke(login, new object[] { true, false });
            await Load(browser, passwordForm, "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, invalidPassword), "rejected", "reject passwords that a browser field cannot preserve");
            Check(await Evaluate(browser, "window.sends === undefined"), true, "do not submit an altered password");

            await Load(browser, passwordForm, "https://auth.openai.com.example.invalid/log-in");
            Check(await Evaluate(browser, initial), "blocked", "reject a lookalike origin");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "keep credentials out of other origins");

            await Load(browser, passwordForm.Replace("<form ", "<form action='https://example.invalid/' "), "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, initial), "waiting", "reject a form submitting to another origin");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "do not fill an external form");

            await Load(browser, passwordForm.Replace("method=post", "method=get"), "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, initial), "blocked", "do not submit credentials in a URL");

            await Load(browser, passwordForm.Replace("current-password", "new-password"), "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, initial), "blocked", "do not enter a password into account creation");
            await Load(browser, passwordForm, "https://auth.openai.com/reset-password");
            Check(await Evaluate(browser, initial), "blocked", "do not reset a password");
            await Load(browser, "<div id=fixture></div><input autocomplete=one-time-code>", "https://auth.openai.com/log-in/otp");
            Check(await Evaluate(browser, initial), "verification", "stop for verification");
            var passwordOption = "<div id=fixture></div><input name=code autocomplete=one-time-code><button type=button onclick='window.choices=(window.choices||0)+1'>Continue with password</button>";
            await Load(browser, passwordOption, "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "password-option", "select the offered OpenAI password alternative before stopping for an email code");
            Check(await Evaluate(browser, initial), "waiting", "wait for password navigation without clicking the alternative twice");
            Check(await Evaluate(browser, "window.choices===1 && document.querySelector('input').value===''"), true, "choose the password method once and leave the code untouched");
            await Load(browser, passwordOption, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, gateway), "password-option", "select the password alternative on the login gateway");
            Check(await Evaluate(browser, gateway), "waiting", "avoid duplicate gateway password selection");
            await Load(browser, passwordOption, "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, allDone), "verification", "retain verification when it is requested after a password was submitted");
            Check(await Evaluate(browser, "window.choices===undefined"), true, "never restart the password step to handle subsequent verification");
            await Load(browser, passwordOption.Replace("<button type=button", "<button disabled type=button"), "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "verification", "do not click a disabled password alternative");
            await Load(browser, passwordOption.Replace("<button type=button", "<button aria-disabled=true type=button"), "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "verification", "respect an aria-disabled password alternative");
            await Load(browser, passwordOption.Replace("<button type=button", "<button style='display:none' type=button"), "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "verification", "do not click a hidden password alternative");
            await Load(browser, passwordOption.Replace("Continue with password", "Continue with Google"), "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "verification", "do not confuse social login with the password alternative");
            await Load(browser, "<div id=fixture></div><input name=code><a href='https://example.invalid/'>Continue with password</a>", "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, initial), "blocked", "reject an external password alternative link");
            await Load(browser, passwordOption, "https://auth.openai.com.example.invalid/email-verification");
            Check(await Evaluate(browser, initial), "blocked", "do not click password alternatives on lookalike hosts");
            await Load(browser, "<div id=fixture></div><div role=alert>Wrong password</div>" + passwordForm, "https://auth.openai.com/log-in/password");
            Check(await Evaluate(browser, initial), "rejected", "stop after a rejected login");

            loginType.GetProperty("Provider").SetValue(login, "google", null);
            loginType.GetProperty("Password").SetValue(login, password, null);
            var googleMethod = loginType.GetMethod("BuildGoogleScript", BindingFlags.NonPublic | BindingFlags.Instance);
            var googleInitial = (string)googleMethod.Invoke(login, new object[] { false, false });
            var googleEmailDone = (string)googleMethod.Invoke(login, new object[] { true, false });
            var googleAllDone = (string)googleMethod.Invoke(login, new object[] { true, true });
            var googleGateway = (string)gatewayMethod.Invoke(login, new object[] { false });
            var googleAuth = (string)method.Invoke(login, new object[] { false, false });
            var socialForm = "<div id=fixture></div><form method=post onsubmit='event.preventDefault();window.emailSent=true'><input type=email><button type=submit>Continue</button></form><button type=button onclick='window.social=(window.social||0)+1'>Continue with Google</button>";
            await Load(browser, socialForm, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, googleGateway), "google-option", "select Google directly without submitting the OpenAI email form");
            Check(await Evaluate(browser, googleGateway), "waiting", "select Google once on the gateway");
            Check(await Evaluate(browser, "window.social===1 && !window.emailSent && document.querySelector('input').value===''"), true, "keep Google login separate from OpenAI email submission");
            await Load(browser, socialForm, "https://auth.openai.com/log-in-or-create-account");
            Check(await Evaluate(browser, googleAuth), "google-option", "select Google on the OpenAI authentication page");
            Check(await Evaluate(browser, googleAuth), "waiting", "select Google once on the authentication page");
            var challenge = "<iframe src='https://challenges.cloudflare.com/fixture' srcdoc='<p>Fixture verification</p>'></iframe>";
            await Load(browser, socialForm + challenge, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, googleGateway), "verification", "wait for gateway verification before selecting Google");
            await Load(browser, socialForm + challenge, "https://auth.openai.com/log-in");
            Check(await Evaluate(browser, googleAuth), "verification", "wait for authentication verification before selecting Google");
            await Load(browser, socialForm.Replace("<button type=button", "<button disabled type=button"), "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, googleGateway), "google-unavailable", "wait for a disabled Google button to become available");
            Check(await Evaluate(browser, "!window.social && !window.emailSent"), true, "do not fall back to email submission for a Google account");
            await Load(browser, "<div id=fixture></div><a href='https://example.invalid/'>Continue with Google</a>", "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, googleGateway), "blocked", "block a social button targeting an external site");
            await Load(browser, socialForm, "https://chatgpt.com.example.invalid/auth/login");
            Check(await Evaluate(browser, googleGateway), "blocked", "do not select Google on a lookalike gateway");
            var chooser = "<div id=fixture></div><div data-identifier='other@example.invalid' onclick='window.other=true'>Other account</div><div data-identifier='TEST@example.invalid' onclick='window.chosen=(window.chosen||0)+1'>Configured account</div>";
            await Load(browser, chooser, "https://accounts.google.com/v3/signin/accountchooser");
            Check(await Evaluate(browser, googleInitial), "account", "select the matching Google account case insensitively");
            Check(await Evaluate(browser, googleInitial), "waiting", "do not repeat an account selection");
            Check(await Evaluate(browser, "window.chosen===1 && !window.other"), true, "leave other Google accounts untouched");
            await Load(browser, chooser.Replace("TEST@example.invalid", "different@example.invalid"), "https://accounts.google.com/v3/signin/accountchooser");
            Check(await Evaluate(browser, googleInitial), "choose-account", "request a human account choice when no account matches");
            var googleEmail = "<div id=fixture></div><input id=identifierId name=identifier type=email value='old@example.invalid'><div id=identifierNext><button onclick='window.sends=(window.sends||0)+1'>Next</button></div>";
            await Load(browser, googleEmail, "https://accounts.google.com/v3/signin/identifier");
            Check(await Evaluate(browser, googleInitial), "email", "submit the configured Google email");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "test@example.invalid", "replace a prefilled Google email with the configured account");
            Check(await Evaluate(browser, googleEmailDone), "waiting", "never repeat the Google email step");
            Check(await Evaluate(browser, "window.sends === 1"), true, "click Google Next once");

            var googlePassword = "<div id=fixture></div><div id=profileIdentifier>test@example.invalid</div><input name=Passwd type=password autocomplete=current-password><div id=passwordNext><button onclick='window.sends=(window.sends||0)+1'>Next</button></div>";
            await Load(browser, googleEmail, "https://accounts.google.com.vn/v3/signin/identifier");
            Check(await Evaluate(browser, googleInitial), "email", "submit Google email on the exact Vietnamese account origin");
            await Load(browser, googlePassword, "https://accounts.google.com.vn/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, googleEmailDone), "password", "submit the matched Google password on the Vietnamese origin");
            Check(await Evaluate(browser, "document.querySelector('input').value"), password, "preserve the password on the Vietnamese Google origin");
            await Load(browser, googlePassword, "https://accounts.google.com.vn.example.invalid/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, googleEmailDone), "blocked", "block lookalikes of the Vietnamese Google origin");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "keep the password out of a Vietnamese-origin lookalike");
            await Load(browser, "<div id=fixture></div><input name=totpPin autocomplete=one-time-code>", "https://accounts.google.com.vn/v3/signin/challenge/totp");
            Check(await Evaluate(browser, googleAllDone), "verification", "retain verification on the Vietnamese Google origin");
            await Load(browser, "<div id=fixture></div><a href='https://accounts.google.com.vn/' onclick='event.preventDefault();window.chosen=true'>Continue with Google</a>", "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, googleGateway), "google-option", "allow an exact Vietnamese Google target for social login");
            await Load(browser, googlePassword, "https://accounts.google.com/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, googleEmailDone), "password", "submit the Google password for the matched account");
            Check(await Evaluate(browser, "document.querySelector('input').value"), password, "preserve the Google password");
            Check(await Evaluate(browser, googleAllDone), "waiting", "never repeat the Google password step");
            Check(await Evaluate(browser, "window.sends === 1"), true, "submit the Google password once");

            await Load(browser, googlePassword.Replace("test@example.invalid", "different@example.invalid"), "https://accounts.google.com/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, googleEmailDone), "account-mismatch", "stop when Google shows a different account");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "keep the password out of a mismatched account");
            await Load(browser, googlePassword, "https://accounts.google.com.example.invalid/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, googleEmailDone), "blocked", "block a lookalike Google origin");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "do not fill a lookalike Google page");
            await Load(browser, "<div id=fixture></div><input name=totpPin autocomplete=one-time-code>", "https://accounts.google.com/v3/signin/challenge/totp");
            Check(await Evaluate(browser, googleAllDone), "verification", "stop for Google two-step verification");
            await Load(browser, "<div id=fixture></div>", "https://accounts.google.com/v3/signin/rejected");
            Check(await Evaluate(browser, googleInitial), "unsupported-browser", "report Google browser rejection");
            await Load(browser, googlePassword, "https://accounts.google.com/signin/recovery");
            Check(await Evaluate(browser, googleInitial), "blocked", "avoid changing Google account recovery settings");
            await Load(browser, passwordForm, "https://auth.openai.com/log-in/password");
            var wrongProvider = (string)method.Invoke(login, new object[] { true, false });
            Check(await Evaluate(browser, wrongProvider), "google-unavailable", "keep a Google password out of the OpenAI password form");
            Check(await Evaluate(browser, "document.querySelector('input').value"), "", "leave the other provider's password field empty");
            await Load(browser, passwordOption, "https://auth.openai.com/email-verification");
            Check(await Evaluate(browser, wrongProvider), "google-unavailable", "do not select an OpenAI password alternative with Google credentials");
            Check(await Evaluate(browser, "window.choices===undefined && document.querySelector('input').value===''"), true, "leave the OTP and password alternative untouched for Google credentials");
            await Load(browser, "<div id=fixture></div><button onclick='window.consent=true'>Continue</button>", "https://accounts.google.com/signin/oauth/consent");
            Check(await Evaluate(browser, googleAllDone), "verification", "leave Google consent for the user to review");
            Check(await Evaluate(browser, "!window.consent"), true, "never automatically grant OAuth consent");
            loginType.GetProperty("Manual", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(login, true, null);
            var manualGoogle = (string)googleMethod.Invoke(login, new object[] { false, false });
            var manualGateway = (string)gatewayMethod.Invoke(login, new object[] { false });
            var manualAuth = (string)method.Invoke(login, new object[] { false, false });
            await Load(browser, googlePassword, "https://accounts.google.com/v3/signin/challenge/pwd");
            Check(await Evaluate(browser, manualGoogle), "waiting", "pause automatic Google credentials during human interaction");
            Check(await Evaluate(browser, "document.querySelector('input').value==='' && window.sends===undefined"), true, "never overwrite a manually entered Google password");
            await Load(browser, socialForm, "https://chatgpt.com/auth/login");
            Check(await Evaluate(browser, manualGateway), "waiting", "pause social selection during human interaction");
            await Load(browser, socialForm, "https://auth.openai.com/log-in");
            Check(await Evaluate(browser, manualAuth), "waiting", "pause authentication selection during human interaction");
            await Load(browser, "<div id=fixture></div><button data-testid=profile-button>Profile</button>", "https://chatgpt.com/");
            Check(await Evaluate(browser, manualGateway), "signed-in", "recognize successful login after manual verification");
            await Load(browser, "<div id=fixture></div>", "https://accounts.google.com/v3/signin/rejected");
            Check(await Evaluate(browser, manualGoogle), "unsupported-browser", "retain browser rejection diagnostics during manual login");
            await Load(browser, "<div id=fixture>This browser or app may not be secure</div>", "https://accounts.google.com/v3/signin/identifier");
            Check(await Evaluate(browser, manualGoogle), "unsupported-browser", "detect a browser rejection shown within the sign-in page");
            await Load(browser, "<div id=fixture></div>", "https://accounts.google.com/signin/oauth/error");
            Check(await Evaluate(browser, manualGoogle), "provider-error", "report Google OAuth errors instead of waiting for a timeout");
        }
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

    static void Check(object actual, object expected, string name)
    {
        if (!Equals(actual, expected)) throw new Exception("FAIL: " + name);
        checks++;
        Console.WriteLine("PASS: " + name);
    }
}
