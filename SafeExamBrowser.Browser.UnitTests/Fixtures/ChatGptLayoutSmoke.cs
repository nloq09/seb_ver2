using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using Moq;
using SafeExamBrowser.I18n.Contracts;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings;
using SafeExamBrowser.UserInterface.Contracts.Browser;

class ChatGptLayoutSmoke
{
    [STAThread]
    static int Main(string[] args)
    {
        var runtime = args[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
            var file = Path.Combine(runtime, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(file) ? Assembly.LoadFrom(file) : null;
        };
        try { Run(runtime); return 0; } catch (Exception error) { Console.WriteLine(error); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string runtime)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var browser = new Mock<IBrowserControl>();
        var host = browser.As<IChatGptHost>();
        var assistant = new Mock<IChatGptAssistant>();
        assistant.SetupGet(a => a.EmbeddableControl).Returns(new Image());
        host.SetupGet(h => h.CanUseChatGpt).Returns(true);
        host.Setup(h => h.CreateChatGptAssistant()).Returns(assistant.Object);
        host.Setup(h => h.GetSelectedTextAsync()).ReturnsAsync("Câu hỏi đã bôi đen");
        host.Setup(h => h.CopyAnswerAsync(It.IsAny<string>())).ReturnsAsync(true);
        browser.SetupGet(b => b.EmbeddableControl).Returns(new System.Windows.Forms.Panel());
        var text = new Mock<IText>();
        text.Setup(t => t.Get(It.IsAny<TextKey>())).Returns<TextKey>(k => k.ToString());
        var settings = new AppSettings().Browser;
        var type = Assembly.LoadFrom(Path.Combine(runtime, "SafeExamBrowser.UserInterface.Desktop.dll")).GetType("SafeExamBrowser.UserInterface.Desktop.Windows.BrowserWindow");
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { browser.Object, settings, false, text.Object, Mock.Of<ILogger>() }, null);
        window.Width = 1000; window.Height = 650;
        window.Measure(new Size(1000, 650)); window.Arrange(new Rect(0, 0, 1000, 650)); window.UpdateLayout();
        var column = (ColumnDefinition)window.FindName("ChatGptColumn");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        host.Verify(h => h.CreateChatGptAssistant(), Times.Never);
        PumpDispatcher();
        host.Verify(h => h.CreateChatGptAssistant(), Times.Once);
        host.Verify(h => h.GetSelectedTextAsync(), Times.Never);
        assistant.Verify(a => a.BeginLogin(), Times.Never);
        assistant.Verify(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>()), Times.Never);
        Check(column.Width.Value == 0 && ((FrameworkElement)window.FindName("ChatGptPanel")).Visibility == Visibility.Collapsed,
            "window load starts ChatGPT after yielding to the UI and keeps the panel hidden without sending");
        Check(((FrameworkElement)window.FindName("ChatGptFeedbackBadge")).Visibility == Visibility.Collapsed,
            "background initialization does not show submission feedback");
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpDispatcher();
        host.Verify(h => h.CreateChatGptAssistant(), Times.Once);
        Check(true, "repeated window loads reuse the existing assistant");
        host.Raise(h => h.ChatGptRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.BeginLogin(), Times.Never);
        Invoke(type, window, "ReloadChatGpt_Click", null, new RoutedEventArgs());
        assistant.Verify(a => a.Reload(), Times.Once);
        assistant.Verify(a => a.BeginLogin(), Times.Never);
        Check(true, "opening and reloading the panel do not request login");
        host.Raise(h => h.ChatGptLoginRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.BeginLogin(), Times.Once);
        host.Verify(h => h.GetSelectedTextAsync(), Times.Never);
        assistant.Verify(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>()), Times.Never);
        Check(column.Width.Value > 0, "login shortcut opens authentication without selecting or sending a question");
        Invoke(type, window, "CloseChatGpt_Click", null, new RoutedEventArgs());
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.MultipleChoice, IsValid = true, Text = "B" });
        ((ComboBox)window.FindName("ChatGptQuestionType")).SelectedIndex = (int)ChatGptQuestionKind.Essay;
        host.Raise(h => h.ChatGptQueryRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.AskQuestionAsync("Câu hỏi đã bôi đen", ChatGptQuestionKind.MultipleChoice, It.IsAny<CancellationToken>()), Times.Once);
        Check(((TextBlock)window.FindName("ChoiceAnswerText")).Text == "B" && column.Width.Value == 0,
            "multiple-choice shortcut selects its explicit type and leaves the panel hidden");
        var labelledQuestion = "2 + 2? A. 3 B. 4";
        host.Setup(h => h.GetSelectedTextAsync()).ReturnsAsync(labelledQuestion);
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.Essay, IsValid = true, Text = "Lời giải." });
        host.Raise(h => h.ChatGptEssayRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.AskQuestionAsync(labelledQuestion, ChatGptQuestionKind.Essay, It.IsAny<CancellationToken>()), Times.Once);
        Check(((TextBox)window.FindName("ChatGptAnswerBox")).Text == "Lời giải." && column.Width.Value == 0,
            "essay shortcut uses essay mode even when the selected question has answer labels");
        host.Verify(h => h.CopyAnswerAsync("Lời giải."), Times.Once);
        host.Setup(h => h.GetSelectedTextAsync()).ReturnsAsync("Câu hỏi đã bôi đen");
        host.Invocations.Clear();
        assistant.Invocations.Clear();
        Invoke(type, window, "ExpandChatGpt"); window.UpdateLayout();
        Check(column.Width.IsAbsolute && column.Width.Value <= 340, "panel opens at compact width");
        var question = (TextBox)window.FindName("ChatGptQuestion"); question.Text = "Câu hỏi đang soạn";
        Invoke(type, window, "CollapseChatGpt_Click", null, new RoutedEventArgs());
        Check(column.Width.Value == 32 && ((FrameworkElement)window.FindName("ChatGptPanel")).Visibility == Visibility.Collapsed, "panel collapses to a 32px strip");
        Check(((FrameworkElement)window.FindName("RestoreChatGptButton")).Visibility == Visibility.Visible, "restore control remains available");
        Invoke(type, window, "RestoreChatGpt_Click", null, new RoutedEventArgs());
        Check(question.Text == "Câu hỏi đang soạn" && ((FrameworkElement)window.FindName("ChatGptPanel")).Visibility == Visibility.Visible, "restore preserves the draft");
        Invoke(type, window, "CloseChatGpt_Click", null, new RoutedEventArgs());
        host.Verify(h => h.CreateChatGptAssistant(), Times.Never);
        Check(column.Width.Value == 0, "closing the panel hides it");
        Invoke(type, window, "OpenChatGpt");
        host.Verify(h => h.GetSelectedTextAsync(), Times.Never);
        assistant.Verify(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>()), Times.Never);
        Check(question.Text == "Câu hỏi đang soạn", "opening for login does not capture or send a question");
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.MultipleChoice, IsValid = true, Text = "B" });
        Invoke(type, window, "CollapseChatGpt_Click", null, new RoutedEventArgs());
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        var answerBox = (TextBox)window.FindName("ChatGptAnswerBox");
        assistant.Verify(a => a.AskQuestionAsync("Câu hỏi đã bôi đen", ChatGptQuestionKind.MultipleChoice, It.IsAny<CancellationToken>()), Times.Once);
        host.Verify(h => h.CopyAnswerAsync(It.IsAny<string>()), Times.Never);
        Check(((TextBlock)window.FindName("ChoiceAnswerText")).Text == "B" && column.Width.Value == 32,
            "query shows the answer while leaving the panel collapsed");
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.Essay, IsValid = true, Text = "Nội dung tự luận." });
        Invoke(type, window, "CloseChatGpt_Click", null, new RoutedEventArgs());
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.Essay);
        host.Verify(h => h.CopyAnswerAsync("Nội dung tự luận."), Times.Once);
        Check(answerBox.Text == "Nội dung tự luận." && column.Width.Value == 0,
            "query copies the essay while leaving the panel hidden");
        host.Setup(h => h.CopyAnswerAsync(It.IsAny<string>())).ReturnsAsync(false);
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.Essay);
        Check(((TextBlock)window.FindName("ChatGptStatus")).Text.Contains("chính sách clipboard"), "clipboard refusal is reported without losing the normalized answer");
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.MultipleChoice, IsValid = false, Text = "unvalidated", Error = "Invalid answer" });
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        Check(answerBox.Text == "Invalid answer" && !((Button)window.FindName("CopyChatGptAnswerButton")).IsEnabled, "invalid answers are not offered for copying");
        Check(((TextBlock)window.FindName("ChatGptFeedbackText")).Text.Contains("Chưa hoàn tất"), "a failed request shows compact failure feedback");

        assistant.Invocations.Clear();
        host.Invocations.Clear();
        var selectedQuestion = new System.Threading.Tasks.TaskCompletionSource<string>();
        var pendingAnswer = new System.Threading.Tasks.TaskCompletionSource<ChatGptAnswer>();
        host.Setup(h => h.GetSelectedTextAsync()).Returns(selectedQuestion.Task);
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).Returns(pendingAnswer.Task);
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        var feedback = (TextBlock)window.FindName("ChatGptFeedbackText");
        var feedbackBadge = (Border)window.FindName("ChatGptFeedbackBadge");
        Check(feedback.Text.Contains("Đang đọc") && feedbackBadge.Visibility == Visibility.Visible && column.Width.Value == 0,
            "query immediately acknowledges selection reading while the panel is hidden");
        ((ComboBox)window.FindName("ChatGptQuestionType")).SelectedIndex = (int)ChatGptQuestionKind.Essay;
        selectedQuestion.SetResult("Câu hỏi đã bôi đen");
        PumpDispatcher();
        assistant.Verify(a => a.AskQuestionAsync("Câu hỏi đã bôi đen", ChatGptQuestionKind.MultipleChoice, It.IsAny<CancellationToken>()), Times.Once);
        Check(true, "changing the selector while reading does not change the shortcut's question type");
        Check(feedback.Text == "Đang gửi…", "pending submission never reports that the question was already sent");
        assistant.Raise(a => a.QuestionSent += null);
        PumpDispatcher();
        Check(feedback.Text == "Đã gửi · đang chờ…" && column.Width.Value == 0, "confirmed submission displays compact feedback without opening the panel");
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        host.Raise(h => h.ChatGptEssayRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>()), Times.Once);
        Check(feedback.Text == "Đã gửi · đang chờ…", "repeated shortcut preserves the confirmed state and sends no duplicate");
        host.Raise(h => h.ChatGptLoginRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.BeginLogin(), Times.Never);
        Check(true, "login shortcut cannot interrupt an active question");
        pendingAnswer.SetResult(new ChatGptAnswer { Kind = ChatGptQuestionKind.MultipleChoice, IsValid = true, Text = "D" });
        PumpDispatcher();
        Check(feedback.Text == "Đã có đáp án" && ((TextBlock)window.FindName("ChoiceAnswerText")).Text == "D", "answer completion updates feedback and preserves the choice badge");
        window.Measure(new Size(1000, 650)); window.Arrange(new Rect(0, 0, 1000, 650)); window.UpdateLayout();
        Check(feedbackBadge.ActualWidth < 180 && feedbackBadge.ActualHeight < 30, "feedback occupies a small badge at the bottom of the window");
        Thread.Sleep(3200);
        PumpDispatcher();
        Check(feedbackBadge.Visibility == Visibility.Collapsed && ((Border)window.FindName("ChoiceAnswerBadge")).Visibility == Visibility.Visible,
            "completion feedback disappears after three seconds while the answer remains visible");
        host.Setup(h => h.GetSelectedTextAsync()).ReturnsAsync("");
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        Check(feedback.Text == "Kiểm tra vùng chọn", "an empty selection shows feedback without claiming a successful send");
        host.Setup(h => h.GetSelectedTextAsync()).ReturnsAsync("Câu hỏi đã bôi đen");
        window.Close();
        assistant.Verify(a => a.Dispose(), Times.Once);
        host.Raise(h => h.ChatGptLoginRequested += null);
        PumpDispatcher();
        assistant.Verify(a => a.BeginLogin(), Times.Never);
        Check(true, "closed windows unsubscribe the login shortcut");

        host.Invocations.Clear();
        host.Setup(h => h.CreateChatGptAssistant()).Throws(new InvalidOperationException("Initialization failed"));
        window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { browser.Object, settings, false, text.Object, Mock.Of<ILogger>() }, null);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpDispatcher();
        Check(((TextBlock)window.FindName("ChatGptStatus")).Text.Contains("thử lại"), "startup failure is contained and reports retry instructions");
        host.Setup(h => h.CreateChatGptAssistant()).Returns(assistant.Object);
        assistant.Setup(a => a.AskQuestionAsync(It.IsAny<string>(), It.IsAny<ChatGptQuestionKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatGptAnswer { Kind = ChatGptQuestionKind.MultipleChoice, IsValid = true, Text = "C" });
        Invoke(type, window, "AskChatGpt", ChatGptQuestionKind.MultipleChoice);
        Check(((TextBlock)window.FindName("ChoiceAnswerText")).Text == "C", "query retries a failed startup without opening the panel");
        window.Close();

        host.Invocations.Clear();
        host.SetupGet(h => h.CanUseChatGpt).Returns(false);
        window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { browser.Object, settings, false, text.Object, Mock.Of<ILogger>() }, null);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        PumpDispatcher();
        host.Verify(h => h.CreateChatGptAssistant(), Times.Never);
        Check(true, "windows without ChatGPT support do not initialize an assistant");
        window.Close();

        host.SetupGet(h => h.CanUseChatGpt).Returns(true);
        window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { browser.Object, settings, false, text.Object, Mock.Of<ILogger>() }, null);
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        window.Close();
        PumpDispatcher();
        host.Verify(h => h.CreateChatGptAssistant(), Times.Never);
        Check(true, "closing before the queued startup prevents assistant creation");
        app.Shutdown();
    }
    static void Invoke(Type type, object target, string method, params object[] args) { type.GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args); }
    static void Check(bool passed, string label) { if (!passed) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    static void PumpDispatcher()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
