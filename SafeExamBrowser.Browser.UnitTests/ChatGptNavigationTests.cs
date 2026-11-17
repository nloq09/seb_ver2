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
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://cdn.platform.openai.com/"));
			Assert.IsTrue(ChatGptAssistant.IsAllowedAddress("https://challenges.cloudflare.com/"));
		}

		[TestMethod]
		public void MustBlockExternalNavigationAndLookalikeHosts()
		{
			foreach (var address in new[] { "https://example.com", "https://chatgpt.com.example.com", "https://accounts.google.com.example.com", "https://accounts.google.com@example.com", "http://accounts.google.com", "https://auth.openai.com.example.com", "https://fakeauth.openai.com", "https://chatgpt.com@example.com", "http://chatgpt.com", "file:///C:/Windows/win.ini", "javascript:alert(1)", "not a URL" })
				Assert.IsFalse(ChatGptAssistant.IsAllowedAddress(address), address);
		}
	}
}
