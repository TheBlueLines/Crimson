using System.Text.Json;
using Crimson.Core;
using Crimson.Models;

namespace Crimson.Tests;

public sealed class GameLaunchRequestTests
{
	[Fact]
	public void ProtocolUriRoundTripsAppName()
	{
		var uri = GameLaunchRequest.CreateProtocolUri("Game Name+Test");

		var parsed = GameLaunchRequest.TryParse(uri, out var appName);

		Assert.True(parsed);
		Assert.Equal("Game Name+Test", appName);
	}

	[Theory]
	[InlineData("https://launch?app=Game")]
	[InlineData("crimson-launcher://launch?app=Game")]
	[InlineData("crimson://other?app=Game")]
	[InlineData("crimson://launch?app=")]
	[InlineData("crimson://launch?app=Game%0AInjected")]
	public void ProtocolParserRejectsInvalidRequests(string value)
	{
		Assert.False(GameLaunchRequest.TryParse(new Uri(value), out _));
	}

	[Fact]
	public void CommandLineArgumentRoundTripsAppName()
	{
		var argument = GameLaunchRequest.CreateCommandLineArgument("Game Name+Test");

		var parsed = GameLaunchRequest.TryParseCommandLine(argument, out var appName);

		Assert.True(parsed);
		Assert.Equal("Game Name+Test", appName);
	}

	[Fact]
	public void ShortcutSelectionsPersistWithInstallItem()
	{
		var install = new InstallItem("TestGame", ActionType.Install, "C:\\Games\\Test")
		{
			CreateDesktopShortcut = true,
			CreateStartMenuShortcut = true
		};

		var restored = JsonSerializer.Deserialize<InstallItem>(JsonSerializer.Serialize(install));

		Assert.NotNull(restored);
		Assert.True(restored.CreateDesktopShortcut);
		Assert.True(restored.CreateStartMenuShortcut);
	}
}
