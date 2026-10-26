using System.Reflection;

namespace Bag;

internal static class BagVersion
{
    public static string Current { get; } = typeof(BagVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
