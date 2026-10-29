using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Crimson.Utils;

/// <summary>
/// Turns the store's long description, which mixes Markdown with HTML comments and
/// tags, into plain paragraphs a TextBlock can show.
/// </summary>
public static partial class StoreDescriptionFormatter
{
	public static string ToPlainText(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return string.Empty;

		var text = value.Replace("\r\n", "\n");
		text = HtmlCommentPattern().Replace(text, string.Empty);
		text = LineBreakTagPattern().Replace(text, "\n");
		text = HtmlTagPattern().Replace(text, string.Empty);
		text = MarkdownImagePattern().Replace(text, string.Empty);
		text = MarkdownLinkPattern().Replace(text, "$1");
		text = HeadingPattern().Replace(text, string.Empty);
		text = BulletPattern().Replace(text, "• ");
		text = EmphasisPattern().Replace(text, "$2");
		text = WebUtility.HtmlDecode(text);
		text = TrailingSpacePattern().Replace(text, "\n");
		text = BlankLinesPattern().Replace(text, "\n\n");
		return text.Trim();
	}

	/// <summary>
	/// Splits the description into the headings, paragraphs and pictures the web store shows,
	/// in order. Text runs go through <see cref="ToPlainText"/>; only https images are kept.
	/// </summary>
	public static IReadOnlyList<StoreDescriptionBlock> ToBlocks(string? value)
	{
		var blocks = new List<StoreDescriptionBlock>();
		if (string.IsNullOrWhiteSpace(value))
			return blocks;

		var paragraph = new StringBuilder();
		void FlushParagraph()
		{
			var text = ToPlainText(paragraph.ToString());
			paragraph.Clear();
			if (text.Length > 0)
				blocks.Add(new StoreDescriptionBlock(StoreDescriptionBlockKind.Text, text, null));
		}

		var text = HtmlCommentPattern().Replace(value.Replace("\r\n", "\n"), string.Empty);
		foreach (var line in text.Split('\n'))
		{
			var heading = HeadingLinePattern().Match(line);
			if (heading.Success)
			{
				FlushParagraph();
				var title = ToPlainText(heading.Groups[1].Value);
				if (title.Length > 0)
					blocks.Add(new StoreDescriptionBlock(StoreDescriptionBlockKind.Heading, title, null));
				continue;
			}

			var images = InlineImagePattern().Matches(line);
			if (images.Count == 0)
			{
				paragraph.Append(line).Append('\n');
				continue;
			}

			// Text around a picture stays in reading order: before it, the picture, after it.
			var position = 0;
			foreach (Match image in images)
			{
				paragraph.Append(line, position, image.Index - position).Append('\n');
				var url = image.Groups["md"].Success ? image.Groups["md"].Value : image.Groups["html"].Value;
				if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
					uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
				{
					FlushParagraph();
					blocks.Add(new StoreDescriptionBlock(StoreDescriptionBlockKind.Image, null, url));
				}
				position = image.Index + image.Length;
			}
			paragraph.Append(line, position, line.Length - position).Append('\n');
		}

		FlushParagraph();
		return blocks;
	}

	[GeneratedRegex(@"^[ \t]*#{1,6}[ \t]+(.+)$")]
	private static partial Regex HeadingLinePattern();

	[GeneratedRegex(@"!\[[^\]]*\]\((?<md>[^)\s]+)[^)]*\)|<img[^>]*\ssrc=[""'](?<html>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
	private static partial Regex InlineImagePattern();

	[GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
	private static partial Regex HtmlCommentPattern();

	[GeneratedRegex(@"<br\s*/?>|</p>", RegexOptions.IgnoreCase)]
	private static partial Regex LineBreakTagPattern();

	[GeneratedRegex(@"</?[a-zA-Z][^>]*>")]
	private static partial Regex HtmlTagPattern();

	[GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
	private static partial Regex MarkdownImagePattern();

	[GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
	private static partial Regex MarkdownLinkPattern();

	[GeneratedRegex(@"^[ \t]*#{1,6}[ \t]*", RegexOptions.Multiline)]
	private static partial Regex HeadingPattern();

	[GeneratedRegex(@"^[ \t]*[-*+][ \t]+", RegexOptions.Multiline)]
	private static partial Regex BulletPattern();

	// Single underscores are left alone so names like some_file_name survive.
	[GeneratedRegex(@"(\*\*|__|\*)(\S(?:.*?\S)?)\1")]
	private static partial Regex EmphasisPattern();

	[GeneratedRegex(@"[ \t]+\n")]
	private static partial Regex TrailingSpacePattern();

	[GeneratedRegex(@"\n{3,}")]
	private static partial Regex BlankLinesPattern();
}

public enum StoreDescriptionBlockKind
{
	Heading,
	Text,
	Image
}

public sealed record StoreDescriptionBlock(StoreDescriptionBlockKind Kind, string? Text, string? ImageUrl)
{
	public bool IsHeading => Kind == StoreDescriptionBlockKind.Heading;
	public bool IsText => Kind == StoreDescriptionBlockKind.Text;
	public bool IsImage => Kind == StoreDescriptionBlockKind.Image;
}
