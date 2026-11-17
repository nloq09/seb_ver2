using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Browser;
using SafeExamBrowser.Settings.Security;

namespace SafeExamBrowser.Browser.UnitTests
{
	[TestClass]
	public class AnswerClipboardTests
	{
		[TestMethod]
		public async Task MustCopyUnicodeIntoExistingIsolatedClipboard()
		{
			var clipboard = new Clipboard(Mock.Of<ILogger>(), new BrowserSettings { UseIsolatedClipboard = true }, ClipboardPolicy.Isolated);
			var updates = 0;
			clipboard.Changed += id => updates++;
			Assert.IsTrue(await clipboard.CopyAnswerAsync("Câu trả lời\n2 + 2 = 4."));
			Assert.AreEqual("Câu trả lời\n2 + 2 = 4.", Encoding.UTF8.GetString(Convert.FromBase64String(clipboard.Content)));
			Assert.AreEqual(1, updates);
		}

		[TestMethod]
		public async Task MustRespectBlockedClipboardEvenIfBrowserFlagIsInconsistent()
		{
			var clipboard = new Clipboard(Mock.Of<ILogger>(), new BrowserSettings { UseIsolatedClipboard = true }, ClipboardPolicy.Block);
			Assert.IsFalse(await clipboard.CopyAnswerAsync("Không được sao chép"));
			Assert.IsNull(clipboard.Content);
		}

		[TestMethod]
		public async Task MustNotFallBackToSystemClipboardForIsolatedOrUnspecifiedPolicy()
		{
			var isolated = new Clipboard(Mock.Of<ILogger>(), new BrowserSettings { UseIsolatedClipboard = false }, ClipboardPolicy.Isolated);
			var unspecified = new Clipboard(Mock.Of<ILogger>(), new BrowserSettings { UseIsolatedClipboard = false });
			var unknown = new Clipboard(Mock.Of<ILogger>(), new BrowserSettings { UseIsolatedClipboard = false }, (ClipboardPolicy)99);
			Assert.IsFalse(await isolated.CopyAnswerAsync("Answer"));
			Assert.IsFalse(await unspecified.CopyAnswerAsync("Answer"));
			Assert.IsFalse(await unknown.CopyAnswerAsync("Answer"));
		}
	}
}
