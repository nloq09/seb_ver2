using System;

namespace SafeExamBrowser.UserInterface.Shared.Utilities
{
	public static class VersionLabel
	{
		public static string Format(string version)
		{
			const string suffix = "-custom";

			if (version?.EndsWith(suffix, StringComparison.Ordinal) == true)
			{
				return version.Substring(0, version.Length - suffix.Length);
			}

			return version;
		}
	}
}
