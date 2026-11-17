using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SafeExamBrowser.UserInterface.Contracts.Browser;

namespace SafeExamBrowser.Browser
{
	internal static class QuestionAnswerFormatter
	{
		private static string[] Labels(string question)
		{
			return Regex.Matches(question ?? "", @"(?:^|\s)\(?(?<label>[A-H])[.):]\s+")
				.Cast<Match>().Select(m => m.Groups["label"].Value).Distinct().OrderBy(x => x).ToArray();
		}

		internal static ChatGptQuestionKind ResolveKind(string question, ChatGptQuestionKind kind)
		{
			if (kind != ChatGptQuestionKind.Automatic) return kind;
			return Labels(question).Length >= 2 || Regex.IsMatch(question ?? "", @"trắc\s*nghiệm|multiple[ -]choice", RegexOptions.IgnoreCase)
				? ChatGptQuestionKind.MultipleChoice : ChatGptQuestionKind.Essay;
		}

		internal static string BuildPrompt(string question, ChatGptQuestionKind kind)
		{
			kind = ResolveKind(question, kind);
			var instruction = kind == ChatGptQuestionKind.MultipleChoice
				? "Giải câu trắc nghiệm sau. CHỈ trả lời nhãn đáp án, ví dụ B; nếu có nhiều đáp án thì A, C. Không giải thích, không nhắc lại đề, không Markdown. Chỉ dùng nhãn có trong đề. Nếu đề không có nhãn thì chỉ ghi nội dung đáp án ngắn gọn."
				: "Trả lời câu tự luận sau bằng tiếng Việt, chỉ viết nội dung câu trả lời hoàn chỉnh để có thể dán vào ô trả lời. Dùng văn bản thuần, xuống dòng hợp lý, không Markdown, không lời mở đầu như 'Dưới đây là', không nhắc lại đề. Giữ công thức và các bước cần thiết nếu đề yêu cầu.";
			return instruction + " Nếu thiếu dữ kiện hoặc không xác định được, chỉ trả lời CHƯA_ĐỦ_DỮ_KIỆN.\n\nĐỀ BÀI:\n" + question;
		}

		internal static ChatGptAnswer Normalize(string question, ChatGptQuestionKind kind, string raw)
		{
			kind = ResolveKind(question, kind);
			var text = PlainText(raw);
			var answer = new ChatGptAnswer { Kind = kind, Text = text };
			if (text.Length == 0 || text.Length > 60000)
			{
				answer.Error = "Câu trả lời trống hoặc quá dài để chuẩn hóa.";
				return answer;
			}
			if (text.Replace('_', ' ').Trim().Equals("CHƯA ĐỦ DỮ KIỆN", StringComparison.OrdinalIgnoreCase))
			{
				answer.Text = "Chưa đủ dữ kiện.";
				answer.Error = "AI chưa xác định được câu trả lời.";
				return answer;
			}
			if (kind == ChatGptQuestionKind.MultipleChoice)
			{
				var firstLine = text.Split('\n')[0].Trim();
				firstLine = Regex.Replace(firstLine, @"^(?:Đáp\s*án(?:\s*đúng)?|Answer|Kết\s*quả)\s*(?:là)?\s*[:\-]?\s*", "", RegexOptions.IgnoreCase);
				var labels = Labels(question);
				var matched = Regex.Match(firstLine, @"^(?<label>[A-H])(?:[.)]\s+.+|[.)])?$", RegexOptions.IgnoreCase);
				if (matched.Success) firstLine = matched.Groups["label"].Value;
				if (Regex.IsMatch(firstLine, @"^[A-H](?:\s*[,;/]\s*[A-H])*$", RegexOptions.IgnoreCase))
				{
					var selected = Regex.Matches(firstLine.ToUpperInvariant(), "[A-H]").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(x => x).ToArray();
					if (labels.Length > 0 && selected.Any(label => !labels.Contains(label)))
					{
						answer.Error = "AI trả về nhãn đáp án không có trong đề.";
						return answer;
					}
					answer.Text = string.Join(", ", selected);
				}
				else if (labels.Length == 0 && text.Length <= 160 && !text.Contains("\n"))
					answer.Text = firstLine;
				else
				{
					answer.Error = "Đầu ra trắc nghiệm chưa đúng định dạng đáp án; hãy kiểm tra trong ChatGPT.";
					return answer;
				}
			}
			answer.IsValid = true;
			return answer;
		}

		internal static string PlainText(string raw)
		{
			var text = (raw ?? "").Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00a0', ' ').Replace("\u200b", "").Replace("\ufeff", "");
			text = Regex.Replace(text, @"(?m)^\s*```[^\n]*\n?", "");
			text = Regex.Replace(text, @"(?m)^\s*#{1,6}\s+", "");
			text = Regex.Replace(text, @"\*\*([^*\n]+)\*\*", "$1");
			text = Regex.Replace(text, @"__([^_\n]+)__", "$1");
			text = Regex.Replace(text, @"`([^`\n]+)`", "$1");
			text = Regex.Replace(text, @"\[([^\]]+)\]\((https?://[^)]+)\)", "$1 ($2)");
			text = string.Join("\n", text.Split('\n').Select(line => line.TrimEnd()));
			return Regex.Replace(text, "\n{3,}", "\n\n").Trim();
		}
	}
}
