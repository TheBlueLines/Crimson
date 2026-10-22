using System.Collections.Generic;

namespace Crimson.Repository;

public sealed class GetManifestUrlData
{
	public List<string> BaseUrls { get; set; }
	public List<string> ManifestUrls { get; set; }
	public string ManifestHash { get; set; }
}
