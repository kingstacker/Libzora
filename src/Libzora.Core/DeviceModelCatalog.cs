namespace Libzora.Core;

/// <summary>
/// 常见的电子书阅读器厂家与型号目录，用于设备型号选择菜单。
/// </summary>
public static class DeviceModelCatalog
{
    public sealed record Vendor(string Name, IReadOnlyList<string> Models);

    public static IReadOnlyList<Vendor> Vendors { get; } =
    [
        new("Kindle",
        [
            "Kindle（基础版）",
            "Kindle Paperwhite",
            "Kindle Paperwhite 5",
            "Kindle Paperwhite 11 代",
            "Kindle Paperwhite 12 代",
            "Kindle Oasis",
            "Kindle Voyage",
            "Kindle Scribe 1 代",
            "Kindle Scribe 2 代",
            "Kindle Scribe 3 代"
        ])
    ];
}
