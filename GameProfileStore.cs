using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AimSense;

// 自定义 / 修正过的游戏换算系数（腰射 yaw）。
// 内置只保证 4 个游戏，且常数写死在代码里；这一层让任何人都能：
//   1) 新建一个任意游戏（自己填 yaw 与类型）；
//   2) 用同名条目**覆盖内置游戏**的 yaw（例如官方改过系数，或你自己实测过）；
//   3) 逐项覆盖模式权重（默认权重由"点击系/跟随系"决定）。
// 存在 %LOCALAPPDATA%\AimSense\games.json（只在本机，不上传）。
// 注意：这里**不做任何系数猜测**——自定义游戏必须由用户明确给出 yaw，否则一律拒绝换算。
internal sealed record CustomGame(string Id, double YawDegreesPerCount, bool TrackingFamily,
    double? ClickWeight = null, double? MovingClickWeight = null, double? TrackWeight = null, string? Note = null);

internal static class GameProfileStore
{
    public const string FileName = "games.json";

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, FileName);

    // 仅自检使用：把系数表读写到临时目录，避免自检污染真实配置。
    internal static string? OverrideDirectory;

    // 注意：不要把这个属性命名为 Path，否则会遮蔽 System.IO.Path（踩过一次）。
    private static string StorePath => OverrideDirectory == null ? FilePath : System.IO.Path.Combine(OverrideDirectory, FileName);

    public static string BuiltInSource => GameProfiles.SourceUrl;
    public static string BuiltInVersion => GameProfiles.PresetVersion;

    public static IReadOnlyList<CustomGame> Customs()
    {
        try
        {
            if (!File.Exists(StorePath)) return [];
            var list = JsonSerializer.Deserialize<List<CustomGame>>(File.ReadAllText(StorePath)) ?? [];
            return list.Where(x => x.Id.Length > 0 && double.IsFinite(x.YawDegreesPerCount) && x.YawDegreesPerCount > 0).ToArray();
        }
        catch { return []; }
    }

    public static void Save(CustomGame game)
    {
        var list = Customs().Where(x => !string.Equals(x.Id, game.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(game);
        Write(list);
    }

    public static bool Delete(string id)
    {
        var list = Customs().ToList();
        int removed = list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Write(list);
        return removed > 0;
    }

    private static void Write(IReadOnlyList<CustomGame> list)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    // 内置 4 个 + 自定义：同名自定义**覆盖**内置的 yaw / 类型 / 权重。
    public static IReadOnlyList<GameProfile> All()
    {
        var result = new List<GameProfile>();
        foreach (string id in GameProfiles.BuiltInIds)
        {
            CustomGame? custom = Customs().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            GameProfile builtin = GameProfiles.Get(id);
            result.Add(custom == null ? builtin : builtin with
            {
                YawDegreesPerCount = custom.YawDegreesPerCount,
                TrackingFamily = custom.TrackingFamily,
                ClickWeight = custom.ClickWeight,
                MovingClickWeight = custom.MovingClickWeight,
                TrackWeight = custom.TrackWeight,
                Source = "用户自定义",
                SourceVersion = "custom"
            });
        }
        foreach (CustomGame custom in Customs().Where(x => !GameProfiles.BuiltInIds.Any(b => string.Equals(b, x.Id, StringComparison.OrdinalIgnoreCase))))
            result.Add(new(custom.Id, custom.YawDegreesPerCount, custom.TrackingFamily, "用户自定义", "custom",
                custom.ClickWeight, custom.MovingClickWeight, custom.TrackWeight));
        return result;
    }

    // 找不到就返回 null（**不抛**），由调用方决定是拒绝还是回退到内置表。
    public static GameProfile? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string wanted = id.Trim();
        GameProfile? found = All().FirstOrDefault(x => string.Equals(x.Id, wanted, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;
        return GameProfiles.BuiltInIds.Any(b => string.Equals(b, wanted, StringComparison.OrdinalIgnoreCase)) ? GameProfiles.Get(wanted) : null;
    }

    // 由"已知游戏灵敏度 + 它对应的 cm/360"反解 yaw：yaw = 360×2.54 / (DPI × cm/360 × 灵敏度)
    public static double? YawFromKnownPair(double dpi, double sensitivity, double cm360)
    {
        if (!double.IsFinite(dpi) || !double.IsFinite(sensitivity) || !double.IsFinite(cm360)) return null;
        if (dpi <= 0 || sensitivity <= 0 || cm360 <= 0) return null;
        double yaw = 360 * 2.54 / (dpi * cm360 * sensitivity);
        return double.IsFinite(yaw) && yaw is > 1e-6 and < 1 ? yaw : null;
    }

    public static string FormatYaw(double yaw) => yaw.ToString("0.000000", CultureInfo.InvariantCulture);
}
