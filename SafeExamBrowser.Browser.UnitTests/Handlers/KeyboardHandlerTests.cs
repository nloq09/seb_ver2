/*
 * Copyright (c) 2025 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System.Windows.Forms;
using CefSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.Browser.Handlers;

namespace SafeExamBrowser.Browser.UnitTests.Handlers
{
	[TestClass]
	public class KeyboardHandlerTests
	{
		private KeyboardHandler sut;

		[TestInitialize]
		public void Initialize()
		{
			sut = new KeyboardHandler();
		}

		[TestMethod]
		public void MustLoginOnlyOnItsOwnShortcutWithoutSending()
		{
			var logins = 0;
			var shortcut = false;
			var modifiers = CefEventFlags.ControlDown | CefEventFlags.ShiftDown;
			sut.ChatGptLoginRequested += () => logins++;
			sut.ChatGptRequested += () => Assert.Fail("Login must not use the open action");
			sut.ChatGptQueryRequested += () => Assert.Fail("Login must not send a question");
			sut.ChatGptEssayRequested += () => Assert.Fail("Login must not send an essay");
			sut.ChatGptHideAnswerRequested += () => Assert.Fail("Login must not hide an answer");
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 0, modifiers, false, ref shortcut));
			sut.EnableChatGptShortcut = true;
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 0, CefEventFlags.ControlDown, false, ref shortcut));
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 0, modifiers | CefEventFlags.AltDown, false, ref shortcut));
			Assert.AreEqual(0, logins);
			Assert.IsTrue(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 0, modifiers, false, ref shortcut));
			Assert.IsTrue(shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.KeyDown, (int) Keys.U, 0, modifiers, false, ref shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 1 << 30, modifiers, false, ref shortcut);
			sut.OnKeyEvent(null, null, KeyType.KeyUp, (int) Keys.U, 0, modifiers, false);
			Assert.AreEqual(1, logins);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.U, 0, modifiers, false, ref shortcut);
			Assert.AreEqual(2, logins);
		}

		[TestMethod]
		public void MustHideChoiceAnswerWithoutOpeningOrSending()
		{
			var hidden = 0;
			var shortcut = false;
			var modifiers = CefEventFlags.ControlDown | CefEventFlags.ShiftDown;
			sut.EnableChatGptShortcut = true;
			sut.ChatGptRequested += () => Assert.Fail("Unexpected open action");
			sut.ChatGptQueryRequested += () => Assert.Fail("Unexpected send action");
			sut.ChatGptEssayRequested += () => Assert.Fail("Unexpected essay action");
			sut.ChatGptHideAnswerRequested += () => hidden++;

			Assert.IsTrue(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.H, 0, modifiers, false, ref shortcut));
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.H, 1 << 30, modifiers, false, ref shortcut);
			Assert.AreEqual(1, hidden);
		}

		[TestMethod]
		public void MustSeparateOpenFromSelectedQuestionSubmission()
		{
			var opened = 0;
			var queried = 0;
			var shortcut = false;
			var modifiers = CefEventFlags.ControlDown | CefEventFlags.ShiftDown;
			sut.EnableChatGptShortcut = true;
			sut.ChatGptRequested += () => opened++;
			sut.ChatGptQueryRequested += () => queried++;
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, modifiers, false, ref shortcut);
			Assert.AreEqual(1, opened);
			Assert.AreEqual(0, queried);
			sut.OnPreKeyEvent(null, null, KeyType.KeyUp, (int) Keys.J, 0, modifiers, false, ref shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.K, 0, modifiers, false, ref shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.K, 1 << 30, modifiers, false, ref shortcut);
			Assert.AreEqual(1, opened);
			Assert.AreEqual(1, queried);
		}

		[TestMethod]
		[DataRow((int) Keys.K)]
		[DataRow((int) Keys.Y)]
		public void MustSendOnlyTheRequestedQuestionKindOnce(int key)
		{
			var multipleChoice = 0;
			var essay = 0;
			var shortcut = false;
			var modifiers = CefEventFlags.ControlDown | CefEventFlags.ShiftDown;
			sut.EnableChatGptShortcut = true;
			sut.ChatGptRequested += () => Assert.Fail("Unexpected open action");
			sut.ChatGptHideAnswerRequested += () => Assert.Fail("Unexpected hide action");
			sut.ChatGptQueryRequested += () => multipleChoice++;
			sut.ChatGptEssayRequested += () => essay++;
			sut.FocusAddressBarRequested += () => Assert.Fail("Submission must keep focus in the question");

			Assert.IsTrue(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, key, 0, modifiers, false, ref shortcut));
			Assert.IsTrue(shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.KeyDown, key, 0, modifiers, false, ref shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, key, 1 << 30, modifiers, false, ref shortcut);
			sut.OnKeyEvent(null, null, KeyType.KeyUp, key, 0, modifiers, false);
			Assert.AreEqual(key == (int) Keys.K ? 1 : 0, multipleChoice);
			Assert.AreEqual(key == (int) Keys.Y ? 1 : 0, essay);
		}

		[TestMethod]
		public void MustIgnoreDisabledOrModifiedEssayShortcut()
		{
			var shortcut = false;
			sut.ChatGptEssayRequested += () => Assert.Fail("Unexpected essay action");
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.Y, 0, CefEventFlags.ControlDown | CefEventFlags.ShiftDown, false, ref shortcut));
			sut.EnableChatGptShortcut = true;
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.Y, 0, CefEventFlags.ControlDown, false, ref shortcut));
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.Y, 0, CefEventFlags.ControlDown | CefEventFlags.ShiftDown | CefEventFlags.AltDown, false, ref shortcut));
		}

		[TestMethod]
		public void MustPreserveAddressBarShortcut()
		{
			var focused = 0;
			sut.EnableChatGptShortcut = true;
			sut.FocusAddressBarRequested += () => focused++;
			sut.OnKeyEvent(null, null, KeyType.KeyUp, (int) Keys.L, 0, CefEventFlags.ControlDown, false);
			Assert.AreEqual(1, focused);
			sut.EnableChatGptShortcut = false;
			sut.OnKeyEvent(null, null, KeyType.KeyUp, (int) Keys.L, 0, CefEventFlags.ControlDown | CefEventFlags.ShiftDown, false);
			Assert.AreEqual(2, focused);
		}

		[TestMethod]
		public void MustOpenChatGptOncePerShortcutPress()
		{
			var count = 0;
			var shortcut = false;
			var modifiers = CefEventFlags.ControlDown | CefEventFlags.ShiftDown;
			sut.EnableChatGptShortcut = true;
			sut.ChatGptRequested += () => count++;
			Assert.IsTrue(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, modifiers, false, ref shortcut));
			Assert.IsTrue(shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.KeyDown, (int) Keys.J, 0, modifiers, false, ref shortcut);
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 1 << 30, modifiers, false, ref shortcut);
			Assert.AreEqual(1, count);
			// Focus changed before key-up, so Chromium never saw it.
			sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, modifiers, false, ref shortcut);
			Assert.AreEqual(2, count);
		}

		[TestMethod]
		public void MustNotTreatOtherKeyCombinationsAsChatGptShortcut()
		{
			var count = 0;
			var shortcut = false;
			sut.ChatGptRequested += () => count++;
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, CefEventFlags.ControlDown | CefEventFlags.ShiftDown, false, ref shortcut));
			sut.EnableChatGptShortcut = true;
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, CefEventFlags.ControlDown, false, ref shortcut));
			Assert.IsFalse(sut.OnPreKeyEvent(null, null, KeyType.RawKeyDown, (int) Keys.J, 0, CefEventFlags.ControlDown | CefEventFlags.ShiftDown | CefEventFlags.AltDown, false, ref shortcut));
			Assert.AreEqual(0, count);
		}

		[TestMethod]
		public void MustDetectFindCommand()
		{
			var findRequested = false;

			sut.FindRequested += () => findRequested = true;

			var handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.F, default(int), CefEventFlags.ControlDown, default(bool));

			Assert.IsTrue(findRequested);
			Assert.IsFalse(handled);

			findRequested = false;
			handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), default(KeyType), default(int), default(int), CefEventFlags.ControlDown, default(bool));

			Assert.IsFalse(findRequested);
			Assert.IsFalse(handled);
		}

		[TestMethod]
		public void MustDetectHomeNavigationCommand()
		{
			var homeRequested = false;

			sut.HomeNavigationRequested += () => homeRequested = true;

			var handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.Home, default(int), default(CefEventFlags), default(bool));

			Assert.IsTrue(homeRequested);
			Assert.IsFalse(handled);

			homeRequested = false;
			handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), default(KeyType), default(int), default(int), default(CefEventFlags), default(bool));

			Assert.IsFalse(homeRequested);
			Assert.IsFalse(handled);
		}

		[TestMethod]
		public void MustDetectReloadCommand()
		{
			var isShortcut = default(bool);
			var reloadRequested = false;

			sut.ReloadRequested += () => reloadRequested = true;

			var handled = sut.OnPreKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.F5, default(int), default(CefEventFlags), default(bool), ref isShortcut);

			Assert.IsTrue(reloadRequested);
			Assert.IsTrue(handled);

			reloadRequested = false;
			handled = sut.OnPreKeyEvent(default(IWebBrowser), default(IBrowser), default(KeyType), default(int), default(int), default(CefEventFlags), default(bool), ref isShortcut);

			Assert.IsFalse(reloadRequested);
			Assert.IsFalse(handled);
		}

		[TestMethod]
		public void MustDetectZoomInCommand()
		{
			var zoomIn = false;
			var zoomOut = false;
			var zoomReset = false;

			sut.ZoomInRequested += () => zoomIn = true;
			sut.ZoomOutRequested += () => zoomOut = true;
			sut.ZoomResetRequested += () => zoomReset = true;

			var handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.Add, default(int), CefEventFlags.ControlDown, false);

			Assert.IsFalse(handled);
			Assert.IsTrue(zoomIn);
			Assert.IsFalse(zoomOut);
			Assert.IsFalse(zoomReset);

			zoomIn = false;
			handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.D1, default(int), CefEventFlags.ControlDown | CefEventFlags.ShiftDown, false);

			Assert.IsFalse(handled);
			Assert.IsTrue(zoomIn);
			Assert.IsFalse(zoomOut);
			Assert.IsFalse(zoomReset);
		}

		[TestMethod]
		public void MustDetectZoomOutCommand()
		{
			var zoomIn = false;
			var zoomOut = false;
			var zoomReset = false;

			sut.ZoomInRequested += () => zoomIn = true;
			sut.ZoomOutRequested += () => zoomOut = true;
			sut.ZoomResetRequested += () => zoomReset = true;

			var handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.Subtract, default(int), CefEventFlags.ControlDown, false);

			Assert.IsFalse(handled);
			Assert.IsFalse(zoomIn);
			Assert.IsTrue(zoomOut);
			Assert.IsFalse(zoomReset);

			zoomOut = false;
			handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.OemMinus, default(int), CefEventFlags.ControlDown, false);

			Assert.IsFalse(handled);
			Assert.IsFalse(zoomIn);
			Assert.IsTrue(zoomOut);
			Assert.IsFalse(zoomReset);
		}

		[TestMethod]
		public void MustDetectZoomResetCommand()
		{
			var zoomIn = false;
			var zoomOut = false;
			var zoomReset = false;

			sut.ZoomInRequested += () => zoomIn = true;
			sut.ZoomOutRequested += () => zoomOut = true;
			sut.ZoomResetRequested += () => zoomReset = true;

			var handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.D0, default(int), CefEventFlags.ControlDown, false);

			Assert.IsFalse(handled);
			Assert.IsFalse(zoomIn);
			Assert.IsFalse(zoomOut);
			Assert.IsTrue(zoomReset);

			zoomReset = false;
			handled = sut.OnKeyEvent(default(IWebBrowser), default(IBrowser), KeyType.KeyUp, (int) Keys.NumPad0, default(int), CefEventFlags.ControlDown, false);

			Assert.IsFalse(handled);
			Assert.IsFalse(zoomIn);
			Assert.IsFalse(zoomOut);
			Assert.IsTrue(zoomReset);
		}
	}
}
