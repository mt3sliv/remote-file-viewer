namespace RemoteFrame.AspNetCore;

internal sealed class OfficeApplicationOpenException(string summary, string? diagnostics)
    : IOException("The document worker started, but Office application initialization failed: " + summary +
        (string.IsNullOrWhiteSpace(diagnostics) ? "" : Environment.NewLine + diagnostics));
