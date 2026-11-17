using System;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using CefSharp;
using CefSharp.WinForms.Handler;
using CefSharp.WinForms.Host;
using SafeExamBrowser.Browser.Wrapper;
using SafeExamBrowser.UserInterface.Contracts.Browser;

namespace SafeExamBrowser.Browser
{
	/// <summary>Hosts the website and submits a question only through an explicit user action.</summary>
	internal sealed class ChatGptAssistant : IChatGptAssistant
	{
		private readonly CefSharpBrowserControl control;
		private readonly RequestContext requestContext;
		private readonly Action<string> diagnostic;
		private string navigationFailure;
		private bool disposed;
		private readonly SemaphoreSlim questionOperation = new SemaphoreSlim(1, 1);

		private class ConversationSnapshot
		{
			public int Count { get; set; }
			public string Id { get; set; }
			public string Text { get; set; }
			public string UserText { get; set; }
			public bool Complete { get; set; }
			public bool Busy { get; set; }
			public bool CanInferComplete { get; set; }
		}

		public object EmbeddableControl => control;
		public event Action HideAnswerRequested;
		public event Action<string> StatusChanged;

		internal ChatGptAssistant(Func<string, bool> allowNavigation, Func<string, bool> allowResource)
			: this(allowNavigation, allowResource, null)
		{
		}

		internal ChatGptAssistant(Func<string, bool> allowNavigation, Func<string, bool> allowResource, Action<string> diagnostic)
		{
			this.diagnostic = diagnostic;
			// No cache path: cookies and login state are isolated from the exam and kept in memory.
			requestContext = new RequestContext(new RequestContextSettings { PersistSessionCookies = false });
			var lifeSpanHandler = LifeSpanHandler.Create(() => throw new InvalidOperationException("ChatGPT popups are disabled."))
				.OnBeforePopupCreated((w, b, f, u, t, d, g, s) => OpenPopupInPanel(u, allowNavigation)).Build();

			control = new CefSharpBrowserControl(lifeSpanHandler, "https://chatgpt.com/") { RequestContext = requestContext };
			var shortcuts = new Handlers.KeyboardHandler { EnableChatGptShortcut = true };
			shortcuts.ChatGptHideAnswerRequested += () => HideAnswerRequested?.Invoke();
			control.PreKeyEvent += (IWebBrowser w, IBrowser b, KeyType type, int key, int nativeKey, CefEventFlags modifiers, bool systemKey, ref bool shortcut, Wrapper.Events.GenericEventArgs args) =>
			{
				if (key == (int) System.Windows.Forms.Keys.H)
					args.Value = shortcuts.OnPreKeyEvent(w, b, type, key, nativeKey, modifiers, systemKey, ref shortcut);
			};
			control.BeforeBrowse += (w, b, f, r, u, i, a) =>
			{
				// Login widgets use blank child frames for messaging; these have no network destination.
				if (!f.IsMain && (r.Url == "about:blank" || r.Url == "about:srcdoc")) return;
				a.Value = !IsAllowedAddress(r.Url) || !allowNavigation(r.Url);
				if (a.Value) ReportFailure("Điều hướng tới " + HostOf(r.Url) + " bị chặn bởi giới hạn ChatGPT hoặc bộ lọc URL của phiên.");
				else if (f.IsMain)
				{
					navigationFailure = null;
				}
			};
			control.ResourceRequestHandlerRequired += (IWebBrowser w, IBrowser b, IFrame f, IRequest r, bool n, bool d, string i, ref bool h, Wrapper.Events.ResourceRequestEventArgs a) =>
			{
				a.Handler = new ResourcePolicy(allowResource, url => ReportFailure("Bộ lọc URL của phiên chặn tài nguyên từ " + HostOf(url) + "."));
			};
			control.OpenUrlFromTab += (w, b, f, u, t, g, a) => { OpenPopupInPanel(u, allowNavigation); a.Value = true; };
			control.BeforeContextMenu += (w, b, f, p, m) => m.Clear();
			control.CanDownload += (w, b, u, r, a) => a.Value = false;
			control.BeforeDownload += (w, b, d, c, a) => { c.Dispose(); a.Value = true; return true; };
			control.FileDialogRequested += (w, b, m, t, p, f, e, d, c) => { using (c) c.Cancel(); };
			control.LoadError += (s, e) =>
			{
				if (e.Frame.IsMain && e.ErrorCode != CefErrorCode.Aborted)
					ReportFailure("Không tải được " + HostOf(e.FailedUrl) + " (" + e.ErrorCode + "). Kiểm tra mạng rồi tải lại.");
			};
			control.FrameLoadEnd += (s, e) =>
			{
				if (e.Frame.IsMain && e.HttpStatusCode >= 400)
					ReportFailure("Trang " + HostOf(e.Url) + " trả về HTTP " + e.HttpStatusCode + ". Đăng nhập có thể không được hỗ trợ trong trình duyệt nhúng.");
			};
			control.LoadingStateChanged += (s, e) =>
			{
				if (!e.IsLoading && navigationFailure == null)
					StatusChanged?.Invoke("Có thể hỏi khi ô chat hiện ra, kể cả chưa đăng nhập. Nhấn Dán & gửi để gửi câu hỏi.");
			};
		}

