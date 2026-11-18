using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SafeExamBrowser.Browser.UnitTests
{
	[TestClass]
	public class ChatGptNavigationTests
	{
		[TestMethod]
		public void MustAllowChatAndEmbeddedAuthentication()
		{
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://chatgpt.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://auth.openai.com/log-in"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://login.auth.openai.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://auth.chatgpt.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://accounts.google.com/o/oauth2/v2/auth"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://accounts.google.com.vn/RotateCookiesPage"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://cdn.platform.openai.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://sentinel.openai.com/"));
			Assert.IsFalse(ChatGptLogin.IsCredentialAddress("https://sentinel.openai.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://challenges.cloudflare.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://www.google.com/recaptcha/api2/anchor"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://www.recaptcha.net/recaptcha/api2/anchor"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://www.google.com/search?q=test"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://www.google.com.example.com/recaptcha/api2/anchor"));
		}

		[TestMethod]
		public void MustBlockExternalNavigationAndLookalikeHosts()
		{
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://accounts.google.com.vn.example.com/"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://accounts.google.com.vn@example.com/"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("http://accounts.google.com.vn/"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://sentinel.openai.com.example.com/"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("https://sentinel.openai.com@example.com/"));
			Assert.IsFalse(ChatGptAssistant.IsAllowedAddress("http://sentinel.openai.com/"));
			foreach (var address in new[] { "https://example.com", "https://chatgpt.com.example.com", "https://accounts.google.com.example.com", "https://accounts.google.com@example.com", "http://accounts.google.com", "https://auth.openai.com.example.com", "https://fakeauth.openai.com", "https://chatgpt.com@example.com", "http://chatgpt.com", "file:///C:/Windows/win.ini", "javascript:alert(1)", "not a URL" })
				Assert.IsFalse(ChatGptAssistant.IsAllowedAddress(address), address);
		}
	}
}
