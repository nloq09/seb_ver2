using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.OffScreen;

// Tests response extraction against local HTML only, without a ChatGPT account.
class ChatGptResponseSmoke
{
    static string script;
    static Type readerType;
    static int checks;

    static int Main(string[] args)
    {
        var settings = new CefSettings {
            BrowserSubprocessPath = Path.Combine(args[0], "CefSharp.BrowserSubprocess.exe"),
            RootCachePath = Path.GetFullPath("artifacts/response-smoke-cache"),
            LogSeverity = LogSeverity.Disable
        };
        if (!Cef.Initialize(settings)) return 1;
        try {
            var type = Assembly.LoadFrom(Path.Combine(args[0], "SafeExamBrowser.Browser.dll")).GetType("SafeExamBrowser.Browser.ChatGptAssistant");
            readerType = type;
            script = (string)type.GetMethod("BuildConversationScript", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Run().GetAwaiter().GetResult();
            Console.WriteLine("Passed " + checks + " response extraction checks.");
            return 0;
        } catch (Exception error) { Console.WriteLine(error); return 1; }
        finally { Cef.Shutdown(); }
    }

    static async Task Run()
    {
        using (var browser = new ChromiumWebBrowser("about:blank")) {
            var user = "<div data-message-author-role=user>Current question</div>";
            var turn = "<div class=turn><div data-message-author-role=assistant data-message-id=latest><div class=markdown>B</div></div><div class=actions><button aria-label='Copy response'>Copy</button><button aria-label='Good response'>Good</button></div></div>";
            await Load(browser, user + turn);
            Check(await Snapshot(browser, "s.text"), "B", "read only the answer without action labels");
            Check(await Snapshot(browser, "s.canInferComplete && !s.busy && !s.complete"), true, "recognize response actions outside the message without an explicit completion flag");
            Check(await Snapshot(browser, "s.afterUser"), true, "associate the answer with the latest user turn");
            await Load(browser, user + turn.Replace("data-message-author-role=assistant", "data-message-role=assistant"));
            Check(await Snapshot(browser, "!s.busy && s.canInferComplete"), true, "do not assume a legacy role is streaming because a completion attribute is missing");
            await Load(browser, user + turn.Replace("data-message-author-role=assistant", "data-turn=assistant"));
            Check(await Snapshot(browser, "s.count===1 && s.text==='B' && s.canInferComplete"), true, "read assistant turns identified only by data-turn");
            await Load(browser, user + "<article data-turn=assistant data-turn-id=turn-id><div data-message-author-role=assistant><div class=markdown>B</div></div><button aria-label=Copy>Copy</button></article>");
            Check(await Snapshot(browser, "s.count===1 && s.id==='turn-id' && s.text==='B'"), true, "deduplicate nested turn and message markers");
            await Load(browser, user + turn + "<button data-testid=stop-button>Stop</button>");
            Check(await Snapshot(browser, "s.busy"), true, "keep waiting when a visible stop button remains");
            await Load(browser, user + turn + "<button data-testid=stop-button style='display:none'>Stop</button>");
            Check(await Snapshot(browser, "s.busy"), false, "ignore hidden stop controls");
            await Load(browser, user + turn.Replace("data-message-id=latest", "data-message-id=latest data-is-streaming=true"));
            Check(await Snapshot(browser, "s.busy"), true, "detect streaming on the message itself");
            await Load(browser, user + turn.Replace("class=turn", "class=turn aria-busy=true"));
            Check(await Snapshot(browser, "s.busy"), true, "detect busy state on the surrounding turn");
            await Load(browser, user + turn.Replace("data-message-id=latest", "data-message-id=latest data-message-complete=false"));
            Check(await Snapshot(browser, "s.complete"), false, "do not treat a false completion attribute as complete");
            await Load(browser, user + turn.Replace("data-message-id=latest", "data-message-id=latest data-message-complete=true"));
            Check(await Snapshot(browser, "s.complete"), true, "retain explicit completion support");
            await Load(browser, user + turn + "<div data-message-author-role=user>Next question</div>");
            Check(await Snapshot(browser, "s.afterUser || s.canInferComplete"), false, "never use an older reply after a new user question");
            await Load(browser, user + turn + "<div data-message-author-role=user>Next question</div><div class=turn><div data-message-author-role=assistant data-message-id=newest><div class=markdown>C</div></div></div>");
            Check(await Snapshot(browser, "s.canInferComplete"), false, "do not reuse an earlier answer's copy button for the latest unfinished turn");
            var composer = "<form><textarea id=prompt-textarea></textarea><button data-testid=composer-speech-button>Voice</button></form>";
            var bare = "<div data-message-author-role=assistant><div class=markdown>B</div></div>";
            await Load(browser, user + bare + composer);
            Check(await Snapshot(browser, "s.canInferComplete && !s.busy"), true, "recognize an idle voice composer after a stable answer");
            await Load(browser, user + bare + composer.Replace("data-testid=composer-speech-button", "data-testid=stop-button"));
            Check(await Snapshot(browser, "s.busy"), true, "retain streaming state while the composer shows stop");
            await Load(browser, user + "<div data-message-author-role=assistant style='display:none'>Old hidden answer</div>" + turn);
            Check(await Snapshot(browser, "s.count===1 && s.text==='B'"), true, "ignore hidden message templates");
            await Load(browser, user + turn, "https://chatgpt.com/auth/login");
            Check(await Snapshot(browser, "Object.keys(s).length===0"), true, "exclude authentication pages from answer extraction");
            await Load(browser, user + turn, "https://chatgpt.com.example.invalid/");
            Check(await Snapshot(browser, "Object.keys(s).length===0"), true, "exclude lookalike origins from answer extraction");
            var defaultScript = script;
            var rules = new Dictionary<string, string> { {"AssistantSelector", ".fixture-assistant"}, {"UserSelector", ".fixture-user"}, {"ContentSelector", ".fixture-body"}, {"ActionSelector", "button[title=Copy]"} };
            script = (string)readerType.GetMethod("BuildConversationScriptWithSelectors", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { rules, true });
            await Load(browser, "<div class=fixture-user>Private fixture question</div><div class=turn><div class=fixture-assistant><div class=fixture-body>B</div></div><button title=Copy>Copy</button></div>");
            Check(await Snapshot(browser, "s.count===1 && s.text==='B' && s.afterUser && s.canInferComplete && s.customSelectors"), true, "apply CSS selector rules to a layout without message-role attributes");
            Check(await Snapshot(browser, "s.structure && Array.isArray(s.structure.buttons)"), true, "capture structural diagnostics only when requested");
            Check(await Snapshot(browser, "!JSON.stringify(s.structure).includes('Private fixture question')"), true, "omit message text from the structural diagnostics");
            script = defaultScript;
            var modern = "<div data-content-search-turn-key=turn1><div data-talvt-turn-state class='group flex flex-col'><div class=contents style='display:contents'><div class='group/user-message'><div data-user-message-bubble><div class='text-size-chat whitespace-pre-wrap'>Current question</div></div><div class=turn-action-controls><button aria-label=Copy>User copy</button></div></div>B</div><div class='actionRow turn-action-controls'><button aria-label=Copy>Copy</button><button aria-label='Good response'>Good</button></div></div></div>";
            await Load(browser, modern);
            Check(await Snapshot(browser, "s.count===1 && s.afterUser && s.text==='B' && s.userText==='Current question'"), true, "read the observed paired-turn layout with display-contents and a direct answer text node");
            Check(await Snapshot(browser, "s.canInferComplete && s.actionsReady && !s.busy"), true, "recognize assistant completion controls in the paired turn");
            await Load(browser, modern + modern.Replace("turn1", "turn2").Replace("Current question", "Next question").Replace("</div>B</div>", "</div>D</div>"));
            Check(await Snapshot(browser, "s.count===2 && s.afterUser && s.text==='D' && s.userText==='Next question'"), true, "associate a second paired-turn answer with its own question");
            await Load(browser, "<div data-talvt-turn-state><div class='group/user-message'><div data-user-message-bubble>Current question</div><button aria-label=Copy>User copy</button></div></div>");
            Check(await Snapshot(browser, "s.text==='' && !s.actionsReady"), true, "exclude the question and its copy control while a paired turn has no answer");
            await Load(browser, modern.Replace("data-talvt-turn-state", "data-talvt-turn-state data-is-streaming=true"));
            Check(await Snapshot(browser, "s.busy"), true, "retain streaming protection on a paired-turn wrapper");
            rules["AssistantSelector"] = "[data-talvt-turn-state] > :has(+ .turn-action-controls)";
            rules["UserSelector"] = "[data-user-message-bubble]";
            script = (string)readerType.GetMethod("BuildConversationScriptWithSelectors", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { rules, true });
            await Load(browser, modern);
            Check(await Snapshot(browser, "s.text==='B' && s.afterUser"), true, "prefer built-in paired-turn support over an obsolete local selector override");
            script = defaultScript;
        }
    }

    static async Task Load(ChromiumWebBrowser browser, string body, string url = "https://chatgpt.com/c/fixture")
    {
        while (!browser.IsBrowserInitialized) await Task.Delay(50);
        browser.LoadHtml("<div id=fixture></div>" + body, url);
        for (var attempt = 0; attempt < 100; attempt++) {
            await Task.Delay(50);
            if (!browser.IsLoading && browser.Address == url && Equals(await Evaluate(browser, "!!document.getElementById('fixture')"), true)) return;
        }
        throw new Exception("Fixture failed to load.");
    }

    static Task<object> Snapshot(ChromiumWebBrowser browser, string expression)
    {
        return Evaluate(browser, "(function(){const s=JSON.parse(" + script + ");return (" + expression + ");})()");
    }

    static async Task<object> Evaluate(ChromiumWebBrowser browser, string code)
    {
        using (var frame = browser.GetMainFrame()) {
            var response = await frame.EvaluateScriptAsync(code, timeout: TimeSpan.FromSeconds(3));
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