		private PopupCreation OpenPopupInPanel(string address, Func<string, bool> allowNavigation)
		{
			if (!disposed && IsAllowedAddress(address) && allowNavigation(address))
			{
				// Keep authentication in the same request context instead of silently dropping window.open.
				control.BeginInvoke((Action) (() => { if (!disposed) control.Load(address); }));
			}
			else
				ReportFailure("Cửa sổ tới " + HostOf(address) + " bị chặn. Hãy dùng luồng đăng nhập email trong khung ChatGPT.");
			return PopupCreation.Cancel;
		}

		private void ReportFailure(string message)
		{
			navigationFailure = message;
			diagnostic?.Invoke(message);
			StatusChanged?.Invoke(message);
		}

		private static string HostOf(string address)
		{
			return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : "địa chỉ không xác định";
		}

		private static string BuildEditorScript(string prompt)
		{
			var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt));
			return @"function findEditor() {
  const selectors = ['#prompt-textarea', '#mobile-composer-prompt', 'textarea[name=""prompt""]', 'textarea[name=""prompt-textarea""]', 'textarea[data-testid=""prompt-textarea""]',
    'main .ProseMirror[contenteditable=""true""]', 'form [contenteditable=""true""][role=""textbox""]',
    'main [contenteditable=""true""][data-lexical-editor=""true""]', 'main form textarea'];
  for (const selector of selectors) {
    for (const element of document.querySelectorAll(selector)) {
      const style = getComputedStyle(element);
      if (element.getClientRects().length && style.visibility !== 'hidden' && style.display !== 'none' && !element.disabled && !element.readOnly &&
          element.getAttribute('aria-disabled') !== 'true' &&
          (element.tagName === 'TEXTAREA' || element.isContentEditable)) return element;
    }
  }
  return null;
}
function normalized(text) { return (text || '').replace(/\r\n/g, '\n').replace(/\n{2,}/g, '\n').replace(/\u00a0/g, ' ').trim(); }
const text = new TextDecoder().decode(Uint8Array.from(atob('" + encoded + @"'), c => c.charCodeAt(0)));";
		}

		public async Task<bool> PreparePromptAsync(string prompt)
		{
			if (disposed || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000) return false;
			var findEditor = BuildEditorScript(prompt);
			var focusScript = "(function () {" + findEditor + @"
  const editor = findEditor();
  if (!editor) return 'missing';
  const existing = editor.value || editor.innerText || '';
  if (normalized(existing).endsWith(normalized(text))) return 'already';
  editor.focus();
  if (editor.tagName === 'TEXTAREA') editor.setSelectionRange(existing.length, existing.length);
  else {
    const range = document.createRange(); range.selectNodeContents(editor); range.collapse(false);
    const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
  }
  return existing.trim() ? 'append' : 'ready';
})()";
			var verifyScript = "(function () {" + findEditor + @"
  const editor = findEditor();
  return !!editor && normalized(editor.value || editor.innerText).endsWith(normalized(text));
})()";
			var fallbackScript = "(function () {" + findEditor + @"
  const editor = findEditor();
  if (!editor) return false;
  const existing = editor.value || editor.innerText || '';
  if (normalized(existing).endsWith(normalized(text))) return true;
  editor.focus();
  if (editor.tagName === 'TEXTAREA') {
    const next = existing.trim() ? existing + '\n\n' + text : text;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(editor, next);
    editor.dispatchEvent(new Event('input', { bubbles: true }));
    editor.dispatchEvent(new Event('change', { bubbles: true }));
  } else {
    const range = document.createRange(); range.selectNodeContents(editor); range.collapse(false);
    const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
    document.execCommand('insertText', false, (existing.trim() ? '\n\n' : '') + text);
  }
  return normalized(editor.value || editor.innerText).endsWith(normalized(text));
})()";
			var deadline = DateTime.UtcNow.AddSeconds(15);
			var openedChat = false;
			while (!disposed && DateTime.UtcNow < deadline)
			{
				if (control.IsBrowserInitialized)
				{
					using (var frame = control.GetMainFrame())
					{
						if (frame != null && frame.IsValid && Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri))
						{
							var onChat = uri.Scheme == "https" && (uri.Host == "chatgpt.com" || uri.Host == "chat.openai.com")
								&& !uri.AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase);
							if (!onChat && !openedChat)
							{
								control.Load("https://chatgpt.com/");
								openedChat = true;
							}
							else if (onChat)
							{
								if (control.Visible) control.GetBrowser().GetHost().SetFocus(true);
								var focus = await EvaluateSafelyAsync(frame, focusScript, timeout: TimeSpan.FromSeconds(2));
								var state = focus.Success ? focus.Result as string : null;
								if (state == "already") return true;
								if (state == "ready" || state == "append")
								{
									// Chromium text input updates React/ProseMirror without using the system clipboard.
									if (control.Visible)
									{
										using (var client = control.GetDevToolsClient())
										{
											var insertion = client.Input.InsertTextAsync((state == "append" ? "\n\n" : "") + prompt);
											if (await Task.WhenAny(insertion, Task.Delay(3000)) != insertion || !(await insertion).Success) return false;
										}
									}
									await Task.Delay(350);
									if (disposed || !frame.IsValid) return false;
									var verification = await EvaluateSafelyAsync(frame, verifyScript, timeout: TimeSpan.FromSeconds(2));
									var inserted = verification.Success && verification.Result is bool present && present;
									if (!inserted)
									{
										var fallback = await EvaluateSafelyAsync(frame, fallbackScript);
										await Task.Delay(200);
										var fallbackCheck = await EvaluateSafelyAsync(frame, verifyScript);
										inserted = fallback.Success && fallbackCheck.Success && fallbackCheck.Result is bool pasted && pasted;
									}
									if (inserted)
									{
										await Task.Delay(350);
										if (disposed || !frame.IsValid) return false;
										var stable = await EvaluateSafelyAsync(frame, verifyScript, timeout: TimeSpan.FromSeconds(2));
										if (stable.Success && stable.Result is bool remains && remains) return true;
									}
								}
							}
						}
					}
				}
				await Task.Delay(250);
			}
			StatusChanged?.Invoke("Chưa tìm thấy ô chat có thể nhập. Mở trang ChatGPT chính và thử lại; trang xác thực không phải ô hỏi đáp.");
			return false;
		}
		public Task<bool> SendPreparedPromptAsync(string prompt)
		{
			return SendPreparedPromptAsync(prompt, CancellationToken.None);
		}

		private async Task<bool> SendPreparedPromptAsync(string prompt, CancellationToken cancellationToken)
		{
			if (disposed || string.IsNullOrWhiteSpace(prompt) || prompt.Length > 12000) return false;
			var script = "(function () {" + BuildEditorScript(prompt) + @"
  const editor = findEditor();
  if (!editor || !normalized(editor.value || editor.innerText).endsWith(normalized(text))) return -2;
  const form = editor.closest('form');
  const root = form || document;
  const selectors = ['#composer-submit-button', 'button[data-testid=""send-button""]', 'button[aria-label=""Send message""]',
    'button[aria-label=""Send prompt""]', 'button[aria-label=""Gửi tin nhắn""]', 'button[aria-label=""Gửi lời nhắc""]'];
  if (form) selectors.push('button[type=""submit""]');
  for (const selector of selectors) {
    const button = Array.from(root.querySelectorAll(selector)).find(e => e.getClientRects().length && !e.disabled && e.getAttribute('aria-disabled') !== 'true');
    if (!button) continue;
    const count = document.querySelectorAll('[data-message-author-role=""user""], [data-turn=""user""]').length;
    button.click();
    return count;
  }
  return -1;
})()";
			var deadline = DateTime.UtcNow.AddSeconds(6);
			while (!disposed && DateTime.UtcNow < deadline)
			{
				cancellationToken.ThrowIfCancellationRequested();
				using (var frame = control.GetMainFrame())
				{
					if (frame == null || !frame.IsValid || !Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri)
						|| uri.Scheme != "https" || (uri.Host != "chatgpt.com" && uri.Host != "chat.openai.com")) return false;
					var click = await EvaluateSafelyAsync(frame, script, timeout: TimeSpan.FromSeconds(2));
					if (!click.Success || click.Result == null) return false;
					var before = Convert.ToInt32(click.Result);
					if (before == -2) return false;
					if (before >= 0)
					{
						// Click at most once. Confirm the draft cleared or a user turn appeared.
						var confirmScript = "(function () {" + BuildEditorScript(prompt) + @"
  const editor = findEditor();
  return (!!editor && !normalized(editor.value || editor.innerText)) ||
    document.querySelectorAll('[data-message-author-role=""user""], [data-turn=""user""]').length > " + before + @";
})()";
						for (var attempt = 0; attempt < 20 && !disposed; attempt++)
						{
							await Task.Delay(250);
							if (!frame.IsValid) return false;
							var confirm = await EvaluateSafelyAsync(frame, confirmScript, timeout: TimeSpan.FromSeconds(2));
							if (confirm.Success && confirm.Result is bool sent && sent)
							{
								return true;
							}
						}
						return false;
					}
				}
				await Task.Delay(200);
			}
			return false;
		}

		private static async Task<JavascriptResponse> EvaluateSafelyAsync(IFrame frame, string script, TimeSpan? timeout = null)
		{
			try { return await frame.EvaluateScriptAsync(script, timeout: timeout ?? TimeSpan.FromSeconds(2)); }
			catch (TaskCanceledException) { return new JavascriptResponse { Success = false }; }
			catch (ObjectDisposedException) { return new JavascriptResponse { Success = false }; }
		}
		public async Task<ChatGptAnswer> AskQuestionAsync(string question, ChatGptQuestionKind kind, CancellationToken cancellationToken)
		{
			kind = QuestionAnswerFormatter.ResolveKind(question, kind);
			await questionOperation.WaitAsync(cancellationToken);
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (string.IsNullOrWhiteSpace(question) || question.Length > 10000)
					return AnswerFailure(kind, "Hãy chọn một câu hỏi không quá 10.000 ký tự.");
				var before = await ReadConversationAsync();
				if (before.Busy) return AnswerFailure(kind, "ChatGPT đang trả lời một câu khác. Hãy đợi xong rồi hỏi tiếp.");
				var prompt = QuestionAnswerFormatter.BuildPrompt(question, kind);
				StatusChanged?.Invoke("Đang gửi câu hỏi " + (kind == ChatGptQuestionKind.MultipleChoice ? "trắc nghiệm..." : "tự luận..."));
				if (!await PreparePromptAsync(prompt)) return AnswerFailure(kind, "Không tìm thấy ô chat để dán câu hỏi.");
				cancellationToken.ThrowIfCancellationRequested();
				// Capture the baseline after a possible redirect from the authentication page.
				before = await ReadConversationAsync();
				cancellationToken.ThrowIfCancellationRequested();
				if (!await SendPreparedPromptAsync(prompt, cancellationToken)) return AnswerFailure(kind, "Câu hỏi đã dán nhưng ChatGPT chưa xác nhận gửi.");
				StatusChanged?.Invoke("Đã gửi. Đang đợi ChatGPT trả lời xong...");
				var deadline = DateTime.UtcNow.AddSeconds(120);
				string stableText = null;
				var stableSince = DateTime.UtcNow;
				while (!disposed && DateTime.UtcNow < deadline)
				{
					await Task.Delay(750, cancellationToken);
					var current = await ReadConversationAsync();
					var isNew = current.Count > before.Count || (!string.IsNullOrEmpty(current.Id) && current.Id != before.Id);
					if (!isNew || string.IsNullOrWhiteSpace(current.Text)) continue;
					if (!string.IsNullOrEmpty(current.UserText) && !QuestionAnswerFormatter.PlainText(current.UserText).Contains(QuestionAnswerFormatter.PlainText(question)))
						return AnswerFailure(kind, "Cuộc hội thoại đã chuyển sang câu khác; không sao chép câu trả lời cũ.");
					if (current.Text != stableText)
					{
						stableText = current.Text;
						stableSince = DateTime.UtcNow;
					}
					if (!current.Busy && (current.Complete || (current.CanInferComplete && DateTime.UtcNow - stableSince >= TimeSpan.FromSeconds(4))))
					{
						cancellationToken.ThrowIfCancellationRequested();
						return QuestionAnswerFormatter.Normalize(question, kind, current.Text);
					}
				}
				return AnswerFailure(kind, "Chưa nhận được câu trả lời hoàn chỉnh trong 120 giây. Hãy kiểm tra khung ChatGPT.");
			}
			finally { questionOperation.Release(); }
		}

		private static ChatGptAnswer AnswerFailure(ChatGptQuestionKind kind, string error)
		{
			return new ChatGptAnswer { Kind = kind, Text = "", Error = error };
		}

		private async Task<ConversationSnapshot> ReadConversationAsync()
		{
			if (disposed || !control.IsBrowserInitialized) return new ConversationSnapshot();
			using (var frame = control.GetMainFrame())
			{
				if (frame == null || !frame.IsValid || !Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri)
					|| uri.Scheme != "https" || (uri.Host != "chatgpt.com" && uri.Host != "chat.openai.com")) return new ConversationSnapshot();
				var response = await EvaluateSafelyAsync(frame, @"(function () {
  const assistant = Array.from(document.querySelectorAll('[data-message-role=""assistant""], [data-message-author-role=""assistant""]'));
  const roots = assistant.filter(e => !assistant.some(other => other !== e && other.contains(e)));
  const last = roots[roots.length - 1];
  const user = Array.from(document.querySelectorAll('[data-message-role=""user""], [data-message-author-role=""user""]')).pop();
  const content = last && (last.querySelector('[data-assistant-markdown-root], .markdown, [data-message-content]') || last);
  const stop = Array.from(document.querySelectorAll('[data-testid=""stop-button""], button[aria-label^=""Stop""], button[aria-label*=""Dừng""]')).some(e => e.getClientRects().length);
  const actions = last && (last.closest('article') || last);
  const hasCopy = !!(actions && actions.querySelector('[data-testid=""copy-turn-action-button""], button[aria-label^=""Copy""], button[aria-label^=""Sao chép""]'));
  return JSON.stringify({ count: roots.length, id: last ? (last.getAttribute('data-message-id') || last.id || String(roots.length)) : '',
    text: content ? content.innerText.slice(0, 200000) : '', userText: user ? user.innerText.slice(0, 20000) : '',
    complete: !!last && (last.hasAttribute('data-message-complete') || last.getAttribute('data-is-streaming') === 'false'),
    canInferComplete: hasCopy,
    busy: stop || !!(last && last.hasAttribute('data-message-role') && !last.hasAttribute('data-message-complete')) || !!(last && last.querySelector('[aria-busy=""true""], [data-is-streaming=""true""]')) });
})()", timeout: TimeSpan.FromSeconds(2));
				return response.Success && response.Result is string json
					? JsonConvert.DeserializeObject<ConversationSnapshot>(json) ?? new ConversationSnapshot() : new ConversationSnapshot();
			}
		}

		public void Reload()
		{
			if (!disposed) control.Load("https://chatgpt.com/");
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			control.Dispose();
			requestContext.Dispose();
		}

		internal static bool IsAllowedAddress(string address)
		{
			if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
			return uri.Host == "chatgpt.com" || uri.Host == "auth.chatgpt.com" || uri.Host == "chat.openai.com"
				|| uri.Host == "accounts.google.com"
				|| uri.Host == "cdn.platform.openai.com"
				|| uri.Host == "auth.openai.com" || uri.Host.EndsWith(".auth.openai.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "auth0.openai.com"
				|| uri.Host == "challenges.cloudflare.com";
		}

		private sealed class ResourcePolicy : CefSharp.Handler.ResourceRequestHandler
		{
			private readonly Func<string, bool> allow;
			private readonly Action<string> blocked;
			internal ResourcePolicy(Func<string, bool> allow, Action<string> blocked) { this.allow = allow; this.blocked = blocked; }
			protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser webBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
			{
				if (allow(request.Url)) return CefReturnValue.Continue;
				blocked(request.Url);
				return CefReturnValue.Cancel;
			}
		}
	}
}
