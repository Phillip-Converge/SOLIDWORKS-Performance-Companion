namespace SWCompanion.Core.Explain;

/// <summary>A "why?" question the owner can click, shown as a key-cap button.</summary>
/// <param name="Id">Internal name used to look up the answer.</param>
/// <param name="Label">Text on the button.</param>
public sealed record WhyQuestion(string Id, string Label);
