using System;

namespace AimSense;

// 搜索起点模型（把"3 档枚举"换成连续函数）。
//
// 旧实现：center = {手臂+手腕 36 | 主要手腕 28 | 主要手臂 45} × √(垫宽/40)，再夹到 18–65。
//   → 全世界只有三个基准值，垫宽只做乘法，game 的点击/跟随类型完全不参与；
//     灵敏度、eDPI、搜索区间全都从这三个数派生，谁来用都是那几个值。
// 新实现：三个**物理/偏好**输入连续合成：
//   wristShare  0=纯手臂 .. 1=纯手腕（对数插值：手臂 52 → 手腕 22 cm/360）
//   trackingFamily 跟随类游戏整体更偏低灵敏度（×1.08）
//   padWidthCm  作为**物理上限**而不是乘数：180° 转身要在垫子内完成 ⇒ cm/360 ÷ 2 ≤ 垫宽
internal static class StartPointModel
{
    public const double ArmCm360 = 52;
    public const double WristCm360 = 22;
    public const double TrackingBias = 1.08;
    public const double MinCm360 = 12;
    public const double MaxCm360 = 85;

    public static double CenterCm(double wristShare, double padWidthCm, bool trackingFamily)
    {
        double wrist = Math.Clamp(wristShare, 0, 1);
        double pad = double.IsFinite(padWidthCm) && padWidthCm is >= 10 and <= 150 ? padWidthCm : 40;
        double baseCm = Math.Exp(Math.Log(ArmCm360) * (1 - wrist) + Math.Log(WristCm360) * wrist);
        if (trackingFamily) baseCm *= TrackingBias;
        double padCap = Math.Max(MinCm360, pad * 2 * 0.9);
        return Math.Clamp(baseCm, MinCm360, Math.Min(MaxCm360, padCap));
    }

    // 搜索区间：以起点为中心按比例展开（rangeShare=0.25 即 ±25%，与旧实现一致）。
    public static (double Low, double High) Range(double center, double rangeShare)
    {
        double range = Math.Clamp(rangeShare, 0.10, 0.60);
        return (Math.Max(5, center / (1 + range)), Math.Min(150, center * (1 + range)));
    }

    public static string DescribeWrist(double wristShare) =>
        $"手腕 {Math.Clamp(wristShare, 0, 1) * 100:0}% / 手臂 {(1 - Math.Clamp(wristShare, 0, 1)) * 100:0}%";
}
