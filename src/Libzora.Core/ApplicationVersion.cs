using System.Reflection;

namespace Libzora.Core;

public static class ApplicationVersion
{
    public static string GetDisplayVersion(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+', 2)[0]
            .Trim();
        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        return assembly.GetName().Version?.ToString(3) ?? UiText.Get("未知");
    }
}
