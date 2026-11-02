using System;
using System.Runtime.InteropServices;

namespace Crimson.Utils;

/// <summary>
///	 Reads the icon Explorer shows for an executable straight from its resources. The shell
///	 thumbnail API was unreliable here: it fails with E_PENDING until Explorer has cached the file.
/// </summary>
public static class ExecutableIcon
{
	/// <summary>Returns premultiplied BGRA pixels of a <paramref name="size"/>-square icon, or null.</summary>
	public static byte[]? ReadPixels(string path, int size)
	{
		var icons = new IntPtr[1];
		var count = PrivateExtractIcons(path, 0, size, size, icons, IntPtr.Zero, 1, 0);
		if (count == 0 || count == uint.MaxValue || icons[0] == IntPtr.Zero)
			return null;

		var icon = icons[0];
		var info = default(IconInfo);
		try
		{
			if (!GetIconInfo(icon, out info) || info.Color == IntPtr.Zero)
				return null;

			var pixels = ReadBitmap(info.Color, size);
			if (pixels is null)
				return null;

			var hasAlpha = false;
			for (var i = 3; i < pixels.Length && !hasAlpha; i += 4)
				hasAlpha = pixels[i] != 0;

			// Legacy icons carry no alpha channel; their transparency lives in the AND mask.
			var mask = hasAlpha ? null : ReadBitmap(info.Mask, size);
			for (var i = 0; i < pixels.Length; i += 4)
			{
				var alpha = hasAlpha ? pixels[i + 3] : mask is null || mask[i] == 0 ? (byte)255 : (byte)0;
				pixels[i] = (byte)(pixels[i] * alpha / 255);
				pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
				pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
				pixels[i + 3] = alpha;
			}
			return pixels;
		}
		finally
		{
			if (info.Color != IntPtr.Zero) DeleteObject(info.Color);
			if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
			DestroyIcon(icon);
		}
	}

	private static byte[]? ReadBitmap(IntPtr bitmap, int size)
	{
		if (bitmap == IntPtr.Zero)
			return null;

		var header = new BitmapInfo
		{
			Header = new BitmapInfoHeader
			{
				Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
				Width = size,
				Height = -size, // top-down rows
				Planes = 1,
				BitCount = 32
			},
			Colors = new uint[256]
		};
		var pixels = new byte[size * size * 4];
		var dc = GetDC(IntPtr.Zero);
		try
		{
			return GetDIBits(dc, bitmap, 0, (uint)size, pixels, ref header, 0) == size ? pixels : null;
		}
		finally
		{
			ReleaseDC(IntPtr.Zero, dc);
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IconInfo
	{
		public bool IsIcon;
		public int HotspotX;
		public int HotspotY;
		public IntPtr Mask;
		public IntPtr Color;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapInfoHeader
	{
		public uint Size;
		public int Width;
		public int Height;
		public ushort Planes;
		public ushort BitCount;
		public uint Compression;
		public uint SizeImage;
		public int XPelsPerMeter;
		public int YPelsPerMeter;
		public uint ClrUsed;
		public uint ClrImportant;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BitmapInfo
	{
		public BitmapInfoHeader Header;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
		public uint[] Colors;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrivateExtractIconsW")]
	private static extern uint PrivateExtractIcons(string fileName, int iconIndex, int width, int height,
		IntPtr[] icons, IntPtr iconIds, uint iconCount, uint flags);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DestroyIcon(IntPtr icon);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr window);

	[DllImport("user32.dll")]
	private static extern int ReleaseDC(IntPtr window, IntPtr dc);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits,
		ref BitmapInfo info, uint usage);

	[DllImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool DeleteObject(IntPtr handle);
}
