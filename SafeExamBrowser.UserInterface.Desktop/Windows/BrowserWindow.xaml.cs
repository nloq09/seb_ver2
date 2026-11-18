/*
 * Copyright (c) 2025 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SafeExamBrowser.Browser.Contracts.Events;
using SafeExamBrowser.Core.Contracts.Resources.Icons;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Browser;
using SafeExamBrowser.UserInterface.Contracts;
using SafeExamBrowser.UserInterface.Contracts.Browser;
using SafeExamBrowser.UserInterface.Contracts.Browser.Data;
using SafeExamBrowser.UserInterface.Contracts.Browser.Events;
using SafeExamBrowser.UserInterface.Contracts.Windows;
using SafeExamBrowser.UserInterface.Contracts.Windows.Events;
using SafeExamBrowser.UserInterface.Desktop.Controls.Browser;
using SafeExamBrowser.UserInterface.Shared.Utilities;

namespace SafeExamBrowser.UserInterface.Desktop.Windows
{
	internal partial class BrowserWindow : Window, IBrowserWindow
	{
		private const string CLEAR_FIND_TERM = "thisisahacktoclearthesearchresultsasitappearsthatthereisnosuchfunctionalityincef";

		private const int WM_HOTKEY = 0x0312;
		private const uint CHAT_MODIFIERS = 0x4006; // MOD_NOREPEAT | MOD_CONTROL | MOD_SHIFT
		private const int CHAT_OPEN = 1;
		private const int CHAT_QUERY = 2;
		private const int CHAT_HIDE = 3;
		private const int CHAT_ESSAY = 4;
		private const int CHAT_LOGIN = 5;

		[DllImport("user32.dll")]
		private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

		[DllImport("user32.dll")]
		private static extern bool UnregisterHotKey(IntPtr window, int id);

		private HwndSource chatHotKeySource;
		private bool chatHotKeysRegistered;

		private readonly bool isMainWindow;
		private readonly BrowserSettings settings;
		private readonly IText text;
		private readonly ILogger logger;
		private readonly IBrowserControl browserControl;

		private WindowClosedEventHandler closed;
		private WindowClosingEventHandler closing;
		private bool browserControlGetsFocusFromTaskbar;
		private IInputElement tabKeyDownFocusElement;
		private IChatGptAssistant chatGptAssistant;
		private bool chatGptClosed;
		private bool readingSelection;
		private double chatGptWidth = 340;
		private bool questionRunning;
		private CancellationTokenSource questionCancellation;
		private readonly System.Windows.Threading.DispatcherTimer chatGptFeedbackTimer =
			new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

		private WindowSettings WindowSettings
		{
			get { return isMainWindow ? settings.MainWindow : settings.AdditionalWindow; }
		}

		public bool CanNavigateBackwards { set => Dispatcher.Invoke(() => BackwardButton.IsEnabled = value); }
		public bool CanNavigateForwards { set => Dispatcher.Invoke(() => ForwardButton.IsEnabled = value); }
		public IntPtr Handle { get; private set; }

		public event AddressChangedEventHandler AddressChanged;
		public event ActionRequestedEventHandler BackwardNavigationRequested;
		public event ActionRequestedEventHandler DeveloperConsoleRequested;
		public event FindRequestedEventHandler FindRequested;
		public event ActionRequestedEventHandler ForwardNavigationRequested;
		public event ActionRequestedEventHandler HomeNavigationRequested;
		public event ActionRequestedEventHandler ReloadRequested;
		public event ActionRequestedEventHandler ZoomInRequested;
		public event ActionRequestedEventHandler ZoomOutRequested;
		public event ActionRequestedEventHandler ZoomResetRequested;
		public event LoseFocusRequestedEventHandler LoseFocusRequested;

		event WindowClosedEventHandler IWindow.Closed
		{
			add { closed += value; }
			remove { closed -= value; }
		}

		event WindowClosingEventHandler IWindow.Closing
		{
			add { closing += value; }
			remove { closing -= value; }
		}

		internal BrowserWindow(IBrowserControl browserControl, BrowserSettings settings, bool isMainWindow, IText text, ILogger logger)
		{
			this.browserControl = browserControl;
			this.isMainWindow = isMainWindow;
			this.logger = logger;
			this.settings = settings;
			this.text = text;

			InitializeComponent();
			InitializeBrowserWindow(browserControl);
		}

		public void BringToForeground()
		{
			Dispatcher.Invoke(() =>
			{
				if (WindowState == WindowState.Minimized)
				{
					WindowState = WindowState.Normal;
				}

				Activate();
			});
		}

		public new void Close()
		{
			Dispatcher.Invoke(() =>
			{
				Closing -= BrowserWindow_Closing;
				closing?.Invoke();
				base.Close();
			});
		}

		public void FocusToolbar(bool forward)
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				Activate();
				await Task.Delay(50);

				// focus all elements in the toolbar, such that the last element that is enabled gets focus
				var buttons = new System.Windows.Controls.Control[] { ForwardButton, BackwardButton, ReloadButton, UrlTextBox, MenuButton, };
				for (var i = forward ? 0 : buttons.Length - 1; i >= 0 && i < buttons.Length; i += forward ? 1 : -1)
				{
					if (buttons[i].IsEnabled && buttons[i].Visibility == Visibility.Visible)
					{
						buttons[i].Focus();
						break;
					}
				}
			}));
		}

		public void FocusBrowser()
		{
			Dispatcher.BeginInvoke((Action) (async () =>
			{
				FocusToolbar(false);
				await Task.Delay(100);

				browserControlGetsFocusFromTaskbar = true;

				var focusedElement = FocusManager.GetFocusedElement(this) as UIElement;
				focusedElement.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));

				await Task.Delay(150);
				browserControlGetsFocusFromTaskbar = false;
			}));
		}

		public void FocusAddressBar()
		{
			Dispatcher.BeginInvoke((Action) (() =>
			{
				UrlTextBox.Focus();
			}));
		}

		public new void Hide()
		{
			Dispatcher.Invoke(base.Hide);
		}

		public new void Show()
		{
			Dispatcher.Invoke(base.Show);
		}

		public void ShowFindbar()
		{
			Dispatcher.InvokeAsync(() =>
			{
				Findbar.Visibility = Visibility.Visible;
				FindTextBox.Focus();
			});
		}

		public void UpdateAddress(string url)
		{
			Dispatcher.Invoke(() => UrlTextBox.Text = url);
		}

		public void UpdateIcon(IconResource icon)
		{
			Dispatcher.InvokeAsync(() =>
			{
				if (icon is BitmapIconResource bitmap)
				{
					Icon = new BitmapImage(bitmap.Uri);
				}
			});
		}

		public void UpdateDownloadState(DownloadItemState state)
		{
			Dispatcher.InvokeAsync(() =>
			{
				var control = Downloads.Children.OfType<DownloadItemControl>().FirstOrDefault(c => c.Id == state.Id);

				if (control == default)
				{
					control = new DownloadItemControl(state.Id, text);
					Downloads.Children.Add(control);
				}

				control.Update(state);
				DownloadsButton.Visibility = Visibility.Visible;
				DownloadsPopup.IsOpen = IsActive;
			});
		}

		public void UpdateLoadingState(bool isLoading)
		{
			Dispatcher.Invoke(() => ProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Hidden);
		}

		public void UpdateProgress(double value)
		{
			Dispatcher.Invoke(() => ProgressBar.Value = value * 100);
		}

		public void UpdateTitle(string title)
		{
			Dispatcher.Invoke(() => Title = title);
		}

		public void UpdateZoomLevel(double value)
		{
			Dispatcher.Invoke(() =>
			{
				ZoomLevel.Text = $"{value}%";
				var zoomButtonName = text.Get(TextKey.BrowserWindow_ZoomLevelReset).Replace("%%ZOOM%%", value.ToString("0"));
				ZoomResetButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, zoomButtonName);
			});
		}

		private void BrowserWindow_Closing(object sender, CancelEventArgs e)
		{
			if (isMainWindow)
			{
				e.Cancel = true;
			}
			else
			{
				closing?.Invoke();
			}
		}

		private void BrowserWindow_KeyDown(object sender, KeyEventArgs e)
		{
			if (!chatHotKeysRegistered && (e.Key == Key.J || e.Key == Key.K || e.Key == Key.Y || e.Key == Key.H || e.Key == Key.U) && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)
				&& browserControl is IChatGptHost host && host.CanUseChatGpt)
			{
				e.Handled = true;
				if (!e.IsRepeat)
				{
					if (e.Key == Key.J) OpenChatGpt();
					else if (e.Key == Key.U) BeginChatGptLogin();
					else if (e.Key == Key.K) AskChatGpt(ChatGptQuestionKind.MultipleChoice);
					else if (e.Key == Key.Y) AskChatGpt(ChatGptQuestionKind.Essay);
					else HideChoiceAnswer();
				}
				return;
			}
			if (e.Key == Key.Tab)
			{
				var hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

				if (Toolbar.IsKeyboardFocusWithin && hasShift)
				{
					var firstActiveElementInToolbar = Toolbar.PredictFocus(FocusNavigationDirection.Right);

					if (firstActiveElementInToolbar is UIElement)
					{
						var control = firstActiveElementInToolbar as UIElement;

						if (control.IsKeyboardFocusWithin)
						{
							LoseFocusRequested?.Invoke(false);
							e.Handled = true;
						}
					}
				}

				tabKeyDownFocusElement = FocusManager.GetFocusedElement(this);
			}
			else
			{
				tabKeyDownFocusElement = null;
			}
		}

		private void BrowserWindow_KeyUp(object sender, KeyEventArgs e)
		{
			// Typing in the assistant must not trigger the exam browser's Home / F5 / find shortcuts.
			if (ChatGptPanel.IsKeyboardFocusWithin) return;

			if (e.Key == Key.F5)
			{
				ReloadRequested?.Invoke();
			}

			if (e.Key == Key.Home)
			{
				HomeNavigationRequested?.Invoke();
			}

			if (settings.AllowFind && (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) && e.Key == Key.F)
			{
				ShowFindbar();
			}

			if (e.Key == Key.Tab)
			{
				var hasCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
				var hasShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

				if (BrowserControlHost.IsFocused && hasCtrl)
				{
					if (Findbar.Visibility == Visibility.Hidden || hasShift)
					{
						Toolbar.Focus();
					}
					else if (Toolbar.Visibility == Visibility.Hidden)
					{
						Findbar.Focus();
					}
				}
				else if (MenuPopup.IsKeyboardFocusWithin)
				{
					var focusedElement = FocusManager.GetFocusedElement(this);

					if (focusedElement is Control focusedControl && tabKeyDownFocusElement is Control prevFocusedControl)
					{
						if (!hasShift && focusedControl.TabIndex < prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							FocusBrowser();
						}
						else if (hasShift && focusedControl.TabIndex > prevFocusedControl.TabIndex)
						{
							MenuPopup.IsOpen = false;
							MenuButton.Focus();
						}
					}
				}
			}

			if (e.Key == Key.Escape && MenuPopup.IsOpen)
			{
				MenuPopup.IsOpen = false;
				MenuButton.Focus();
			}
		}

		private void BrowserWindow_Loaded(object sender, RoutedEventArgs e)
		{
			Handle = new WindowInteropHelper(this).Handle;

			if (isMainWindow)
			{
				this.DisableCloseButton();
			}
		}

		private void FindbarCloseButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(CLEAR_FIND_TERM, true, false);
			Findbar.Visibility = Visibility.Collapsed;
		}

		private void FindNextButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true);
		}

		private void FindPreviousButton_Click(object sender, RoutedEventArgs e)
		{
			FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true, false);
		}

		private void FindTextBox_KeyUp(object sender, KeyEventArgs e)
		{
			if (string.IsNullOrEmpty(FindTextBox.Text))
			{
				FindRequested?.Invoke(CLEAR_FIND_TERM, true, false);
			}
			else if (e.Key == Key.Enter)
			{
				FindRequested?.Invoke(FindTextBox.Text, false, FindCaseSensitiveCheckBox.IsChecked == true);
			}
			else
			{
				FindRequested?.Invoke(FindTextBox.Text, true, FindCaseSensitiveCheckBox.IsChecked == true);
			}
		}

		private CustomPopupPlacement[] Popup_PlacementCallback(Size popupSize, Size targetSize, Point offset)
		{
			return new[]
			{
				new CustomPopupPlacement(new Point(targetSize.Width - Toolbar.Margin.Right - popupSize.Width, -2), PopupPrimaryAxis.None)
			};
		}

		private void SystemParameters_StaticPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(SystemParameters.WorkArea))
			{
				Dispatcher.InvokeAsync(InitializeBounds);
			}
		}

		private void UrlTextBox_GotMouseCapture(object sender, MouseEventArgs e)
		{
			if (UrlTextBox.Tag as bool? != true)
			{
				UrlTextBox.SelectAll();
				UrlTextBox.Tag = true;
			}
		}

		private void UrlTextBox_KeyUp(object sender, KeyEventArgs e)
		{
			if (e.Key == Key.Enter)
			{
				AddressChanged?.Invoke(UrlTextBox.Text);
			}
		}

		private void InitializeBrowserWindow(IBrowserControl browserControl)
		{
			if (browserControl.EmbeddableControl is System.Windows.Forms.Control control)
			{
				BrowserControlHost.Child = control;
			}

			RegisterEvents();
			InitializeBounds();
			ApplySettings();
			LoadIcons();
			LoadText();
		}

		private void RegisterChatHotKeys()
		{
			chatHotKeySource = PresentationSource.FromVisual(this) as HwndSource;
			if (chatHotKeySource == null || chatHotKeysRegistered) return;

			chatHotKeySource.AddHook(ChatHotKeyMessage);
			var handle = chatHotKeySource.Handle;
			chatHotKeysRegistered = RegisterHotKey(handle, CHAT_OPEN, CHAT_MODIFIERS, 'J')
				&& RegisterHotKey(handle, CHAT_QUERY, CHAT_MODIFIERS, 'K')
				&& RegisterHotKey(handle, CHAT_ESSAY, CHAT_MODIFIERS, 'Y')
				&& RegisterHotKey(handle, CHAT_LOGIN, CHAT_MODIFIERS, 'U')
				&& RegisterHotKey(handle, CHAT_HIDE, CHAT_MODIFIERS, 'H');
			if (!chatHotKeysRegistered)
			{
				UnregisterChatHotKeys();
				logger.Warn("Could not register ChatGPT hotkeys with Windows. Using in-window shortcuts.");
			}
		}

		private void UnregisterChatHotKeys()
		{
			chatHotKeysRegistered = false;
			if (chatHotKeySource == null) return;

			var handle = chatHotKeySource.Handle;
			UnregisterHotKey(handle, CHAT_OPEN);
			UnregisterHotKey(handle, CHAT_QUERY);
			UnregisterHotKey(handle, CHAT_ESSAY);
			UnregisterHotKey(handle, CHAT_LOGIN);
			UnregisterHotKey(handle, CHAT_HIDE);
			chatHotKeySource.RemoveHook(ChatHotKeyMessage);
			chatHotKeySource = null;
		}

		private IntPtr ChatHotKeyMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
		{
			if (message != WM_HOTKEY || !chatHotKeysRegistered || !IsActive) return IntPtr.Zero;

			switch (wParam.ToInt32())
			{
				case CHAT_OPEN: ChatGptShortcutRequested(); break;
				case CHAT_QUERY: ChatGptQueryShortcutRequested(); break;
				case CHAT_ESSAY: ChatGptEssayShortcutRequested(); break;
				case CHAT_LOGIN: ChatGptLoginShortcutRequested(); break;
				case CHAT_HIDE: ChatGptHideAnswerShortcutRequested(); break;
				default: return IntPtr.Zero;
			}
			handled = true;
			return IntPtr.Zero;
		}

		private void RegisterEvents()
		{
			chatGptFeedbackTimer.Tick += (o, args) =>
			{
				chatGptFeedbackTimer.Stop();
				ChatGptFeedbackBadge.Visibility = Visibility.Collapsed;
			};
			ChatGptAnswerBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
				(o, args) => { args.Handled = true; CopyChatGptAnswer_Click(o, new RoutedEventArgs()); },
				(o, args) => { args.CanExecute = CopyChatGptAnswerButton.IsEnabled; args.Handled = true; }));
			if (browserControl is IChatGptHost host && host.CanUseChatGpt)
			{
				Loaded += (o, args) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
					new Action(() => { InitializeChatGpt(); }));
				Activated += (o, args) => RegisterChatHotKeys();
				Deactivated += (o, args) => UnregisterChatHotKeys();
				host.ChatGptRequested += ChatGptShortcutRequested;
				host.ChatGptLoginRequested += ChatGptLoginShortcutRequested;
				host.ChatGptQueryRequested += ChatGptQueryShortcutRequested;
				host.ChatGptEssayRequested += ChatGptEssayShortcutRequested;
				host.ChatGptHideAnswerRequested += ChatGptHideAnswerShortcutRequested;
			}
			Closed += (o, args) =>
			{
				chatGptClosed = true;
				chatGptFeedbackTimer.Stop();
				UnregisterChatHotKeys();
				questionCancellation?.Cancel();
				if (browserControl is IChatGptHost closingHost)
				{
					closingHost.ChatGptRequested -= ChatGptShortcutRequested;
					closingHost.ChatGptLoginRequested -= ChatGptLoginShortcutRequested;
					closingHost.ChatGptQueryRequested -= ChatGptQueryShortcutRequested;
					closingHost.ChatGptEssayRequested -= ChatGptEssayShortcutRequested;
					closingHost.ChatGptHideAnswerRequested -= ChatGptHideAnswerShortcutRequested;
				}
				if (chatGptAssistant != null)
				{
					chatGptAssistant.StatusChanged -= ChatGpt_StatusChanged;
					chatGptAssistant.QuestionSent -= ChatGpt_QuestionSent;
					chatGptAssistant.Dispose();
				}
				ChatGptQuestion.Clear();
			};
			BackwardButton.Click += (o, args) => BackwardNavigationRequested?.Invoke();
			Closed += (o, args) => closed?.Invoke();
			Closing += BrowserWindow_Closing;
			DeveloperConsoleButton.Click += (o, args) => DeveloperConsoleRequested?.Invoke();
			DownloadsButton.Click += (o, args) => DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
			DownloadsButton.MouseLeave += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => DownloadsPopup.IsOpen = DownloadsPopup.IsMouseOver));
			DownloadsPopup.CustomPopupPlacementCallback = new CustomPopupPlacementCallback(Popup_PlacementCallback);
			DownloadsPopup.MouseLeave += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => DownloadsPopup.IsOpen = DownloadsPopup.IsMouseOver));
			FindbarCloseButton.Click += FindbarCloseButton_Click;
			FindNextButton.Click += FindNextButton_Click;
			FindPreviousButton.Click += FindPreviousButton_Click;
			FindMenuButton.Click += (o, args) => ShowFindbar();
			FindTextBox.KeyUp += FindTextBox_KeyUp;
			ForwardButton.Click += (o, args) => ForwardNavigationRequested?.Invoke();
			HomeButton.Click += (o, args) => HomeNavigationRequested?.Invoke();
			Loaded += BrowserWindow_Loaded;
			MenuButton.Click += MenuButton_Click;
			MenuPopup.CustomPopupPlacementCallback = new CustomPopupPlacementCallback(Popup_PlacementCallback);
			MenuPopup.LostFocus += (o, args) => Task.Delay(250).ContinueWith(_ => Dispatcher.Invoke(() => MenuPopup.IsOpen = MenuPopup.IsKeyboardFocusWithin));
			KeyDown += BrowserWindow_KeyDown;
			KeyUp += BrowserWindow_KeyUp;
			LocationChanged += (o, args) => { DownloadsPopup.IsOpen = false; MenuPopup.IsOpen = false; };
			ReloadButton.Click += (o, args) => ReloadRequested?.Invoke();
			SizeChanged += (o, args) => { DownloadsPopup.IsOpen = false; MenuPopup.IsOpen = false; };
			SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
			UrlTextBox.GotKeyboardFocus += (o, args) => UrlTextBox.SelectAll();
			UrlTextBox.GotMouseCapture += UrlTextBox_GotMouseCapture;
			UrlTextBox.LostKeyboardFocus += (o, args) => UrlTextBox.Tag = null;
			UrlTextBox.LostFocus += (o, args) => UrlTextBox.Tag = null;
			UrlTextBox.KeyUp += UrlTextBox_KeyUp;
			UrlTextBox.MouseDoubleClick += (o, args) => UrlTextBox.SelectAll();
			ZoomInButton.Click += (o, args) => ZoomInRequested?.Invoke();
			ZoomOutButton.Click += (o, args) => ZoomOutRequested?.Invoke();
			ZoomResetButton.Click += (o, args) => ZoomResetRequested?.Invoke();
			BrowserControlHost.GotKeyboardFocus += BrowserControlHost_GotKeyboardFocus;
		}

		private void ChatGptShortcutRequested()
		{
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() => { if (!chatGptClosed) OpenChatGpt(); }));
		}

		private void ChatGptLoginShortcutRequested()
		{
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() => { if (!chatGptClosed) BeginChatGptLogin(); }));
		}

		private void BeginChatGptLogin()
		{
			if (chatGptClosed) return;
			if (questionRunning)
			{
				ChatGptStatus.Text = "Đang xử lý câu trước. Nhấn Ctrl + Shift + U sau khi có kết quả để đăng nhập.";
				return;
			}
			try
			{
				if (OpenChatGptPanel()) chatGptAssistant.BeginLogin();
			}
			catch (Exception) { ChatGptStatus.Text = "Không thể bắt đầu đăng nhập. Nhấn Ctrl + Shift + U để thử lại."; }
		}

		private void ChatGptQueryShortcutRequested()
		{
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() => { if (!chatGptClosed) AskChatGpt(ChatGptQuestionKind.MultipleChoice); }));
		}

		private void ChatGptEssayShortcutRequested()
		{
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() => { if (!chatGptClosed) AskChatGpt(ChatGptQuestionKind.Essay); }));
		}

		private void ChatGptHideAnswerShortcutRequested()
		{
			if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke((Action) HideChoiceAnswer);
		}

		private void HideChoiceAnswer()
		{
			if (chatGptClosed) return;
			ChoiceAnswerBadge.Visibility = Visibility.Collapsed;
			ChoiceAnswerText.Text = string.Empty;
		}

		private bool InitializeChatGpt()
		{
			if (chatGptClosed || !(browserControl is IChatGptHost host) || !host.CanUseChatGpt) return false;
			try
			{
				if (chatGptAssistant == null)
				{
					chatGptAssistant = host.CreateChatGptAssistant();
					chatGptAssistant.StatusChanged += ChatGpt_StatusChanged;
					chatGptAssistant.QuestionSent += ChatGpt_QuestionSent;
					ChatGptControlHost.Content = chatGptAssistant.EmbeddableControl;
					ChatGptStatus.Text = "ChatGPT đang tải. Ctrl + Shift + U để đăng nhập; K cho trắc nghiệm, Y cho tự luận (giữ Ctrl + Shift).";
				}
				return true;
			}
			catch (Exception)
			{
				logger.Warn("Could not initialize ChatGPT. A shortcut can retry initialization.");
				ChatGptStatus.Text = "Không thể khởi tạo ChatGPT. Nhấn Ctrl + Shift + J, K hoặc Y để thử lại.";
				return false;
			}
		}

		private bool OpenChatGptPanel()
		{
			if (!InitializeChatGpt()) return false;
			ExpandChatGpt();
			return true;
		}

		private void OpenChatGpt()
		{
			try
			{
				if (OpenChatGptPanel() && !questionRunning)
					ChatGptStatus.Text = "Ctrl + Shift + U để đăng nhập. Bôi đen đề, nhấn Ctrl + Shift + K cho trắc nghiệm hoặc Ctrl + Shift + Y cho tự luận.";
			}
			catch (Exception) { ChatGptStatus.Text = "Không thể mở khung ChatGPT. Hãy thử lại."; }
		}

		private async void AskChatGpt(ChatGptQuestionKind kind)
		{
			if (chatGptClosed) return;
			if (!InitializeChatGpt() || !(browserControl is IChatGptHost host))
			{
				ShowChatGptFeedback("ChatGPT chưa sẵn sàng", true);
				return;
			}
			if (questionRunning || readingSelection)
			{
				ChatGptStatus.Text = questionRunning
					? "Đang xử lý câu trước. Nhấn lại tổ hợp phím sau khi có kết quả."
					: "Đang đọc vùng bôi đen...";
				return;
			}
			readingSelection = true;
			ShowChatGptFeedback("Đang đọc vùng chọn…");
			try
			{
				var selection = await host.GetSelectedTextAsync();
				if (chatGptClosed) return;
				if (string.IsNullOrWhiteSpace(selection) || selection.Length > 10000)
				{
					ChatGptStatus.Text = "Hãy bôi đen câu hỏi, tối đa 10.000 ký tự. Nhấn Ctrl + Shift + K cho trắc nghiệm hoặc Ctrl + Shift + Y cho tự luận.";
					ShowChatGptFeedback("Kiểm tra vùng chọn", true);
					return;
				}
				ChatGptQuestion.Text = selection.Trim();
				ChatGptQuestionType.SelectedIndex = (int) kind;
				readingSelection = false;
				await RunQuestionAsync(kind);
			}
			catch (OperationCanceledException) { ShowChatGptFeedback("Đã hủy", true); }
			catch (Exception)
			{
				if (!chatGptClosed) ChatGptStatus.Text = "Không thể đọc vùng chọn hoặc gửi câu hỏi. Hãy thử lại.";
				ShowChatGptFeedback("Không gửi được", true);
				logger.Warn("Could not start the selected-question request.");
			}
			finally { readingSelection = false; }
		}

		private async void PrepareChatGpt_Click(object sender, RoutedEventArgs e)
		{
			try { await RunQuestionAsync(); }
			catch (Exception)
			{
				if (!chatGptClosed) ChatGptStatus.Text = "Không thể bắt đầu câu hỏi. Hãy thử lại.";
			}
		}

		private async Task RunQuestionAsync(ChatGptQuestionKind? questionKind = null)
		{
			if (chatGptClosed || questionRunning || chatGptAssistant == null) return;
			var question = ChatGptQuestion.Text.Trim();
			if (question.Length == 0 || question.Length > 10000)
			{
				ChatGptStatus.Text = "Hãy chọn hoặc nhập câu hỏi không quá 10.000 ký tự.";
				ShowChatGptFeedback("Kiểm tra câu hỏi", true);
				return;
			}
			questionRunning = true;
			ShowChatGptFeedback("Đang gửi…");
			var cancellation = new CancellationTokenSource();
			questionCancellation = cancellation;
			PrepareChatGptButton.IsEnabled = false;
			CopyChatGptAnswerButton.IsEnabled = false;
			HideChoiceAnswer();
			ChatGptAnswerBox.Clear();
			ChatGptResult.IsExpanded = false;
			try
			{
				var kind = questionKind ?? (ChatGptQuestionKind) ChatGptQuestionType.SelectedIndex;
				var answer = await chatGptAssistant.AskQuestionAsync(question, kind, cancellation.Token);
				if (chatGptClosed || cancellation.IsCancellationRequested) return;
				ChatGptResult.IsExpanded = true;
				if (!answer.IsValid)
				{
					ChatGptAnswerBox.Text = answer.Error;
					ChatGptStatus.Text = answer.Error;
					ShowChatGptFeedback("Chưa hoàn tất · xem lỗi", true);
					return;
				}
				ShowChatGptFeedback("Đã có đáp án", true);
				if (answer.Kind == ChatGptQuestionKind.MultipleChoice &&
					System.Text.RegularExpressions.Regex.IsMatch(answer.Text, @"\A[A-D](?:, [A-D])*\z"))
				{
					ChatGptResult.IsExpanded = false;
					ChoiceAnswerText.Text = answer.Text;
					ChoiceAnswerBadge.Visibility = Visibility.Visible;
					ChatGptStatus.Text = "Ctrl + Shift + H để ẩn đáp án ở góc dưới.";
					return;
				}
				ChatGptAnswerBox.Text = answer.Text;
				ChatGptAnswerBox.Height = answer.Kind == ChatGptQuestionKind.MultipleChoice ? 36 : 80;
				CopyChatGptAnswerButton.IsEnabled = true;
				if (answer.Kind == ChatGptQuestionKind.Essay && browserControl is IChatGptHost host)
				{
					var copied = await host.CopyAnswerAsync(answer.Text);
					if (!chatGptClosed) ChatGptStatus.Text = copied
						? "Đã chuẩn hóa và sao chép câu tự luận. Dùng Ctrl + V tại nơi được phép dán trong SEB."
						: "Đã có câu trả lời chuẩn hóa. Chưa sao chép được; hãy kiểm tra chính sách clipboard của phiên.";
				}
				else ChatGptStatus.Text = "Đáp án: " + answer.Text;
			}
			catch (OperationCanceledException)
			{
				if (!chatGptClosed) ChatGptStatus.Text = "Đã hủy chờ câu trả lời.";
				ShowChatGptFeedback("Đã hủy", true);
			}
			catch (Exception)
			{
				if (!chatGptClosed) ChatGptStatus.Text = "Không thể hoàn tất câu hỏi. Kiểm tra khung ChatGPT rồi thử lại.";
				ShowChatGptFeedback("Chưa hoàn tất · xem lỗi", true);
				logger.Warn("Could not complete or normalize the ChatGPT request.");
			}
			finally
			{
				questionRunning = false;
				questionCancellation = null;
				cancellation.Dispose();
				if (!chatGptClosed) PrepareChatGptButton.IsEnabled = true;
			}
		}

		private async void CopyChatGptAnswer_Click(object sender, RoutedEventArgs e)
		{
			if (!(browserControl is IChatGptHost host) || !CopyChatGptAnswerButton.IsEnabled) return;
			try
			{
				var copied = await host.CopyAnswerAsync(ChatGptAnswerBox.Text);
				if (!chatGptClosed) ChatGptStatus.Text = copied ? "Đã sao chép câu trả lời chuẩn hóa." : "Không thể sao chép theo chính sách clipboard hiện tại.";
			}
			catch (Exception) { if (!chatGptClosed) ChatGptStatus.Text = "Chưa sao chép được. Hãy thử lại."; }
		}
		private void ShowChatGptFeedback(string message, bool autoHide = false)
		{
			if (chatGptClosed) return;
			chatGptFeedbackTimer.Stop();
			ChatGptFeedbackText.Text = message;
			ChatGptFeedbackBadge.Visibility = Visibility.Visible;
			if (autoHide) chatGptFeedbackTimer.Start();
		}

		private void ChatGpt_QuestionSent()
		{
			var cancellation = questionCancellation;
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() =>
				{
					if (questionRunning && questionCancellation == cancellation) ShowChatGptFeedback("Đã gửi · đang chờ…");
				}));
		}

		private void ChatGpt_StatusChanged(string status)
		{
			if (!chatGptClosed && !Dispatcher.HasShutdownStarted)
				Dispatcher.BeginInvoke((Action) (() => { if (!chatGptClosed) ChatGptStatus.Text = status; }));
		}

		private void CloseChatGpt_Click(object sender, RoutedEventArgs e)
		{
			ChatGptPanel.Visibility = ChatGptSplitter.Visibility = Visibility.Collapsed;
			RestoreChatGptButton.Visibility = Visibility.Collapsed;
			ChatGptColumn.Width = new GridLength(0);
		}

		private void ExpandChatGpt()
		{
			RestoreChatGptButton.Visibility = Visibility.Collapsed;
			ChatGptPanel.Visibility = ChatGptSplitter.Visibility = Visibility.Visible;
			ChatGptColumn.Width = new GridLength(Math.Min(chatGptWidth, Math.Max(160, ActualWidth * 0.45)));
		}

		private void CollapseChatGpt_Click(object sender, RoutedEventArgs e)
		{
			chatGptWidth = Math.Max(160, ChatGptColumn.ActualWidth);
			ChatGptPanel.Visibility = ChatGptSplitter.Visibility = Visibility.Collapsed;
			ChatGptColumn.Width = new GridLength(32);
			RestoreChatGptButton.Visibility = Visibility.Visible;
		}

		private void RestoreChatGpt_Click(object sender, RoutedEventArgs e)
		{
			ExpandChatGpt();
		}

		private void ReloadChatGpt_Click(object sender, RoutedEventArgs e)
		{
			questionCancellation?.Cancel();
			chatGptAssistant?.Reload();
		}

		private void MenuButton_Click(object sender, RoutedEventArgs e)
		{
			MenuPopup.IsOpen = !MenuPopup.IsOpen;
			ZoomInButton.Focus();
		}

		private void BrowserControlHost_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
		{
			var forward = !browserControlGetsFocusFromTaskbar;

			// focus the first / last element on the page
			var javascript = @"
if (typeof __SEB_focusElement === 'undefined') {
  __SEB_focusElement = function (forward) {
	if (!document.body)
		return;
	var items = [].map
	  .call(document.body.querySelectorAll(['input', 'select', 'a[href]', 'textarea', 'button', '[tabindex]']), function(el, i) { return { el, i } })
	  .filter(function(e) { return e.el.tabIndex >= 0 && !e.el.disabled && e.el.offsetParent; })
	  .sort(function(a,b) { return a.el.tabIndex === b.el.tabIndex ? a.i - b.i : (a.el.tabIndex || 9E9) - (b.el.tabIndex || 9E9); })
	var item = items[forward ? 1 : items.length - 1];
	if (item && item.focus && typeof item.focus !== 'function')
		throw ('item.focus is not a function, ' + typeof item.focus)
	setTimeout(function () { item && item.focus && item.focus(); }, 20);
  }
}";
			browserControl.ExecuteJavaScript(javascript, result =>
			{
				if (!result.Success)
				{
					logger.Warn($"Failed to initialize JavaScript: {result.Message}");
				}
			});

			browserControl.ExecuteJavaScript("__SEB_focusElement(" + forward.ToString().ToLower() + ")", result =>
			{
				if (!result.Success)
				{
					logger.Warn($"Failed to execute JavaScript: {result.Message}");
				}
			});
		}

		private void ApplySettings()
		{
			BackwardButton.IsEnabled = WindowSettings.AllowBackwardNavigation;
			BackwardButton.Visibility = WindowSettings.AllowBackwardNavigation ? Visibility.Visible : Visibility.Collapsed;
			DeveloperConsoleMenuItem.Visibility = WindowSettings.AllowDeveloperConsole ? Visibility.Visible : Visibility.Collapsed;
			FindMenuItem.Visibility = settings.AllowFind ? Visibility.Visible : Visibility.Collapsed;
			ForwardButton.IsEnabled = WindowSettings.AllowForwardNavigation;
			ForwardButton.Visibility = WindowSettings.AllowForwardNavigation ? Visibility.Visible : Visibility.Collapsed;
			HomeButton.IsEnabled = WindowSettings.ShowHomeButton;
			HomeButton.Visibility = WindowSettings.ShowHomeButton ? Visibility.Visible : Visibility.Collapsed;
			ReloadButton.IsEnabled = WindowSettings.AllowReloading;
			ReloadButton.Visibility = WindowSettings.ShowReloadButton ? Visibility.Visible : Visibility.Collapsed;
			Toolbar.Visibility = WindowSettings.ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
			UrlTextBox.Visibility = WindowSettings.AllowAddressBar ? Visibility.Visible : Visibility.Hidden;
			ZoomMenuItem.Visibility = settings.AllowPageZoom ? Visibility.Visible : Visibility.Collapsed;
		}

		private void InitializeBounds()
		{
			if (isMainWindow && WindowSettings.FullScreenMode)
			{
				Top = 0;
				Left = 0;
				Height = SystemParameters.WorkArea.Height;
				Width = SystemParameters.WorkArea.Width;
				ResizeMode = ResizeMode.NoResize;
				WindowStyle = WindowStyle.None;
			}
			else if (WindowSettings.RelativeHeight == 100 && WindowSettings.RelativeWidth == 100)
			{
				WindowState = WindowState.Maximized;
			}
			else
			{
				if (WindowSettings.RelativeHeight > 0)
				{
					Height = SystemParameters.WorkArea.Height * WindowSettings.RelativeHeight.Value / 100;
					Top = (SystemParameters.WorkArea.Height / 2) - (Height / 2);
				}
				else if (WindowSettings.AbsoluteHeight > 0)
				{
					Height = this.TransformFromPhysical(0, WindowSettings.AbsoluteHeight.Value).Y;
					Top = (SystemParameters.WorkArea.Height / 2) - (Height / 2);
				}

				if (WindowSettings.RelativeWidth > 0)
				{
					Width = SystemParameters.WorkArea.Width * WindowSettings.RelativeWidth.Value / 100;
				}
				else if (WindowSettings.AbsoluteWidth > 0)
				{
					Width = this.TransformFromPhysical(WindowSettings.AbsoluteWidth.Value, 0).X;
				}

				if (Height > SystemParameters.WorkArea.Height)
				{
					Top = 0;
					Height = SystemParameters.WorkArea.Height;
				}

				if (Width > SystemParameters.WorkArea.Width)
				{
					Left = 0;
					Width = SystemParameters.WorkArea.Width;
				}

				switch (WindowSettings.Position)
				{
					case WindowPosition.Left:
						Left = 0;
						break;
					case WindowPosition.Center:
						Left = (SystemParameters.WorkArea.Width / 2) - (Width / 2);
						break;
					case WindowPosition.Right:
						Left = SystemParameters.WorkArea.Width - Width;
						break;
				}
			}
		}

		private void LoadIcons()
		{
			var backward = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateBack.xaml") };
			var forward = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/NavigateForward.xaml") };
			var home = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Home.xaml") };
			var menu = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Menu.xaml") };
			var reload = new XamlIconResource { Uri = new Uri("pack://application:,,,/SafeExamBrowser.UserInterface.Desktop;component/Images/Reload.xaml") };

			BackwardButton.Content = IconResourceLoader.Load(backward);
			ForwardButton.Content = IconResourceLoader.Load(forward);
			HomeButton.Content = IconResourceLoader.Load(home);
			MenuButton.Content = IconResourceLoader.Load(menu);
			ReloadButton.Content = IconResourceLoader.Load(reload);
		}

		private void LoadText()
		{
			DeveloperConsoleText.Text = text.Get(TextKey.BrowserWindow_DeveloperConsoleMenuItem);
			DeveloperConsoleButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_DeveloperConsoleMenuItem));
			FindCaseSensitiveCheckBox.Content = text.Get(TextKey.BrowserWindow_FindCaseSensitive);
			FindMenuText.Text = text.Get(TextKey.BrowserWindow_FindMenuItem);
			FindMenuButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_FindMenuItem));
			ZoomText.Text = text.Get(TextKey.BrowserWindow_ZoomMenuItem);
			ZoomInButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ZoomMenuPlus));
			ZoomOutButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ZoomMenuMinus));
			ReloadButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ReloadButton));
			BackwardButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_BackwardButton));
			ForwardButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_ForwardButton));
			DownloadsButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_DownloadsButton));
			HomeButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_HomeButton));
			MenuButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_MenuButton));
			UrlTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_UrlTextBox));
			FindTextBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchTextBox));
			FindPreviousButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchPrevious));
			FindNextButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_SearchNext));
			FindbarCloseButton.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, text.Get(TextKey.BrowserWindow_CloseButton));
		}
	}
}
