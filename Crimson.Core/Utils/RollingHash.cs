using System.Linq;
namespace Crimson.Utils;

// Rolling hash Epic uses, it appears to be a variation on CRC-64-ECMA
public static class RollingHash
{
	private const ulong HashPoly = 0xC96C5795D7870F42;
	private static readonly ulong[] HashTable = new ulong[256];

	static RollingHash()
	{
		Initialize();
	}

	private static void Initialize()
	{
		for (var i = 0; i < 256; i++)
		{
			var crc = (ulong)i;
			for (var j = 0; j < 8; j++)
			{
				if ((crc & 1) != 0)
				{
					crc >>= 1;
					crc ^= HashPoly;
				}
				else
				{
					crc >>= 1;
				}
			}
			HashTable[i] = crc;
		}
	}

	public static ulong ComputeHash(byte[] data)
	{
		return data.Aggregate<byte, ulong>(0, (current, b) => ((current << 1) | (current >> 63)) ^ HashTable[b]);
	}
}