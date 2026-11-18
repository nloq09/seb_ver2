using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using Newtonsoft.Json;
using CefSharp;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CefSharp.OffScreen;
using SafeExamBrowser.Browser.Wrapper;
using SafeExamBrowser.UserInterface.Contracts.Browser;

namespace SafeExamBrowser.Browser
{
	/// <summary>Hosts the website and submits a question only through an explicit user action.</summary>
	internal sealed class ChatGptAssistant : IChatGptAssistant
	{
		private readonly ChromiumWebBrowser control;
		private readonly Image preview;
		private int framePending;
		private readonly RequestContext requestContext;
		private readonly Action<string> diagnostic;
		private readonly Func<ChatGptLogin> loadLogin;
		private string navigationFailure;
		private bool disposed;
		private readonly CancellationTokenSource loginCancellation = new CancellationTokenSource();
		private CancellationTokenSource loginAttemptCancellation;
		private Task<bool> loginTask;
		private string loginFailure;
		private volatile bool manualLogin;
		private readonly ChatGptLoginInput loginInput;
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
			public bool AfterUser { get; set; }
			public bool ReaderSucceeded { get; set; }
			public int AssistantMarkers { get; set; }
			public int UserMarkers { get; set; }
			public bool StopVisible { get; set; }
			public bool Streaming { get; set; }
			public bool ActionsReady { get; set; }
			public bool ComposerIdle { get; set; }
			public bool LastMarkerHasBox { get; set; }
			public bool LastMarkerAriaHidden { get; set; }
			public object Structure { get; set; }
			public bool CustomSelectors { get; set; }
		}

		public object EmbeddableControl => preview;
		public event Action<string> StatusChanged;
		public event Action QuestionSent;

		internal ChatGptAssistant(Func<string, bool> allowNavigation, Func<string, bool> allowResource)
			: this(allowNavigation, allowResource, null)
		{
		}

		internal ChatGptAssistant(Func<string, bool> allowNavigation, Func<string, bool> allowResource, Action<string> diagnostic)
			: this(allowNavigation, allowResource, diagnostic, null)
		{
		}

