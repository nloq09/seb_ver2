using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SafeExamBrowser.Browser
{
	internal sealed class ChatGptLogin
	{
		private static readonly string[] GoogleHosts = { "accounts.google.com", "accounts.google.com.vn" };
		public string Email { get; set; }
		public string Password { get; set; }
		public string Provider { get; set; } = "openai";
		internal bool Manual { get; set; }

		internal static ChatGptLogin LoadEnvironmentFile(string path)
		{
			if (!File.Exists(path)) return null;
			var login = new ChatGptLogin();
			foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
			{
				var entry = line.Trim();
				if (entry.Length == 0 || entry.StartsWith("#", StringComparison.Ordinal)) continue;
				var separator = entry.IndexOf('=');
				if (separator < 0) continue;
				var key = entry.Substring(0, separator).Trim();
				if (key != "CHATGPT_EMAIL" && key != "CHATGPT_PASSWORD" && key != "CHATGPT_LOGIN_PROVIDER") continue;
				var value = entry.Substring(separator + 1).Trim();
				if (value.StartsWith("\"", StringComparison.Ordinal) || value.StartsWith("'", StringComparison.Ordinal))
				{
					if (value.Length < 2 || value[value.Length - 1] != value[0])
						throw new InvalidDataException("Unclosed quote in ChatGPT login settings.");
					value = value.Substring(1, value.Length - 2);
				}
				if (key == "CHATGPT_EMAIL") login.Email = value;
				else if (key == "CHATGPT_LOGIN_PROVIDER") login.Provider = value;
				else login.Password = value;
			}
			return Validate(login);
		}

		internal static ChatGptLogin Load(string path)
		{
			if (!File.Exists(path)) return null;
			var data = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
			try
			{
				return Validate(JsonConvert.DeserializeObject<ChatGptLogin>(Encoding.UTF8.GetString(data)));
			}
			finally { Array.Clear(data, 0, data.Length); }
		}

		private static ChatGptLogin Validate(ChatGptLogin login)
		{
			if (string.IsNullOrWhiteSpace(login?.Email) || string.IsNullOrEmpty(login.Password))
				throw new InvalidDataException("Incomplete ChatGPT login settings.");
			login.Provider = (login.Provider ?? "openai").Trim().ToLowerInvariant();
			if (login.Provider != "openai" && login.Provider != "google")
				throw new InvalidDataException("Unsupported ChatGPT login provider.");
			return login;
		}

		internal static bool IsCredentialAddress(string address)
		{
			return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https"
				&& (uri.Host == "auth.openai.com" || uri.Host == "auth0.openai.com" || uri.Host == "auth.chatgpt.com");
		}

		internal static bool IsInteractiveAddress(string address)
		{
			if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return false;
			return IsCredentialAddress(address) || IsGoogleAddress(address)
				|| uri.Host == "chatgpt.com" && (uri.AbsolutePath == "/auth/login" || uri.AbsolutePath == "/auth/login/");
		}

		internal static bool IsGoogleAddress(string address)
		{
			return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https"
				&& Array.IndexOf(GoogleHosts, uri.Host) >= 0;
		}

		internal string BuildFormScript(bool emailSubmitted, bool passwordSubmitted)
		{
			var passwordValue = Provider == "google" ? null : Password;
			return @"(function () {
  if (location.protocol !== 'https:' || (location.hostname !== 'auth.openai.com' && location.hostname !== 'auth0.openai.com' && location.hostname !== 'auth.chatgpt.com')) return 'blocked';
  const allowed = url => url.protocol === 'https:' && ['auth.openai.com', 'auth0.openai.com', 'auth.chatgpt.com'].includes(url.hostname);
  if (/sign-up|signup|reset|forgot/i.test(location.pathname)) return 'blocked';
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden' && !e.disabled;
  const find = selector => Array.from(document.querySelectorAll(selector)).find(visible);
  if (" + (Manual ? "true" : "false") + @") return 'waiting';
  if (find('iframe[src*=""challenges.cloudflare.com""]')) return 'verification';
" + BuildGoogleChoiceScript() + @"
" + BuildPasswordAlternativeScript(passwordSubmitted) + @"
  if (find('input[aria-invalid=""true""]') || Array.from(document.querySelectorAll('[role=""alert""]')).some(e => visible(e) && e.textContent.trim())) return 'rejected';
  if (find('input[autocomplete=""new-password""]')) return 'blocked';
  const password = find('input[type=""password""]');
  if (password && " + (Provider == "google" ? "true" : "false") + @") return 'wrong-provider';
  const email = find('input[type=""email""], input[name=""username""], input[autocomplete=""username""]');
  const input = password || email;
  const form = input && input.closest('form');
  if (!form || !allowed(new URL(form.action || location.href, location.href))) return 'waiting';
  if (form.method.toLowerCase() !== 'post') return 'blocked';
  const fill = (element, value) => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(element, value);
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
  };
  let step = 'waiting';
  if (password && !" + (passwordSubmitted ? "true" : "false") + @") {
    if (email) fill(email, " + JsonConvert.ToString(Email) + @");
    fill(password, " + JsonConvert.ToString(passwordValue) + @");
    if (password.value !== " + JsonConvert.ToString(passwordValue) + @") return 'rejected';
    step = 'password';
  }
  else if (!password && email && !" + (emailSubmitted ? "true" : "false") + @") {
    fill(email, " + JsonConvert.ToString(Email) + @");
    if (email.value !== " + JsonConvert.ToString(Email) + @") return 'rejected';
    step = 'email';
  }
  if (step === 'waiting') return step;
  const button = Array.from(form.querySelectorAll('button[type=""submit""], input[type=""submit""]')).find(e =>
    visible(e) && /^(?:continue|next|log in|sign in|tiếp tục|đăng nhập)(?: with (?:email|password))?$/i.test((e.innerText || e.value || e.getAttribute('aria-label') || '').trim()));
  if (!button || button.getAttribute('aria-disabled') === 'true') return 'waiting';
  button.click();
  return step;
})()";
		}

		internal string BuildGoogleScript(bool emailSubmitted, bool passwordSubmitted)
		{
			if (Provider != "google") return "(function () { return 'wrong-provider'; })()";
			return @"(function () {
  if (location.protocol !== 'https:' || !" + JsonConvert.SerializeObject(GoogleHosts) + @".includes(location.hostname)) return 'blocked';
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden' && !e.disabled;
  const find = selector => Array.from(document.querySelectorAll(selector)).find(visible);
  if (/\/deniedsigninrejected|\/signin\/rejected/i.test(location.pathname) || /browser or app may not be secure|disallowed_useragent|trình duyệt hoặc ứng dụng này có thể không an toàn/i.test(document.body.innerText)) return 'unsupported-browser';
  if (/\/signin\/oauth\/error/i.test(location.pathname)) return 'provider-error';
  if (" + (Manual ? "true" : "false") + @") return 'waiting';
  if (/\/signup|\/recovery|\/AccountRecovery/i.test(location.pathname)) return 'blocked';
  if (find('input[autocomplete=""one-time-code""], input[name=""totpPin""], input[name=""idvPin""], input[name=""Pin""], input[name=""backupCodePin""], #captchaimg, iframe[src*=""recaptcha""]')) return 'verification';
  if (/\/challenge\//i.test(location.pathname) && !/\/challenge\/pwd\/?$/i.test(location.pathname)) return 'verification';
  if (find('input[aria-invalid=""true""]') || Array.from(document.querySelectorAll('[role=""alert""]')).some(e => visible(e) && e.textContent.trim())) return 'rejected';
  if (find('input[autocomplete=""new-password""]')) return 'blocked';
  if (!" + (emailSubmitted || passwordSubmitted ? "true" : "false") + @" && /accountchooser/i.test(location.pathname)) {
    const account = Array.from(document.querySelectorAll('[data-identifier], [data-email]')).find(e =>
      visible(e) && (e.getAttribute('data-identifier') || e.getAttribute('data-email') || '').trim().toLowerCase() === " + JsonConvert.ToString(Email.ToLowerInvariant()) + @");
    if (account) {
      if (account.hasAttribute('data-seb-account-selected')) return 'waiting';
      account.setAttribute('data-seb-account-selected', 'true');
      account.click();
      return 'account';
    }
    if (Array.from(document.querySelectorAll('[data-identifier], [data-email]')).some(visible)) return 'choose-account';
  }
  const password = find('input[type=""password""][name=""Passwd""], input[type=""password""][autocomplete=""current-password""]');
  const email = find('#identifierId, input[type=""email""][name=""identifier""]');
  const fill = (element, value) => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(element, value);
    element.dispatchEvent(new Event('input', { bubbles: true }));
    element.dispatchEvent(new Event('change', { bubbles: true }));
    return element.value === value;
  };
  if (password && !" + (passwordSubmitted ? "true" : "false") + @") {
    const identity = document.querySelector('#profileIdentifier, [data-email]');
    const account = identity && (identity.getAttribute('data-email') || identity.textContent || '').trim().toLowerCase();
    if (account !== " + JsonConvert.ToString(Email.ToLowerInvariant()) + @") return 'account-mismatch';
    if (!fill(password, " + JsonConvert.ToString(Password) + @")) return 'rejected';
    const button = find('#passwordNext button, button#passwordNext');
    if (!button || button.getAttribute('aria-disabled') === 'true') return 'waiting';
    button.click();
    return 'password';
  }
  if (!password && email && !" + (emailSubmitted ? "true" : "false") + @") {
    if (!fill(email, " + JsonConvert.ToString(Email) + @")) return 'rejected';
    const button = find('#identifierNext button, button#identifierNext');
    if (!button || button.getAttribute('aria-disabled') === 'true') return 'waiting';
    button.click();
    return 'email';
  }
  if (!email && !password && /consent|approval|challenge|accountchooser/i.test(location.pathname)) return 'verification';
  return 'waiting';
})()";
		}

		internal string BuildGatewayScript(bool emailSubmitted)
		{
			return @"(function () {
  if (location.protocol !== 'https:' || location.hostname !== 'chatgpt.com') return 'blocked';
  const visible = e => e && e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden' && !e.disabled;
  if (Array.from(document.querySelectorAll('[data-testid=""profile-button""], [data-testid=""accounts-profile-button""], [data-testid=""user-menu-button""]')).some(visible)) return 'signed-in';
  if (location.pathname !== '/auth/login' && location.pathname !== '/auth/login/') return 'waiting';
  if (" + (Manual ? "true" : "false") + @") return 'waiting';
  if (Array.from(document.querySelectorAll('iframe[src*=""challenges.cloudflare.com""]')).some(visible)) return 'verification';
" + BuildGoogleChoiceScript() + @"
" + BuildPasswordAlternativeScript(false) + @"
  if (Array.from(document.querySelectorAll('[role=""alert""]')).some(e => visible(e) && e.textContent.trim())) return 'rejected';
  const email = Array.from(document.querySelectorAll('input[type=""email""], input[name=""username""]')).find(visible);
  if (!email) return 'waiting';
  if (" + (emailSubmitted ? "true" : "false") + @") return 'waiting';
  const form = email.closest('form');
  if (!form) return 'waiting';
  const action = new URL(form.action || location.href, location.href);
  if (action.protocol !== 'https:' || action.hostname !== 'chatgpt.com' || !/^\/auth\/login\/?$/.test(action.pathname)) return 'blocked';
  Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(email, " + JsonConvert.ToString(Email) + @");
  email.dispatchEvent(new Event('input', { bubbles: true }));
  email.dispatchEvent(new Event('change', { bubbles: true }));
  if (email.value !== " + JsonConvert.ToString(Email) + @") return 'rejected';
  const button = Array.from(form.querySelectorAll('button[type=""submit""], input[type=""submit""]')).find(e =>
    visible(e) && /^(?:continue|next|log in|sign in|tiếp tục|đăng nhập)(?: with (?:email|password))?$/i.test((e.innerText || e.value || e.getAttribute('aria-label') || '').trim()));
  if (!button || button.getAttribute('aria-disabled') === 'true') return 'waiting';
  button.click();
  return 'email';
})()";
		}

		private string BuildPasswordAlternativeScript(bool passwordSubmitted)
		{
			return @"
  if (Array.from(document.querySelectorAll('input[autocomplete=""one-time-code""], input[name=""code""], input[name=""otp""]')).some(visible)) {
    if (" + (passwordSubmitted ? "true" : "false") + @") return 'verification';
    const alternative = Array.from(document.querySelectorAll('button, input[type=""submit""], a[href]')).find(e =>
      visible(e) && e.getAttribute('aria-disabled') !== 'true' && /^(?:continue with password|tiếp tục (?:bằng|với) mật khẩu)$/i.test((e.innerText || e.value || e.getAttribute('aria-label') || '').trim()));
    if (!alternative) return 'verification';
    if (" + (Provider == "google" ? "true" : "false") + @") return 'wrong-provider';
    const form = alternative.form || alternative.closest('form');
    const target = new URL(alternative.getAttribute('href') || alternative.getAttribute('formaction') || (form && form.action) || location.href, location.href);
    if (target.protocol !== 'https:' || !['chatgpt.com', 'auth.openai.com', 'auth0.openai.com', 'auth.chatgpt.com'].includes(target.hostname)) return 'blocked';
    if (alternative.hasAttribute('data-seb-password-selected')) return 'waiting';
    alternative.setAttribute('data-seb-password-selected', 'true');
    alternative.click();
    return 'password-option';
  }
";
		}

		private string BuildGoogleChoiceScript()
		{
			if (Provider != "google") return string.Empty;
			return @"
  const google = Array.from(document.querySelectorAll('button, a[href], input[type=""submit""]')).find(e =>
    visible(e) && e.getAttribute('aria-disabled') !== 'true' && /^(?:(?:continue|sign in|log in) with google|(?:tiếp tục|đăng nhập) (?:bằng|với) google)$/i.test((e.innerText || e.value || e.getAttribute('aria-label') || '').trim()));
  if (!google) return 'google-unavailable';
  const googleForm = google.form || google.closest('form');
  const googleTarget = new URL(google.getAttribute('href') || google.getAttribute('formaction') || (googleForm && googleForm.action) || location.href, location.href);
  if (googleTarget.protocol !== 'https:' || !['chatgpt.com', 'auth.openai.com', 'auth0.openai.com', 'auth.chatgpt.com'].concat(" + JsonConvert.SerializeObject(GoogleHosts) + @").includes(googleTarget.hostname)) return 'blocked';
  if (google.hasAttribute('data-seb-google-selected')) return 'waiting';
  google.setAttribute('data-seb-google-selected', 'true');
  google.click();
  return 'google-option';
";
		}
	}
}
