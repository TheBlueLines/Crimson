using System.Net;
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
