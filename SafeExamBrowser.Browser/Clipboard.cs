/*
 * Copyright (c) 2025 ETH Zürich, IT Services
 * 
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Threading.Tasks;
using System.Threading;
using System.Text;
using CefSharp;
using SafeExamBrowser.Browser.Events;
using SafeExamBrowser.Logging.Contracts;
using SafeExamBrowser.Settings.Security;
using BrowserSettings = SafeExamBrowser.Settings.Browser.BrowserSettings;

namespace SafeExamBrowser.Browser
{
	internal class Clipboard
	{
		private readonly ILogger logger;
		private readonly BrowserSettings settings;
		private readonly ClipboardPolicy policy;

		internal string Content { get; private set; }

		internal event ClipboardChangedEventHandler Changed;

		internal Clipboard(ILogger logger, BrowserSettings settings, ClipboardPolicy? policy = null)
		{
			this.logger = logger;
			this.settings = settings;
			this.policy = policy ?? (settings.UseIsolatedClipboard ? ClipboardPolicy.Isolated : ClipboardPolicy.Block);
		}

		internal Task<bool> CopyAnswerAsync(string text)
		{
			if (policy == ClipboardPolicy.Block || string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
			if (policy == ClipboardPolicy.Isolated)
			{
				if (!settings.UseIsolatedClipboard) return Task.FromResult(false);
				Content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
				return Task.Run(() => { Changed?.Invoke(Guid.NewGuid().ToString()); return true; });
			}
			if (policy != ClipboardPolicy.Allow) return Task.FromResult(false);
			var completion = new TaskCompletionSource<bool>();
			var thread = new Thread(() =>
			{
				try
				{
					System.Windows.Forms.Clipboard.SetText(text, System.Windows.Forms.TextDataFormat.UnicodeText);
					completion.TrySetResult(true);
				}
				catch (Exception)
				{
					logger.Warn("Could not write the normalized answer to the permitted system clipboard.");
					completion.TrySetResult(false);
				}
			}) { IsBackground = true };
			thread.SetApartmentState(ApartmentState.STA);
			thread.Start();
			return completion.Task;
		}

		internal void Update(JavascriptMessageReceivedEventArgs message)
		{
			if (settings.UseIsolatedClipboard)
			{
				try
				{
					var data = message.ConvertMessageTo<Data>();

					if (data != default && data.Type == "Clipboard" && TrySetContent(data.Content))
					{
						Task.Run(() => Changed?.Invoke(data.Id));
					}
				}
				catch (Exception e)
				{
					logger.Error($"Failed to process browser message '{message}'!", e);
				}
			}
		}

		private bool TrySetContent(object value)
		{
			var text = value as string;

			if (text != default)
			{
				Content = text;
			}

			return text != default;
		}

		private class Data
		{
			public string Content { get; set; }
			public string Id { get; set; }
			public string Type { get; set; }
		}
	}
}
