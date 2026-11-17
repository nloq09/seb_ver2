using System;
using System.Threading.Tasks;
using System.Threading;

namespace SafeExamBrowser.UserInterface.Contracts.Browser
{
	public enum ChatGptQuestionKind { Automatic, MultipleChoice, Essay }

	public class ChatGptAnswer
	{
		public ChatGptQuestionKind Kind { get; set; }
		public string Text { get; set; }
		public bool IsValid { get; set; }
		public string Error { get; set; }
	}
	/// <summary>A separate, session-scoped browser for the ChatGPT website.</summary>
	public interface IChatGptAssistant : IDisposable
	{
		object EmbeddableControl { get; }
		event Action HideAnswerRequested;
		event Action<string> StatusChanged;
		Task<bool> PreparePromptAsync(string prompt);
		Task<bool> SendPreparedPromptAsync(string prompt);
		Task<ChatGptAnswer> AskQuestionAsync(string question, ChatGptQuestionKind kind, CancellationToken cancellationToken);
		void Reload();
	}

	/// <summary>Optional capability; user interfaces without a chat panel can ignore it.</summary>
	public interface IChatGptHost
	{
		bool CanUseChatGpt { get; }
		event Action ChatGptRequested;
		event Action ChatGptQueryRequested;
		event Action ChatGptHideAnswerRequested;
		IChatGptAssistant CreateChatGptAssistant();
		Task<string> GetSelectedTextAsync();
		Task<bool> CopyAnswerAsync(string text);
	}
}
