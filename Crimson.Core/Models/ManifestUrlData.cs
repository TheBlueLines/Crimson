using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Crimson.Models;

public class Element
{
	[JsonPropertyName("appName")]
	public string AppName { get; set; }

	[JsonPropertyName("buildVersion")]
	public string BuildVersion { get; set; }

	[JsonPropertyName("hash")]
	public string Hash { get; set; }

	[JsonPropertyName("labelName")]
	public string LabelName { get; set; }

	[JsonPropertyName("manifests")]
	public List<ManifestList> Manifests { get; set; }

	[JsonPropertyName("useSignedUrl")]
	public bool UseSignedUrl { get; set; }
}

public class ManifestList
{
	[JsonPropertyName("queryParams")]
	public List<QueryParam> QueryParams { get; set; }

	[JsonPropertyName("uri")]
	public string Uri { get; set; }
}

public class QueryParam
{
	[JsonPropertyName("name")]
	public string Name { get; set; }

	[JsonPropertyName("value")]
	public string Value { get; set; }
}

public class ManifestUrlData
{
	[JsonPropertyName("elements")]
	public List<Element> Elements { get; set; }
}

