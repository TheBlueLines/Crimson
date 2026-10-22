using Crimson.Models;
using Crimson.ViewModels;

namespace Crimson.Tests;

public sealed class PortablePresentationTests
{
	[Theory]
	[InlineData(null, GamePrimaryAction.Install)]
	[InlineData(InstallState.NotInstalled, GamePrimaryAction.Install)]
	[InlineData(InstallState.Installed, GamePrimaryAction.Play)]
	[InlineData(InstallState.NeedUpdate, GamePrimaryAction.Update)]
	[InlineData(InstallState.Broken, GamePrimaryAction.Repair)]
	[InlineData(InstallState.Installing, GamePrimaryAction.None)]
	public void MapsInstallStateToSemanticPrimaryAction(
		InstallState? installState,
		GamePrimaryAction expected)
	{
		Assert.Equal(expected, GameInfoViewModel.GetPrimaryAction(installState));
	}

	[Fact]
	public void InstallArtworkPrefersTallImage()
	{
		var game = GameWithImages(
			new KeyImage { Type = "DieselGameBox", Url = "box" },
			new KeyImage { Type = "DieselGameBoxTall", Url = "tall" });

		Assert.Equal("tall", AppInstallDialogViewModel.SelectImageUrl(game));
	}

	[Fact]
	public void InstallArtworkFallsBackToRegularBox()
	{
		var game = GameWithImages(new KeyImage { Type = "DieselGameBox", Url = "box" });

		Assert.Equal("box", AppInstallDialogViewModel.SelectImageUrl(game));
	}

	[Fact]
	public void MissingInstallArtworkProducesNull()
	{
		Assert.Null(AppInstallDialogViewModel.SelectImageUrl(GameWithImages()));
	}

	private static Game GameWithImages(params KeyImage[] images) => new()
	{
		AppName = "test",
		AppTitle = "Test",
		AssetInfos = null!,
		Metadata = new Metadata { KeyImages = [.. images] }
	};
}
