using System;

namespace Crimson.Models;

public class MirrorStats
{
	public string BaseUrl { get; set; }
	public int FailureCount { get; set; }
	public double AverageSpeed { get; set; }
	public DateTime LastAttempt { get; set; }
}

