using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Crimson.Models;
using Serilog;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;

namespace Crimson.Core;

public sealed class GameShortcutManager : IGameShortcutManager
{
	private readonly HttpClient _httpClient;
	private readonly ILogger _log;
	private readonly string _desktopDirectory;
	private readonly string _startMenuDirectory;
	private readonly string _iconDirectory;

	public GameShortcutManager(HttpClient httpClient, ILogger log)
		: this(
			httpClient,
			log,
			Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
				"Programs",
				"Crimson Games"),
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Crimson",
				"shortcut-icons"))
	{
	}

	internal GameShortcutManager(
		HttpClient httpClient,
		ILogger log,
		string desktopDirectory,
		string startMenuDirectory,
		string iconDirectory)
	{
		_httpClient = httpClient;
		_log = log;
		_desktopDirectory = desktopDirectory;
		_startMenuDirectory = startMenuDirectory;
		_iconDirectory = iconDirectory;
	}

	public async Task CreateAsync(Game game, GameShortcutLocation location, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var path = GetShortcutPath(game, location);
		RequireAvailableShortcutPath(path, game.AppName);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var iconPath = await GetIconPathAsync(game, cancellationToken);
		cancellationToken.ThrowIfCancellationRequested();
		CreateShellLink(path, game, iconPath);
	}

	public void Remove(Game game)
	{
		RemoveOwnedShortcut(GetShortcutPath(game, GameShortcutLocation.StartMenu), game.AppName);
		RemoveOwnedShortcut(GetShortcutPath(game, GameShortcutLocation.Desktop), game.AppName);
		TryDeleteFile(
			Path.Combine(_iconDirectory, "games", GameShortcutNaming.GetIconFileName(game.AppName)),
			game.AppName);
	}

	private string GetShortcutPath(Game game, GameShortcutLocation location)
	{
		var directory = location == GameShortcutLocation.Desktop
			? _desktopDirectory
			: _startMenuDirectory;
		return Path.Combine(directory, GameShortcutNaming.GetShortcutFileName(game.AppTitle));
	}

	private void RemoveOwnedShortcut(string path, string appName)
	{
		if (IsOwnedShortcut(path, appName))
			TryDeleteFile(path, appName);
	}

	private static void RequireAvailableShortcutPath(string path, string appName)
	{
		if (File.Exists(path) && !IsOwnedShortcut(path, appName))
			throw new IOException("An unrelated file already occupies the requested shortcut path.");
	}

	private static bool IsOwnedShortcut(string path, string appName)
	{
		if (!File.Exists(path))
			return false;

		try
		{
			return ReadShortcutOwnership(path, appName);
		}
		catch (COMException)
		{
			return false;
		}
		catch (IOException)
		{
			return false;
		}
		catch (UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static bool ReadShortcutOwnership(string path, string appName)
	{
		IShellLinkW link = (IShellLinkW)new ShellLink();
		try
		{
			((IPersistFile)link).Load(path, 0);
			var target = new StringBuilder(32768);
			var arguments = new StringBuilder(32768);
			link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
			link.GetArguments(arguments, arguments.Capacity);
			var targetsCrimson = string.Equals(target.ToString(), GetPackagedAliasPath(), StringComparison.OrdinalIgnoreCase) ||
				string.Equals(target.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
			return targetsCrimson && string.Equals(arguments.ToString(),
				GameLaunchRequest.CreateCommandLineArgument(appName), StringComparison.Ordinal);
		}
		finally
		{
			Marshal.FinalReleaseComObject(link);
		}
	}

	private void TryDeleteFile(string path, string appName)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Failed to remove shortcut artifact {Path} for {AppName}", path, appName);
		}
	}

	private async Task<string> GetIconPathAsync(Game game, CancellationToken cancellationToken)
	{
		var gameIconDirectory = Path.Combine(_iconDirectory, "games");
		Directory.CreateDirectory(gameIconDirectory);
		var iconPath = Path.Combine(gameIconDirectory, GameShortcutNaming.GetIconFileName(game.AppName));
		if (File.Exists(iconPath))
			return iconPath;

		var imageUrl = FindImageUrl(game);
		if (!string.IsNullOrWhiteSpace(imageUrl))
		{
			try
			{
				var imageUri = EpicEndpointPolicy.RequireContentUri(imageUrl);
				await using var source = await _httpClient.GetStreamAsync(imageUri, cancellationToken);
				await CreateIconFileAsync(source, iconPath);
				return iconPath;
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				_log.Warning(ex, "Failed to create shortcut icon for {AppName}", game.AppName);
			}
		}

		return await GetCrimsonIconPathAsync();
	}

	private async Task<string> GetCrimsonIconPathAsync()
	{
		var iconPath = Path.Combine(_iconDirectory, "Crimson.ico");
		if (File.Exists(iconPath))
			return iconPath;

		try
		{
			var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.scale-200.png");
			await using var source = File.OpenRead(logoPath);
			await CreateIconFileAsync(source, iconPath);
			return iconPath;
		}
		catch (Exception ex)
		{
			_log.Warning(ex, "Failed to create the Crimson shortcut icon");
			return Environment.ProcessPath ?? string.Empty;
		}
	}

	private static string? FindImageUrl(Game game)
	{
		var images = game.Metadata?.KeyImages;
		if (images is null)
			return null;

		return images.FirstOrDefault(image => image.Type == "DieselGameBoxTall")?.Url
			?? images.FirstOrDefault(image => image.Type == "DieselGameBox")?.Url;
	}

	internal static async Task CreateIconFileAsync(Stream source, string path)
	{
		var (png, size) = await CreateSquarePngAsync(source);
		WriteIcon(path, png, size);
	}

	private static async Task<(byte[] Png, uint Size)> CreateSquarePngAsync(Stream source)
	{
		using var bufferedSource = new MemoryStream();
		await source.CopyToAsync(bufferedSource);
		bufferedSource.Position = 0;
		using var sourceStream = bufferedSource.AsRandomAccessStream();
		var decoder = await BitmapDecoder.CreateAsync(sourceStream);
		var edge = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
		var size = Math.Min(edge, 256u);
		var pixels = await decoder.GetPixelDataAsync(
			BitmapPixelFormat.Bgra8,
			BitmapAlphaMode.Premultiplied,
			new BitmapTransform(),
			ExifOrientationMode.IgnoreExifOrientation,
			ColorManagementMode.DoNotColorManage);
		var croppedPixels = CropSquare(
			pixels.DetachPixelData(),
			decoder.PixelWidth,
			decoder.PixelHeight,
			edge);

		using var png = new MemoryStream();
		using var output = png.AsRandomAccessStream();
		var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
		encoder.SetPixelData(
			BitmapPixelFormat.Bgra8,
			BitmapAlphaMode.Premultiplied,
			edge,
			edge,
			decoder.DpiX > 0 ? decoder.DpiX : 96,
			decoder.DpiY > 0 ? decoder.DpiY : 96,
			croppedPixels);
		encoder.BitmapTransform.ScaledWidth = size;
		encoder.BitmapTransform.ScaledHeight = size;
		encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
		await encoder.FlushAsync();
		return (png.ToArray(), size);
	}

	private static byte[] CropSquare(byte[] source, uint width, uint height, uint edge)
	{
		var edgeLength = checked((int)edge);
		var rowBytes = checked(edgeLength * 4);
		var result = new byte[checked(rowBytes * edgeLength)];
		var x = (width - edge) / 2;
		var y = (height - edge) / 2;
		for (var row = 0; row < edgeLength; row++)
		{
			var sourceOffset = checked((int)(((y + (uint)row) * width + x) * 4));
			Buffer.BlockCopy(source, sourceOffset, result, row * rowBytes, rowBytes);
		}

		return result;
	}

	private static void WriteIcon(string path, byte[] png, uint size)
	{
		using var output = File.Create(path);
		using var writer = new BinaryWriter(output);
		writer.Write((ushort)0);
		writer.Write((ushort)1);
		writer.Write((ushort)1);
		writer.Write(size >= 256 ? (byte)0 : (byte)size);
		writer.Write(size >= 256 ? (byte)0 : (byte)size);
		writer.Write((byte)0);
		writer.Write((byte)0);
		writer.Write((ushort)1);
		writer.Write((ushort)32);
		writer.Write((uint)png.Length);
		writer.Write((uint)22);
		writer.Write(png);
	}

	internal static void CreateShellLink(
		string path,
		Game game,
		string iconPath,
		bool? packaged = null)
	{
		RequireAvailableShortcutPath(path, game.AppName);
		IShellLinkW link = (IShellLinkW)new ShellLink();
		try
		{
			if (packaged ?? IsPackaged())
				link.SetPath(GetPackagedAliasPath());
			else
				link.SetPath(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable."));
			link.SetArguments(GameLaunchRequest.CreateCommandLineArgument(game.AppName));


			link.SetDescription($"Launch {game.AppTitle} with Crimson");
			if (!string.IsNullOrEmpty(iconPath))
				link.SetIconLocation(iconPath, 0);
			((IPersistFile)link).Save(path, true);
		}
		finally
		{
			Marshal.FinalReleaseComObject(link);
		}
	}

	internal static string GetPackagedAliasPath() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Microsoft",
		"WindowsApps",
		"crimson.exe");

	private static bool IsPackaged()
	{
		try
		{
			_ = Package.Current.Id.Name;
			return true;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	[ComImport]
	[Guid("00021401-0000-0000-C000-000000000046")]
	private class ShellLink;

	[ComImport]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	[Guid("000214F9-0000-0000-C000-000000000046")]
	private interface IShellLinkW
	{
		void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
		void GetIDList(out IntPtr idList);
		void SetIDList(IntPtr idList);
		void GetDescription(IntPtr name, int maxName);
		void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
		void GetWorkingDirectory(IntPtr directory, int maxPath);
		void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
		void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
		void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
		void GetHotkey(out short hotkey);
		void SetHotkey(short hotkey);
		void GetShowCmd(out int showCommand);
		void SetShowCmd(int showCommand);
		void GetIconLocation(IntPtr iconPath, int iconPathLength, out int iconIndex);
		void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
		void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
		void Resolve(IntPtr window, uint flags);
		void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
	}
}
