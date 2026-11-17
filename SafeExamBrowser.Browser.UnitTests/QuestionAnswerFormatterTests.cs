using Microsoft.VisualStudio.TestTools.UnitTesting;
using SafeExamBrowser.UserInterface.Contracts.Browser;

namespace SafeExamBrowser.Browser.UnitTests
{
	[TestClass]
	public class QuestionAnswerFormatterTests
	{
		private const string Question = "2 + 2 = ?\nA. 3\nB. 4\nC. 5";

		[TestMethod]
		public void MustDetectChoicesAndRespectExplicitEssayMode()
		{
			Assert.AreEqual(ChatGptQuestionKind.MultipleChoice, QuestionAnswerFormatter.ResolveKind(Question, ChatGptQuestionKind.Automatic));
			Assert.AreEqual(ChatGptQuestionKind.Essay, QuestionAnswerFormatter.ResolveKind(Question, ChatGptQuestionKind.Essay));
			Assert.AreEqual(ChatGptQuestionKind.Essay, QuestionAnswerFormatter.ResolveKind("Giải thích phép cộng 2 + 2.", ChatGptQuestionKind.Automatic));
		}

		[TestMethod]
		[DataRow("**Đáp án: B.**", "B")]
		[DataRow("B. 4\nVì phép cộng...", "B")]
		[DataRow("C; A", "A, C")]
		public void MustNormalizeChoicesWithoutExplanation(string raw, string expected)
		{
			var answer = QuestionAnswerFormatter.Normalize(Question, ChatGptQuestionKind.Automatic, raw);
			Assert.IsTrue(answer.IsValid);
			Assert.AreEqual(expected, answer.Text);
		}

		[TestMethod]
		public void MustRejectUnknownChoiceAndUnstructuredExplanation()
		{
			Assert.IsFalse(QuestionAnswerFormatter.Normalize(Question, ChatGptQuestionKind.MultipleChoice, "D").IsValid);
			Assert.IsFalse(QuestionAnswerFormatter.Normalize(Question, ChatGptQuestionKind.MultipleChoice, "Tôi nghĩ đáp án đúng là bốn.").IsValid);
		}

		[TestMethod]
		public void MustProducePasteableEssayWithoutDamagingMath()
		{
			var answer = QuestionAnswerFormatter.Normalize("Giải thích", ChatGptQuestionKind.Essay, "# Lời giải\r\n\r\n**Công thức:** a * b * c.\r\n\r\n\r\nĐáp số: 4.");
			Assert.IsTrue(answer.IsValid);
			Assert.AreEqual("Lời giải\n\nCông thức: a * b * c.\n\nĐáp số: 4.", answer.Text);
		}

		[TestMethod]
		public void MustNotTreatMissingDataAsAReadyAnswer()
		{
			Assert.IsFalse(QuestionAnswerFormatter.Normalize(Question, ChatGptQuestionKind.Automatic, "CHƯA_ĐỦ_DỮ_KIỆN").IsValid);
			Assert.IsFalse(QuestionAnswerFormatter.Normalize("Đề bài", ChatGptQuestionKind.Essay, " ").IsValid);
		}
	}
}