		internal ChatGptAssistant(Func<string, bool> allowNavigation, Func<string, bool> allowResource, Action<string> diagnostic, Func<ChatGptLogin> loadLogin)
		{
			this.diagnostic = diagnostic;
			this.loadLogin = loadLogin ?? LoadLogin;
			// No cache path: cookies and login state are isolated from the exam and kept in memory.
			requestContext = new RequestContext(new RequestContextSettings { PersistSessionCookies = false });
			preview = new Image { Focusable = false, IsHitTestVisible = false, Stretch = Stretch.Uniform };
			control = new ChromiumWebBrowser("https://chatgpt.com/", requestContext: requestContext,
				automaticallyCreateBrowser: false, useLegacyRenderHandler: false)
			{
				RequestHandler = new ChatRequestHandler(this, allowNavigation, allowResource),
				LifeSpanHandler = new ChatLifeSpanHandler(this, allowNavigation),
				DownloadHandler = new ChatDownloadHandler(),
				DialogHandler = new ChatDialogHandler()
			};
			loginInput = new ChatGptLoginInput(preview, control,
				() => !disposed && loginTask != null && !loginTask.IsCompleted, () => manualLogin = true);
			preview.SizeChanged += (sender, args) =>
			{
				if (!disposed && args.NewSize.Width >= 1 && args.NewSize.Height >= 1)
					control.Size = new System.Drawing.Size((int) args.NewSize.Width, (int) args.NewSize.Height);
			};
			control.Paint += PaintPreview;
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
				if (!e.IsLoading && navigationFailure == null && (loginTask == null || loginTask.IsCompleted))
					StatusChanged?.Invoke(loginFailure ?? "Có thể hỏi khi ô chat hiện ra. Ctrl + Shift + U để đăng nhập bằng tài khoản đã lưu.");
			};
			control.CreateBrowser();
		}

		public void BeginLogin()
		{
			if (disposed) return;
			if (loginTask != null && !loginTask.IsCompleted)
			{
				StatusChanged?.Invoke("Đang đăng nhập ChatGPT. Hoàn tất xác minh trong khung nếu được yêu cầu.");
				return;
			}
			loginFailure = null;
			var login = loadLogin();
			if (login == null)
			{
				if (loginFailure == null) LoginFailed("Chưa có tài khoản đã lưu. Cấu hình .env hoặc ConfigureChatGptLogin.ps1 rồi nhấn Ctrl + Shift + U.");
				return;
			}
			manualLogin = false;
			navigationFailure = null;
			control.Load("https://chatgpt.com/auth/login");
			StartLogin(login);
		}

		private void StartLogin(ChatGptLogin login)
		{
			loginAttemptCancellation?.Cancel();
			loginAttemptCancellation?.Dispose();
			loginAttemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(loginCancellation.Token);
			loginTask = login == null ? null : LogInAsync(login, loginAttemptCancellation.Token);
		}

		private ChatGptLogin LoadLogin()
		{
			try
			{
				var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeExamBrowser", "ChatGpt", "credentials.dat");
				return ChatGptLogin.LoadEnvironmentFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env"))
					?? ChatGptLogin.Load(path);
			}
			catch (Exception)
			{
				LoginFailed("Không đọc được tài khoản ChatGPT. Kiểm tra CHATGPT_EMAIL và CHATGPT_PASSWORD trong .env hoặc chạy lại ConfigureChatGptLogin.ps1.");
				return null;
			}
		}

		private async Task<bool> LogInAsync(ChatGptLogin login, CancellationToken cancellationToken)
		{
			var gatewaySubmitted = false;
			var emailSubmitted = false;
			var passwordSubmitted = false;
			var googleEmailSubmitted = false;
			var googlePasswordSubmitted = false;
			var manualRequested = false;
			var googleUnavailableReported = false;
			var deadline = DateTime.UtcNow.AddSeconds(90);
			Action<string> requestManual = message =>
			{
				manualLogin = login.Manual = true;
				if (manualRequested) return;
				manualRequested = true;
				deadline = DateTime.UtcNow.AddMinutes(10);
				diagnostic?.Invoke("Authentication is waiting for the user to complete verification.");
				StatusChanged?.Invoke(message + " Nhấn Ctrl + Shift + J, bấm/gõ trực tiếp trong trang đăng nhập. Chờ tối đa 10 phút.");
			};
			try
			{
				StatusChanged?.Invoke("Đang đăng nhập ChatGPT...");
				while (!disposed && DateTime.UtcNow < deadline)
				{
					await Task.Delay(500, cancellationToken);
					if (manualLogin) requestManual("Đang chờ bạn hoàn tất đăng nhập.");
					if (!control.IsBrowserInitialized) continue;
					if (await IsChatReadyAsync(cancellationToken)) return MarkChatReady();
					if (control.IsLoading) continue;
					using (var frame = control.GetMainFrame())
					{
						if (frame == null || !frame.IsValid) continue;
						if (ChatGptLogin.IsGoogleAddress(frame.Url))
						{
							if (login.Provider != "google")
								return LoginFailed("Tài khoản chuyển sang Google. Đặt CHATGPT_LOGIN_PROVIDER=google và dùng mật khẩu Google trong .env.");
							var response = await EvaluateSafelyAsync(frame, login.BuildGoogleScript(googleEmailSubmitted, googlePasswordSubmitted));
							cancellationToken.ThrowIfCancellationRequested();
							if (!response.Success) continue;
							switch (response.Result as string)
							{
								case "email":
									googleEmailSubmitted = true;
									diagnostic?.Invoke("Auto-login submitted the Google email step.");
									StatusChanged?.Invoke("Đã gửi email cho Google. Đang chờ bước mật khẩu...");
									break;
								case "account":
									googleEmailSubmitted = true;
									StatusChanged?.Invoke("Đã chọn tài khoản Google khớp CHATGPT_EMAIL. Đang chờ bước tiếp theo...");
									break;
								case "choose-account":
									requestManual("Chọn tài khoản khớp CHATGPT_EMAIL hoặc dùng tài khoản khác trên trang Google.");
									break;
								case "password":
									googlePasswordSubmitted = true;
									diagnostic?.Invoke("Auto-login submitted the Google password step.");
									StatusChanged?.Invoke("Đã gửi mật khẩu Google. Đang chờ trở về ChatGPT...");
									break;
								case "verification": requestManual("Google yêu cầu mã OTP, xác nhận trên điện thoại hoặc đồng ý đăng nhập."); break;
								case "unsupported-browser": return LoginFailed("Google từ chối trình duyệt nhúng này. Cần dùng phương thức đăng nhập được Google hỗ trợ.");
									case "provider-error": return LoginFailed("Google trả về lỗi đăng nhập. Mở khung để xem thông báo của Google rồi nhấn Ctrl + Shift + U để thử lại.");
								case "account-mismatch": requestManual("Google đang hiển thị tài khoản khác CHATGPT_EMAIL. Chọn lại đúng tài khoản; mật khẩu chưa được điền."); break;
								case "rejected": requestManual("Google từ chối thông tin tự điền. Kiểm tra thông báo và hoàn tất đăng nhập thủ công."); break;
							}
						}
						else if (ChatGptLogin.IsCredentialAddress(frame.Url))
						{
							var response = await EvaluateSafelyAsync(frame, login.BuildFormScript(emailSubmitted, passwordSubmitted));
							cancellationToken.ThrowIfCancellationRequested();
							if (!response.Success) continue;
							switch (response.Result as string)
							{
								case "email":
									emailSubmitted = true;
									diagnostic?.Invoke("Auto-login submitted the authentication email step.");
									StatusChanged?.Invoke("Đã gửi email. Đang chờ bước mật khẩu...");
									break;
								case "password":
									passwordSubmitted = true;
									diagnostic?.Invoke("Auto-login submitted the password step.");
									StatusChanged?.Invoke("Đã gửi mật khẩu. Đang xác nhận đăng nhập...");
									break;
								case "password-option":
									StatusChanged?.Invoke("Đã chọn đăng nhập bằng mật khẩu OpenAI. Đang chờ ô mật khẩu...");
									break;
								case "google-option": StatusChanged?.Invoke("Đã chọn Continue with Google. Đang chuyển sang Google..."); break;
								case "google-unavailable":
									if (!googleUnavailableReported) StatusChanged?.Invoke("Đang tìm nút Continue with Google. Có thể mở khung để chọn cách đăng nhập.");
									googleUnavailableReported = true;
									break;
								case "verification": requestManual("ChatGPT yêu cầu xác minh hoặc mã OTP."); break;
								case "rejected": requestManual("ChatGPT từ chối thông tin tự điền. Kiểm tra thông báo trên trang đăng nhập."); break;
								case "wrong-provider": requestManual("Trang đang yêu cầu mật khẩu OpenAI nhưng .env được đặt cho Google. Chọn Continue with Google."); break;
							}
						}
						else if (Uri.TryCreate(frame.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "chatgpt.com")
						{
							var response = await EvaluateSafelyAsync(frame, login.BuildGatewayScript(gatewaySubmitted));
							cancellationToken.ThrowIfCancellationRequested();
							if (!response.Success) continue;
							switch (response.Result as string)
							{
								case "email":
									gatewaySubmitted = true;
									diagnostic?.Invoke("Auto-login submitted the ChatGPT login email step.");
									StatusChanged?.Invoke("Đã gửi email. Đang mở trang xác thực...");
									break;
								case "signed-in":
									loginFailure = null;
									manualLogin = false;
									StatusChanged?.Invoke("Đã đăng nhập ChatGPT.");
									return true;
								case "password-option":
									StatusChanged?.Invoke("Đã chọn đăng nhập bằng mật khẩu OpenAI. Đang chờ ô mật khẩu...");
									break;
								case "google-option": StatusChanged?.Invoke("Đã chọn Continue with Google. Đang chuyển sang Google..."); break;
								case "google-unavailable":
									if (!googleUnavailableReported) StatusChanged?.Invoke("Đang tìm nút Continue with Google. Có thể mở khung để chọn cách đăng nhập.");
									googleUnavailableReported = true;
									break;
								case "wrong-provider": requestManual("Trang cung cấp mật khẩu OpenAI nhưng .env được đặt cho Google. Chọn Continue with Google."); break;
								case "verification": requestManual("ChatGPT yêu cầu xác minh hoặc mã OTP."); break;
								case "rejected": requestManual("ChatGPT từ chối email tự điền. Kiểm tra thông báo trên trang đăng nhập."); break;
							}
						}
					}
				}
				return LoginFailed(manualRequested ? "Hết 10 phút chờ hoàn tất đăng nhập. Nhấn Ctrl + Shift + U để thử lại."
					: "Chưa xác nhận được đăng nhập ChatGPT sau 90 giây. Mở khung để xem trạng thái.");
			}
			catch (OperationCanceledException) { return false; }
			catch (Exception) { return cancellationToken.IsCancellationRequested ? false : LoginFailed("Không thể tự đăng nhập ChatGPT. Kiểm tra mạng và file đăng nhập."); }
			finally { login.Password = null; }
		}

		private bool LoginFailed(string message)
		{
			loginFailure = message;
			if (!disposed)
			{
				diagnostic?.Invoke(message);
				StatusChanged?.Invoke(message);
			}
			return false;
		}

		private async Task<bool> WaitForLoginAsync(CancellationToken cancellationToken)
		{
			while (!disposed)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var attempt = loginTask;
				if (loginFailure != null || attempt != null && (!attempt.IsCompleted || !await attempt))
				{
					if (await IsChatReadyAsync(cancellationToken)) return MarkChatReady();
				}
				if (loginFailure != null) return false;
				attempt = loginTask;
				if (attempt == null) return true;
				if (attempt.IsCompleted) return await attempt;
				await Task.Delay(200, cancellationToken);
			}
			return false;
		}

		private bool MarkChatReady()
		{
			loginFailure = null;
			manualLogin = false;
			loginTask = Task.FromResult(true);
			loginAttemptCancellation?.Cancel();
			StatusChanged?.Invoke("ChatGPT đã sẵn sàng nhận câu hỏi.");
			return true;
		}

		private async Task<bool> IsChatReadyAsync(CancellationToken cancellationToken)
		{
			if (disposed || !control.IsBrowserInitialized) return false;
			using (var frame = control.GetMainFrame())
			{
				if (frame == null || !frame.IsValid) return false;
				var address = frame.Url;
				if (!IsChatContentAddress(address)) return false;
				var response = await EvaluateSafelyAsync(frame, BuildChatReadyScript());
				cancellationToken.ThrowIfCancellationRequested();
				return !disposed && frame.IsValid && frame.Url == address && IsChatContentAddress(frame.Url)
					&& response.Success && response.Result is bool ready && ready;
			}
		}

		internal static bool IsChatContentAddress(string address)
		{
			return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https"
				&& (uri.Host == "chatgpt.com" || uri.Host == "chat.openai.com")
				&& !uri.AbsolutePath.Equals("/auth", StringComparison.OrdinalIgnoreCase)
				&& !uri.AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase)
				&& !uri.AbsolutePath.Equals("/api", StringComparison.OrdinalIgnoreCase)
				&& !uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
		}

		internal static string BuildChatReadyScript()
		{
			return @"(function () {
  if (location.protocol !== 'https:' || !['chatgpt.com', 'chat.openai.com'].includes(location.hostname) || /^\/(?:auth|api)(?:\/|$)/i.test(location.pathname)) return false;
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none';
  const login = Array.from(document.querySelectorAll('a[href], button, [data-testid=""login-button""]')).some(e => {
    if (!visible(e)) return false;
    const href = e.getAttribute('href');
    const link = href && new URL(href, location.href);
    return e.getAttribute('data-testid') === 'login-button' || (link && link.origin === location.origin && /^\/auth\/login(?:\/|$)/i.test(link.pathname)) ||
      /^(?:log in|sign in|đăng nhập)$/i.test((e.innerText || e.getAttribute('aria-label') || '').trim());
  });
  if (login || Array.from(document.querySelectorAll('[role=""dialog""][aria-modal=""true""], iframe[src*=""challenges.cloudflare.com""]')).some(visible)) return false;
" + BuildEditorScript(string.Empty) + @"
  const editor = findEditor();
  return !!editor && !editor.closest('[inert], [aria-hidden=""true""]');
})()";
		}

		private void PaintPreview(object sender, OnPaintEventArgs e)
		{
			if (disposed || e.IsPopup || e.Width <= 0 || e.Height <= 0) return;
			if (Interlocked.Exchange(ref framePending, 1) != 0) return;

			var stride = e.Width * 4;
			var frame = BitmapSource.Create(e.Width, e.Height, 96, 96, PixelFormats.Pbgra32, null,
				e.BufferHandle, stride * e.Height, stride);
			frame.Freeze();
			preview.Dispatcher.BeginInvoke((Action) (() =>
			{
				Interlocked.Exchange(ref framePending, 0);
				if (!disposed) preview.Source = frame;
			}));
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
			if (!await WaitForLoginAsync(loginCancellation.Token)) return false;
			var findEditor = BuildEditorScript(prompt);
			var verifyScript = "(function () {" + findEditor + @"
  const editor = findEditor();
  return !!editor && normalized(editor.value || editor.innerText).endsWith(normalized(text));
})()";
			var insertScript = "(function () {" + findEditor + @"
  const editor = findEditor();
  if (!editor) return false;
  const existing = editor.value || editor.innerText || '';
  if (normalized(existing).endsWith(normalized(text))) return true;
  if (editor.tagName === 'TEXTAREA') {
    const next = existing.trim() ? existing + '\n\n' + text : text;
    Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value').set.call(editor, next);
    editor.dispatchEvent(new Event('input', { bubbles: true }));
    editor.dispatchEvent(new Event('change', { bubbles: true }));
  } else {
    const draft = document.createDocumentFragment();
    if (existing.trim()) draft.append(document.createElement('p'));
    for (const line of text.split(/\r?\n/)) {
      const paragraph = document.createElement('p');
      paragraph.append(line ? document.createTextNode(line) : document.createElement('br'));
      draft.append(paragraph);
    }
    if (existing.trim()) editor.append(draft);
    else editor.replaceChildren(draft);
    editor.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: text }));
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
								var insertion = await EvaluateSafelyAsync(frame, insertScript, timeout: TimeSpan.FromSeconds(2));
								if (insertion.Success && insertion.Result is bool inserted && inserted)
								{
									await Task.Delay(350);
									if (disposed || !frame.IsValid) return false;
									var verification = await EvaluateSafelyAsync(frame, verifyScript, timeout: TimeSpan.FromSeconds(2));
									if (verification.Success && verification.Result is bool present && present) return true;
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
    const count = document.querySelectorAll('[data-message-author-role=""user""], [data-turn=""user""], [data-user-message-bubble]').length;
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
    document.querySelectorAll('[data-message-author-role=""user""], [data-turn=""user""], [data-user-message-bubble]').length > " + before + @";
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
				if (!await WaitForLoginAsync(cancellationToken))
					return AnswerFailure(kind, loginFailure ?? "Đăng nhập ChatGPT đã dừng.");
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
				QuestionSent?.Invoke();
				StatusChanged?.Invoke("Đã gửi. Đang đợi ChatGPT trả lời xong...");
				var deadline = DateTime.UtcNow.AddSeconds(120);
				string stableText = null;
				string previousDiagnostic = null;
				var stableSince = DateTime.UtcNow;
				while (!disposed && DateTime.UtcNow < deadline)
				{
					await Task.Delay(750, cancellationToken);
					var current = await ReadConversationAsync();
					var isNew = current.Count > before.Count || (!string.IsNullOrEmpty(current.Id) && current.Id != before.Id);
					RecordReplyDiagnostics(current, before, question, isNew, ref previousDiagnostic);
					if (!isNew || !current.AfterUser || string.IsNullOrWhiteSpace(current.Text)) continue;
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
						var answer = QuestionAnswerFormatter.Normalize(question, kind, current.Text);
						StatusChanged?.Invoke(answer.IsValid ? "Đã nhận được câu trả lời hoàn chỉnh." : answer.Error);
						return answer;
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
				var captureStructure = File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatGptDiagnostics.target"));
				var response = await EvaluateSafelyAsync(frame, BuildConversationScriptWithSelectors(LoadReplySelectors(), captureStructure), timeout: TimeSpan.FromSeconds(2));
				return response.Success && response.Result is string json
					? JsonConvert.DeserializeObject<ConversationSnapshot>(json) ?? new ConversationSnapshot() : new ConversationSnapshot();
			}
		}

		internal static string BuildConversationScript()
		{
			return BuildConversationScriptWithSelectors(null, false);
		}

		private static string BuildConversationScriptWithSelectors(Dictionary<string, string> selectors, bool captureStructure)
		{
			return @"(function () {
  if (location.protocol !== 'https:' || !['chatgpt.com', 'chat.openai.com'].includes(location.hostname) || /^\/(?:auth|api)(?:\/|$)/i.test(location.pathname)) return '{}';
  const rules = " + JsonConvert.SerializeObject(selectors ?? new Dictionary<string, string>()) + @";
  const query = (root, selector) => { try { return Array.from(root.querySelectorAll(selector)); } catch (_) { return []; } };
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden' && getComputedStyle(e).display !== 'none' && !e.closest('[aria-hidden=""true""]');
  const rawAssistants = Array.from(document.querySelectorAll('[data-message-role=""assistant""], [data-message-author-role=""assistant""], [data-turn=""assistant""]'));
  const rawUsers = Array.from(document.querySelectorAll('[data-message-role=""user""], [data-message-author-role=""user""], [data-turn=""user""]'));
  const rawLast = rawAssistants[rawAssistants.length - 1];
  const messages = role => {
    const primary = '[data-message-role=""' + role + '""], [data-message-author-role=""' + role + '""], [data-turn=""' + role + '""]';
    let nodes = query(document, primary).filter(visible);
    if (!nodes.length) nodes = query(document, role === 'assistant' ? '[data-talvt-turn-state]' : '[data-user-message-bubble]').filter(visible);
    if (!nodes.length && rules[role === 'assistant' ? 'AssistantSelector' : 'UserSelector']) nodes = query(document, rules[role === 'assistant' ? 'AssistantSelector' : 'UserSelector']).filter(visible);
    return nodes.filter(e => !nodes.some(other => other !== e && other.contains(e)));
  };
  const roots = messages('assistant');
  const last = roots[roots.length - 1];
  const users = messages('user');
  const user = users[users.length - 1];
  const pairedTurn = !!last && last.hasAttribute('data-talvt-turn-state') && !!user && last.contains(user);
  const afterUser = !!last && (pairedTurn || !user || !!(user.compareDocumentPosition(last) & Node.DOCUMENT_POSITION_FOLLOWING));
  let scope = last;
  for (let i = 0; scope && scope.parentElement && i < 8; i++) {
    const parent = scope.parentElement;
    if (parent === document.body || parent.tagName === 'MAIN' || parent.getAttribute('role') === 'main' ||
        (user && parent.contains(user)) || roots.some(other => other !== last && parent.contains(other))) break;
    scope = parent;
    if (scope.tagName === 'ARTICLE' || scope.hasAttribute('data-turn-id')) break;
  }
  const candidates = last ? query(last, rules.ContentSelector || '[data-assistant-markdown-root], .markdown, [data-message-content]').filter(e => visible(e) && !e.closest('[data-user-message-bubble], [class~=""group/user-message""]')) : [];
  const bodies = candidates.filter(e => !candidates.some(other => other !== e && other.contains(e)));
  const content = bodies[bodies.length - 1] || last;
  const readText = (e, assistantOnly) => {
    if (!e) return '';
    const clone = e.cloneNode(true);
    if (assistantOnly) clone.querySelectorAll('[data-user-message-bubble], [class~=""group/user-message""], .turn-action-controls').forEach(n => n.remove());
    clone.querySelectorAll('button, [role=""button""], svg, script, style, [aria-hidden=""true""], .sr-only').forEach(n => n.remove());
    clone.querySelectorAll('br').forEach(n => n.replaceWith(document.createTextNode('\n')));
    clone.querySelectorAll('p, div, li, pre, h1, h2, h3, h4, h5, h6, tr').forEach(n => { n.prepend(document.createTextNode('\n')); n.append(document.createTextNode('\n')); });
    return clone.textContent.trim();
  };
  const stop = query(document, rules.StopSelector || '[data-testid=""stop-button""], [data-testid=""stop-generating-button""], button[aria-label^=""Stop"" i], button[aria-label*=""Dừng"" i]').some(visible);
  const containsSignal = selector => !!scope && (scope.matches(selector) || query(scope, selector).some(e => visible(e) && !e.closest('[data-user-message-bubble], [class~=""group/user-message""]')));
  const streaming = containsSignal('[aria-busy=""true""], [data-is-streaming=""true""], [data-state=""streaming""], .result-streaming');
  const complete = containsSignal('[data-message-complete]:not([data-message-complete=""false""]), [data-is-streaming=""false""]');
  const actions = containsSignal(rules.ActionSelector || '[data-testid=""copy-turn-action-button""], [data-testid=""copy-message-button""], button[aria-label^=""Copy"" i], button[aria-label^=""Sao chép"" i], button[aria-label=""Good response"" i], button[aria-label=""Bad response"" i], button[aria-label^=""Read aloud"" i]');
" + BuildEditorScript(string.Empty) + @"
  const editor = findEditor();
  const composer = editor && (editor.closest('form') || editor.parentElement);
  const idleComposer = !!composer && Array.from(composer.querySelectorAll('button')).some(e => visible(e) && !e.disabled && e.getAttribute('aria-disabled') !== 'true' &&
    (e.id === 'composer-submit-button' || e.hasAttribute('data-composer-voice-start') || ['send-button', 'composer-speech-button', 'voice-mode-button'].includes(e.getAttribute('data-testid')) || /^(?:send|gửi|start voice|start voice mode|bắt đầu.*thoại)/i.test(e.getAttribute('aria-label') || '')));
  const describe = e => ({tag:e.tagName, classes:typeof e.className === 'string' ? e.className.slice(0,350) : '', attributeNames:Array.from(e.attributes).map(a=>a.name), role:e.getAttribute('role')});
  const structure = " + (captureStructure ? "true" : "false") + @" ? {
    articles:document.querySelectorAll('article').length, markdown:document.querySelectorAll('.markdown, .prose, [data-assistant-markdown-root]').length,
    candidates:query(document, 'main article, [data-testid^=""conversation-turn""], .markdown, .prose, .whitespace-pre-wrap, [class*=""message""], [class*=""Message""]').filter(visible).slice(-18).map(describe),
    buttons:query(document, 'button').filter(visible).slice(-18).map(b=>{
      const label=(b.getAttribute('aria-label')||'')+' '+(b.getAttribute('title')||'')+' '+(b.getAttribute('data-testid')||'');
      const kind=/copy|sao chép/i.test(label)?'copy':/stop|dừng/i.test(label)?'stop':/voice|thoại/i.test(label)?'voice':/good response|bad response/i.test(label)?'feedback':'other';
      const parents=[];for(let p=b.parentElement,i=0;p&&p!==document.body&&i<5;p=p.parentElement,i++)parents.push(describe(p));
      return {kind:kind,button:describe(b),svgClasses:query(b,'svg').map(s=>s.getAttribute('class')),parents:parents};
    })
  } : null;
  return JSON.stringify({ count: roots.length, id: last ? (last.getAttribute('data-message-id') || last.getAttribute('data-turn-id') || last.id || String(roots.length)) : '',
    text: readText(content, true).slice(0, 200000), userText: readText(user, false).slice(0, 20000), afterUser: afterUser,
    complete: complete, canInferComplete: afterUser && (actions || idleComposer), busy: stop || streaming,
    readerSucceeded: true, assistantMarkers: rawAssistants.length, userMarkers: rawUsers.length,
    stopVisible: stop, streaming: streaming, actionsReady: actions, composerIdle: idleComposer,
    lastMarkerHasBox: !!rawLast && !!rawLast.getClientRects().length,
    lastMarkerAriaHidden: !!rawLast && !!rawLast.closest('[aria-hidden=""true""]'), structure:structure, customSelectors:Object.keys(rules).length>0 });
})()";
		}

		private static Dictionary<string, string> LoadReplySelectors()
		{
			try
			{
				var targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatGptDiagnostics.target");
				if (!File.Exists(targetFile)) return null;
				var target = File.ReadAllText(targetFile).Trim();
				if (Path.GetFileName(target) != "chatgpt-reply-diagnostics.json") return null;
				var path = Path.Combine(Path.GetDirectoryName(target), "chatgpt-reply-selectors.json");
				if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
				var rules = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
				if (rules == null) return null;
				var allowed = new[] { "AssistantSelector", "UserSelector", "ContentSelector", "ActionSelector", "StopSelector" };
				foreach (var entry in rules)
					if (Array.IndexOf(allowed, entry.Key) < 0 || string.IsNullOrWhiteSpace(entry.Value) || entry.Value.Length > 2000) return null;
				return rules;
			}
			catch (Exception) { return null; }
		}

		private void RecordReplyDiagnostics(ConversationSnapshot current, ConversationSnapshot before, string question, bool isNew, ref string previous)
		{
			try
			{
				var state = JsonConvert.SerializeObject(new {
					Build = typeof(ChatGptAssistant).Assembly.ManifestModule.ModuleVersionId.ToString("N"),
					current.ReaderSucceeded, current.AssistantMarkers, current.UserMarkers, current.Count,
					BaselineCount = before.Count, IsNew = isNew, current.AfterUser,
					HasText = !string.IsNullOrWhiteSpace(current.Text), TextLength = current.Text?.Length ?? 0,
					UserMatchesQuestion = !string.IsNullOrEmpty(current.UserText) && QuestionAnswerFormatter.PlainText(current.UserText).Contains(QuestionAnswerFormatter.PlainText(question)),
					current.Complete, current.CanInferComplete, current.Busy, current.StopVisible, current.Streaming,
					current.ActionsReady, current.ComposerIdle, current.LastMarkerHasBox, current.LastMarkerAriaHidden,
					current.Structure, current.CustomSelectors
				});
				if (state == previous) return;
				previous = state;
				diagnostic?.Invoke("ReplyReader " + state);
				var targetFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ChatGptDiagnostics.target");
				if (!File.Exists(targetFile)) return;
				var target = File.ReadAllText(targetFile).Trim();
				if (!Path.GetFileName(target).Equals("chatgpt-reply-diagnostics.json", StringComparison.OrdinalIgnoreCase)) return;
				File.WriteAllText(target, state, new UTF8Encoding(false));
			}
			catch (Exception) { diagnostic?.Invoke("Could not write reply reader diagnostics."); }
		}

		public void Reload()
		{
			if (disposed) return;
			loginAttemptCancellation?.Cancel();
			loginTask = null;
			loginFailure = null;
			manualLogin = false;
			navigationFailure = null;
			control.Load("https://chatgpt.com/");
		}

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			loginCancellation.Cancel();
			loginInput.Dispose();
			control.Dispose();
			requestContext.Dispose();
			loginAttemptCancellation?.Dispose();
			loginCancellation.Dispose();
		}

		internal static bool IsAllowedAddress(string address)
		{
			if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
			return uri.Host == "chatgpt.com" || uri.Host == "auth.chatgpt.com" || uri.Host == "chat.openai.com"
				|| (uri.Host == "www.google.com" || uri.Host == "www.recaptcha.net") && uri.AbsolutePath.StartsWith("/recaptcha/", StringComparison.Ordinal)
				|| ChatGptLogin.IsGoogleAddress(address)
				|| uri.Host == "cdn.platform.openai.com"
				|| uri.Host == "sentinel.openai.com"
				|| uri.Host == "auth.openai.com" || uri.Host.EndsWith(".auth.openai.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "auth0.openai.com"
				|| uri.Host == "challenges.cloudflare.com";
		}

		private sealed class ChatRequestHandler : CefSharp.Handler.RequestHandler
		{
			private readonly ChatGptAssistant assistant;
			private readonly Func<string, bool> allowNavigation;
			private readonly Func<string, bool> allowResource;

			internal ChatRequestHandler(ChatGptAssistant assistant, Func<string, bool> allowNavigation, Func<string, bool> allowResource)
			{
				this.assistant = assistant;
				this.allowNavigation = allowNavigation;
				this.allowResource = allowResource;
			}

			protected override bool OnBeforeBrowse(IWebBrowser webBrowser, IBrowser browser, IFrame frame, IRequest request,
				bool userGesture, bool isRedirect)
			{
				if (!frame.IsMain && (request.Url == "about:blank" || request.Url == "about:srcdoc")) return false;
				if (IsAllowedAddress(request.Url) && allowNavigation(request.Url))
				{
					assistant.navigationFailure = null;
					return false;
				}

				assistant.ReportFailure("Điều hướng tới " + HostOf(request.Url) + (IsAllowedAddress(request.Url)
					? " bị chặn bởi bộ lọc URL của phiên." : " nằm ngoài danh sách miền cho phép của khung ChatGPT."));
				return true;
			}

			protected override bool OnOpenUrlFromTab(IWebBrowser webBrowser, IBrowser browser, IFrame frame, string targetUrl,
				WindowOpenDisposition disposition, bool userGesture)
			{
				assistant.OpenAuthenticationPopup(targetUrl, allowNavigation);
				return true;
			}

			protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser webBrowser, IBrowser browser, IFrame frame,
				IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
			{
				return new ResourcePolicy(allowResource,
					url => assistant.ReportFailure("Bộ lọc URL của phiên chặn tài nguyên từ " + HostOf(url) + "."));
			}
		}

		private sealed class ChatLifeSpanHandler : CefSharp.Handler.LifeSpanHandler
		{
			private readonly ChatGptAssistant assistant;
			private readonly Func<string, bool> allowNavigation;

			internal ChatLifeSpanHandler(ChatGptAssistant assistant, Func<string, bool> allowNavigation)
			{
				this.assistant = assistant;
				this.allowNavigation = allowNavigation;
			}

			protected override bool OnBeforePopup(IWebBrowser webBrowser, IBrowser browser, IFrame frame, string targetUrl,
				string targetFrameName, WindowOpenDisposition disposition, bool userGesture, IPopupFeatures popupFeatures,
				IWindowInfo windowInfo, IBrowserSettings browserSettings, ref bool noJavascriptAccess, out IWebBrowser newBrowser)
			{
				newBrowser = null;
				assistant.OpenAuthenticationPopup(targetUrl, allowNavigation);
				return true;
			}
		}

		private void OpenAuthenticationPopup(string address, Func<string, bool> allowNavigation)
		{
			if (disposed) return;
			if (!IsAllowedAddress(address) || !allowNavigation(address))
			{
				ReportFailure("Cửa sổ đăng nhập tới " + HostOf(address) + " bị chặn bởi giới hạn miền hoặc bộ lọc URL của phiên.");
				return;
			}
			preview.Dispatcher.BeginInvoke((Action) (() => { if (!disposed) control.Load(address); }));
		}

		private sealed class ChatDownloadHandler : CefSharp.Handler.DownloadHandler
		{
			protected override bool CanDownload(IWebBrowser webBrowser, IBrowser browser, string url, string requestMethod) => false;

			protected override bool OnBeforeDownload(IWebBrowser webBrowser, IBrowser browser, DownloadItem item, IBeforeDownloadCallback callback)
			{
				callback.Dispose();
				return true;
			}
		}

		private sealed class ChatDialogHandler : CefSharp.Handler.DialogHandler
		{
			protected override bool OnFileDialog(IWebBrowser webBrowser, IBrowser browser, CefFileDialogMode mode, string title,
				string defaultFilePath, IReadOnlyCollection<string> filters, IReadOnlyCollection<string> extensions,
				IReadOnlyCollection<string> descriptions, IFileDialogCallback callback)
			{
				using (callback) callback.Cancel();
				return true;
			}
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
