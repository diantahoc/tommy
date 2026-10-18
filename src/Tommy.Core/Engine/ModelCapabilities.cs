namespace Tommy.Core.Engine;

/// <summary>
/// Capabilities detected from the engine (via <c>/v1/models</c>). Vision is a
/// detected capability, not a fixed limit: the per-page rendered image goes
/// straight to the model only when it reports an image input modality,
/// otherwise it is routed to OCR + human preview.
/// </summary>
public sealed record ModelCapabilities(
    string ModelId,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities,
    int MaxContextTokens)
{
    /// <summary>True when the model accepts image input.</summary>
    public bool SupportsVision =>
        InputModalities.Any(m => m.Equals("image", StringComparison.OrdinalIgnoreCase));

    public static ModelCapabilities Unknown { get; } =
        new("unknown", Array.Empty<string>(), Array.Empty<string>(), 0);
}
