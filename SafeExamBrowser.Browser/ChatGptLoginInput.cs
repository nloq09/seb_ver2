using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CefSharp;
using CefSharp.OffScreen;

namespace SafeExamBrowser.Browser
{
	/// <summary>Forwards human input to authentication pages without changing their DOM or session.</summary>
	internal sealed class ChatGptLoginInput : IDisposable
	{
		private readonly Image preview;
		private readonly ChromiumWebBrowser browser;
		private readonly Func<bool> enabled;
		private readonly Action interacted;
		private bool mouseDown;
		private MouseEvent lastPoint;

		internal ChatGptLoginInput(Image preview, ChromiumWebBrowser browser, Func<bool> enabled, Action interacted)
		{
			this.preview = preview;
			this.browser = browser;
			this.enabled = enabled;
			this.interacted = interacted;
			preview.Focusable = true;
			preview.IsHitTestVisible = true;
			preview.MouseLeftButtonDown += MouseDown;
			preview.MouseLeftButtonUp += MouseUp;
			preview.MouseMove += MouseMove;
			preview.MouseLeave += MouseLeave;
			preview.MouseWheel += MouseWheel;
			preview.PreviewTextInput += TextInput;
			preview.PreviewKeyDown += KeyDown;
			preview.PreviewKeyUp += KeyUp;
			preview.LostKeyboardFocus += LostFocus;
		}

		private bool CanInput => enabled() && browser.IsBrowserInitialized && ChatGptLogin.IsInteractiveAddress(browser.Address);

		internal static bool TryMapPoint(double x, double y, double width, double height, int browserWidth, int browserHeight, out int mappedX, out int mappedY)
		{
			mappedX = mappedY = 0;
			if (width <= 0 || height <= 0 || browserWidth <= 0 || browserHeight <= 0) return false;
			var scale = Math.Min(width / browserWidth, height / browserHeight);
			var left = (width - browserWidth * scale) / 2;
			var top = (height - browserHeight * scale) / 2;
			if (x < left || y < top || x >= left + browserWidth * scale || y >= top + browserHeight * scale) return false;
			mappedX = (int) ((x - left) / scale);
			mappedY = (int) ((y - top) / scale);
			return true;
		}

		private bool TryGetPoint(MouseEventArgs e, out MouseEvent point)
		{
			var position = e.GetPosition(preview);
			point = new MouseEvent();
			if (!TryMapPoint(position.X, position.Y, preview.ActualWidth, preview.ActualHeight, browser.Size.Width, browser.Size.Height, out var x, out var y)) return false;
			point = new MouseEvent(x, y, Modifiers());
			lastPoint = point;
			return true;
		}

		private static CefEventFlags Modifiers()
		{
			var flags = CefEventFlags.None;
			if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) flags |= CefEventFlags.ShiftDown;
			if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) flags |= CefEventFlags.ControlDown;
			if (Mouse.LeftButton == MouseButtonState.Pressed) flags |= CefEventFlags.LeftMouseButton;
			return flags;
		}

		private void MouseDown(object sender, MouseButtonEventArgs e)
		{
			if (!CanInput || !TryGetPoint(e, out var point)) return;
			interacted();
			preview.Focus();
			browser.GetBrowserHost().SendFocusEvent(true);
			browser.GetBrowserHost().SendMouseClickEvent(point, MouseButtonType.Left, false, Math.Min(2, e.ClickCount));
			mouseDown = true;
			preview.CaptureMouse();
			e.Handled = true;
		}

		private void MouseUp(object sender, MouseButtonEventArgs e)
		{
			if (!mouseDown) return;
			mouseDown = false;
			preview.ReleaseMouseCapture();
			if (CanInput) browser.GetBrowserHost().SendMouseClickEvent(TryGetPoint(e, out var point) ? point : lastPoint, MouseButtonType.Left, true, Math.Min(2, e.ClickCount));
			e.Handled = true;
		}

		private void MouseMove(object sender, MouseEventArgs e)
		{
			if (CanInput && TryGetPoint(e, out var point)) browser.GetBrowserHost().SendMouseMoveEvent(point, false);
		}

		private void MouseLeave(object sender, MouseEventArgs e)
		{
			if (CanInput) browser.GetBrowserHost().SendMouseMoveEvent(lastPoint, true);
		}

		private void MouseWheel(object sender, MouseWheelEventArgs e)
		{
			if (!CanInput || !TryGetPoint(e, out var point)) return;
			browser.GetBrowserHost().SendMouseWheelEvent(point, 0, e.Delta);
			e.Handled = true;
		}

		private void TextInput(object sender, TextCompositionEventArgs e)
		{
			if (!CanInput || string.IsNullOrEmpty(e.Text) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
			interacted();
			var text = new string(Array.FindAll(e.Text.ToCharArray(), character => !char.IsControl(character)));
			if (text.Length > 0) browser.GetBrowserHost().ImeCommitText(text, null, 0);
			e.Handled = true;
		}

		private static bool IsNavigationKey(Key key)
		{
			return key == Key.Back || key == Key.Delete || key == Key.Tab || key == Key.Enter || key == Key.Escape
				|| key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down || key == Key.Home || key == Key.End
				|| key == Key.PageUp || key == Key.PageDown;
		}

		private void KeyDown(object sender, KeyEventArgs e)
		{
			if (!CanInput || !IsNavigationKey(e.Key) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
			interacted();
			browser.GetBrowserHost().SendKeyEvent(new CefSharp.KeyEvent { Type = KeyEventType.RawKeyDown, WindowsKeyCode = KeyInterop.VirtualKeyFromKey(e.Key), Modifiers = Modifiers() });
			if (e.Key == Key.Enter) browser.GetBrowserHost().SendKeyEvent(new CefSharp.KeyEvent { Type = KeyEventType.Char, WindowsKeyCode = 13, Modifiers = Modifiers() });
			e.Handled = true;
		}

		private void KeyUp(object sender, KeyEventArgs e)
		{
			if (!CanInput || !IsNavigationKey(e.Key) || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
			browser.GetBrowserHost().SendKeyEvent(new CefSharp.KeyEvent { Type = KeyEventType.KeyUp, WindowsKeyCode = KeyInterop.VirtualKeyFromKey(e.Key), Modifiers = Modifiers() });
			e.Handled = true;
		}

		private void LostFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			if (browser.IsBrowserInitialized) browser.GetBrowserHost().SendFocusEvent(false);
		}

		public void Dispose()
		{
			preview.MouseLeftButtonDown -= MouseDown;
			preview.MouseLeftButtonUp -= MouseUp;
			preview.MouseMove -= MouseMove;
			preview.MouseLeave -= MouseLeave;
			preview.MouseWheel -= MouseWheel;
			preview.PreviewTextInput -= TextInput;
			preview.PreviewKeyDown -= KeyDown;
			preview.PreviewKeyUp -= KeyUp;
			preview.LostKeyboardFocus -= LostFocus;
			if (preview.IsMouseCaptured) preview.ReleaseMouseCapture();
		}
	}
}
