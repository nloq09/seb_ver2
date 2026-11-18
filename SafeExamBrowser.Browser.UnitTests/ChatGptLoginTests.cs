using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SafeExamBrowser.Browser.UnitTests
{
	[TestClass]
	public class ChatGptLoginTests
	{
		[TestMethod]
		[DataRow("CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=p=a#ss", "p=a#ss")]
		[DataRow("# comment\n\nCHATGPT_EMAIL = 'test@example.invalid'\nCHATGPT_PASSWORD = \"  p=$#\\ss  \"", "  p=$#\\ss  ")]
		[DataRow("UNRELATED=value\nCHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD='Tiếng Việt'", "Tiếng Việt")]
		[DataRow("CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=old\nCHATGPT_PASSWORD=new", "new")]
		public void MustReadEnvironmentLoginWithoutChangingPassword(string content, string password)
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
			try
			{
				Assert.IsNull(ChatGptLogin.LoadEnvironmentFile(path));
				File.WriteAllText(path, content, new UTF8Encoding(true));
				var login = ChatGptLogin.LoadEnvironmentFile(path);
				Assert.AreEqual("test@example.invalid", login.Email);
				Assert.AreEqual(password, login.Password);
			}
			finally { File.Delete(path); }
		}

		[TestMethod]
		[DataRow("CHATGPT_EMAIL=test@example.invalid")]
		[DataRow("CHATGPT_EMAIL=\nCHATGPT_PASSWORD=fixture")]
		[DataRow("CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=\"\"")]
		[DataRow("CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=\"unclosed")]
		[DataRow("CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=fixture\nCHATGPT_LOGIN_PROVIDER=other")]
		public void MustRejectIncompleteOrMalformedEnvironmentLogin(string content)
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
			try
			{
				File.WriteAllText(path, content);
				Assert.ThrowsExactly<InvalidDataException>(() => ChatGptLogin.LoadEnvironmentFile(path));
			}
			finally { File.Delete(path); }
		}

		[TestMethod]
		public void MustReadGoogleProviderFromEnvironmentFile()
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
			try
			{
				File.WriteAllText(path, "CHATGPT_EMAIL=test@example.invalid\nCHATGPT_PASSWORD=fixture\nCHATGPT_LOGIN_PROVIDER=Google");
				Assert.AreEqual("google", ChatGptLogin.LoadEnvironmentFile(path).Provider);
			}
			finally { File.Delete(path); }
		}

		[TestMethod]
		public void MustUsePasswordsOnlyWithTheSelectedProvider()
		{
			var login = new ChatGptLogin { Email = "test@example.invalid", Password = "fixture-sensitive-unique", Provider = "google" };
			Assert.IsFalse(login.BuildFormScript(false, false).Contains(login.Password));
			Assert.IsTrue(login.BuildGoogleScript(false, false).Contains(login.Password));
			login.Provider = "openai";
			Assert.IsFalse(login.BuildGoogleScript(false, false).Contains(login.Password));
			Assert.IsTrue(login.BuildFormScript(false, false).Contains(login.Password));
		}

		[TestMethod]
		public void MustReadEncryptedLoginForCurrentWindowsUser()
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
			try
			{
				Assert.IsNull(ChatGptLogin.Load(path));
				var data = Encoding.UTF8.GetBytes("{\"Email\":\"test@example.invalid\",\"Password\":\"fixture-password\"}");
				var encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
				File.WriteAllBytes(path, encrypted);
				var login = ChatGptLogin.Load(path);
				Assert.AreEqual("test@example.invalid", login.Email);
				Assert.AreEqual("fixture-password", login.Password);
				Assert.IsFalse(Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("fixture-password"));
			}
			finally { File.Delete(path); }
		}

		[TestMethod]
		public void MustRejectIncompleteEncryptedLogin()
		{
			var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
			try
			{
				File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Email\":\"test@example.invalid\"}"), null, DataProtectionScope.CurrentUser));
				Assert.ThrowsExactly<InvalidDataException>(() => ChatGptLogin.Load(path));
			}
			finally { File.Delete(path); }
		}

		[TestMethod]
		public void MustKeepPasswordOutOfTheLoginGateway()
		{
			var login = new ChatGptLogin { Email = "test@example.invalid", Password = "fixture-sensitive-unique" };
			var script = login.BuildGatewayScript(false);
			Assert.IsTrue(script.Contains(login.Email));
			Assert.IsFalse(script.Contains(login.Password));
		}

		[TestMethod]
		public void MustRestrictCredentialEntryToAuthenticationOrigins()
		{
			Assert.IsTrue(ChatGptLogin.IsCredentialAddress("https://auth.openai.com/log-in"));
			Assert.IsTrue(ChatGptLogin.IsCredentialAddress("https://auth.chatgpt.com/log-in/password"));
			foreach (var address in new[] { "https://chatgpt.com/", "https://accounts.google.com/", "http://auth.openai.com/", "https://auth.openai.com.example.com/", "https://auth.openai.com@example.com/", "invalid" })
				Assert.IsFalse(ChatGptLogin.IsCredentialAddress(address));
		}

		[TestMethod]
		public void MustRestrictHumanInputToLoginPages()
		{
			Assert.IsTrue(ChatGptLogin.IsInteractiveAddress("https://accounts.google.com.vn/v3/signin/challenge/totp"));
			Assert.IsFalse(ChatGptLogin.IsInteractiveAddress("https://accounts.google.com.vn.example.com/"));
			foreach (var address in new[] { "https://accounts.google.com/v3/signin/challenge/totp", "https://auth.openai.com/email-verification", "https://auth.chatgpt.com/login", "https://chatgpt.com/auth/login" })
				Assert.IsTrue(ChatGptLogin.IsInteractiveAddress(address), address);
			foreach (var address in new[] { "https://chatgpt.com/", "https://chatgpt.com/c/test", "https://chatgpt.com/auth/login/other", "https://sentinel.openai.com/", "http://accounts.google.com/", "https://accounts.google.com.example.com/", "file:///C:/test.html" })
				Assert.IsFalse(ChatGptLogin.IsInteractiveAddress(address), address);
		}

		[TestMethod]
		[DataRow("https://accounts.google.com/v3/signin", true)]
		[DataRow("https://accounts.google.com.vn/v3/signin", true)]
		[DataRow("https://accounts.google.com.vn.example.com/", false)]
		[DataRow("https://accounts.google.com.vn@example.com/", false)]
		[DataRow("http://accounts.google.com.vn/", false)]
		[DataRow("https://fakeaccounts.google.com.vn/", false)]
		[DataRow("https://accounts.google.example/", false)]
		public void MustRecognizeOnlySupportedGoogleAccountOrigins(string address, bool expected)
		{
			Assert.AreEqual(expected, ChatGptLogin.IsGoogleAddress(address), address);
		}

		[TestMethod]
		[DataRow(100.0, 150.0, 200.0, 300.0, 400, 600, 200, 300)]
		[DataRow(150.0, 150.0, 300.0, 300.0, 100, 200, 50, 100)]
		[DataRow(150.0, 150.0, 300.0, 300.0, 200, 100, 100, 50)]
		public void MustMapAuthenticationInputThroughUniformScaling(double x, double y, double width, double height, int browserWidth, int browserHeight, int expectedX, int expectedY)
		{
			Assert.IsTrue(ChatGptLoginInput.TryMapPoint(x, y, width, height, browserWidth, browserHeight, out var mappedX, out var mappedY));
			Assert.AreEqual(expectedX, mappedX);
			Assert.AreEqual(expectedY, mappedY);
		}

		[TestMethod]
		public void MustRejectClicksOutsideAuthenticationImage()
		{
			Assert.IsFalse(ChatGptLoginInput.TryMapPoint(10, 150, 300, 300, 100, 200, out _, out _));
			Assert.IsFalse(ChatGptLoginInput.TryMapPoint(150, 10, 300, 300, 200, 100, out _, out _));
			Assert.IsFalse(ChatGptLoginInput.TryMapPoint(150, 150, 0, 300, 200, 100, out _, out _));
			Assert.IsFalse(ChatGptLoginInput.TryMapPoint(150, 150, 300, 300, 0, 100, out _, out _));
		}

		[TestMethod]
		public void MustCheckChatReadinessOnlyOnChatContentPages()
		{
			foreach (var address in new[] { "https://chatgpt.com/", "https://chatgpt.com/c/fixture", "https://chat.openai.com/" })
				Assert.IsTrue(ChatGptAssistant.IsChatContentAddress(address), address);
			foreach (var address in new[] { "https://chatgpt.com/auth", "https://chatgpt.com/auth/login", "https://chatgpt.com/api/auth/session", "https://accounts.google.com/", "https://chatgpt.com.example.com/", "http://chatgpt.com/", "invalid" })
				Assert.IsFalse(ChatGptAssistant.IsChatContentAddress(address), address);
		}
	}
}
