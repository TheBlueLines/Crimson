using System.Collections.Generic;
using System.Text.Json.Serialization;
using Crimson.Models;

namespace Crimson.Utils;

/// <summary>
/// Compile-time parsers for the records read at every startup, one file per owned game.
/// Read-only on purpose: Asset.Metadata is an object, which generated writers can only handle for known runtime types.
/// </summary>
[JsonSerializable(typeof(Game))]
[JsonSerializable(typeof(Dictionary<string, LocalAppState>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
