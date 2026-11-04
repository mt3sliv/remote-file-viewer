using System.Security.Claims;
using System.Security.Principal;

namespace RemoteFrame.AspNetCore;

internal static class SessionOwner
{
    internal static string? Identifier(ClaimsPrincipal user)
    {
        var identifier = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrWhiteSpace(identifier)) return identifier;
        if (OperatingSystem.IsWindows() && user.Identity is WindowsIdentity { IsAuthenticated: true } windows)
            return windows.User is { } sid ? "windows:" + sid.Value : null;
        return null;
    }
}
