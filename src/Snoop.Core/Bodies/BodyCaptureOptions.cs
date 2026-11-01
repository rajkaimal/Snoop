namespace Snoop.Core.Bodies;

/// <summary>Limits for how message bodies are recorded.</summary>
public sealed record BodyCaptureOptions
{
    /// <summary>Bodies up to this size stay in memory; larger ones spill to a temp file.</summary>
    public int MemoryThresholdBytes { get; init; } = 256 * 1024;

    /// <summary>Bytes beyond this are still forwarded but not recorded.</summary>
    public long MaxCapturedBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Folder for spilled bodies. Created on first use.</summary>
    public string TempDirectory { get; init; } = SnoopPaths.DefaultTempDirectory;

    public static BodyCaptureOptions Default { get; } = new();
}
