using System;
using System.Collections.Generic;
using System.Linq;

namespace AimSense;

// 逐帧运动学：**逐字移植** ml/export_dataset.py 的 15 项 Kin* 定义。
//
// 为什么要两份实现：Python 那份喂离线训练，C# 这份喂上传包（玩家机器上没有 Python）。
// 两份口径必须完全一致，否则就是训练/服务偏差——所以有 --kin-parity 对拍工具，
// 并且验收标准是"同一条 trace，两边逐项吻合"（计数类相等，均值类 1e-6 相对容差）。
//
// 与 numpy 对齐的三个细节（不能用直觉替代）：
//   1. percentile(95) 默认是**线性插值**：idx = 0.95×(n−1)，在 lo/hi 之间插值；
//   2. 速度用的 dt 会被截断到 ≥1e-3 秒；帧间隔只取 >0 的那些；
//   3. 角路径只统计 yaw/pitch 有限的样本，且 yaw 差分要按 mod 360 包裹后再取绝对值。
internal readonly record struct KinSample(double T, double DX, double DY, string Phase, double Yaw, double Pitch);

internal static class KinematicsAnalysis
{
    public static readonly string[] Keys =
    [
        "KinSamples", "KinScoringShare", "KinMeanFrameDtMs", "KinP95FrameDtMs", "KinMeanAbsDx", "KinMeanAbsDy",
        "KinMaxAbsDx", "KinDirectionReversals", "KinDirectionReversalRate", "KinPathLength", "KinAngularPathRad",
        "KinMeanSpeed", "KinP95Speed", "KinSpeedKurtosis", "KinDurationSeconds",
    ];

    public static Dictionary<string, double> FromSamples(IEnumerable<KinSample> samples, bool requireFinite = true)
    {
        var list = samples.Where(s => !requireFinite || (double.IsFinite(s.T) && double.IsFinite(s.DX) && double.IsFinite(s.DY))).ToArray();
        var result = new Dictionary<string, double> { ["KinSamples"] = list.Length };
        if (list.Length == 0) return result;

        double[] t = list.Select(s => s.T).ToArray();
        double[] dx = list.Select(s => s.DX).ToArray();
        double[] dy = list.Select(s => s.DY).ToArray();

        int scoring = list.Count(s => s.Phase == "Scoring");
        result["KinScoringShare"] = scoring / (double)list.Length;

        if (list.Length >= 2)
        {
            double[] dtMs = new double[t.Length - 1];
            for (int i = 0; i < dtMs.Length; i++) dtMs[i] = (t[i + 1] - t[i]) * 1000.0;
            dtMs = dtMs.Where(x => x > 0).ToArray();
            if (dtMs.Length > 0)
            {
                result["KinMeanFrameDtMs"] = dtMs.Average();
                result["KinP95FrameDtMs"] = Percentile(dtMs, 95);
            }
        }

        result["KinMeanAbsDx"] = dx.Select(Math.Abs).Average();
        result["KinMeanAbsDy"] = dy.Select(Math.Abs).Average();
        result["KinMaxAbsDx"] = dx.Select(Math.Abs).Max();

        // 方向反转：去掉 0 计数后 DX 符号翻转的次数
        var signs = dx.Where(x => x != 0).Select(Math.Sign).ToArray();
        int reversals = 0;
        for (int i = 1; i < signs.Length; i++) if (signs[i] != signs[i - 1]) reversals++;
        result["KinDirectionReversals"] = reversals;

        double span = list.Length >= 2 ? t[^1] - t[0] : 0;
        if (span > 0)
        {
            result["KinDurationSeconds"] = span;
            result["KinDirectionReversalRate"] = reversals / span;
        }

        double[] step = new double[list.Length];
        for (int i = 0; i < step.Length; i++) step[i] = double.Hypot(dx[i], dy[i]);
        result["KinPathLength"] = step.Sum();

        // 角路径：只取 yaw/pitch 有限的样本，yaw 差分按 mod 360 包裹
        int[] finiteIndexes = Enumerable.Range(0, list.Length).Where(i => double.IsFinite(list[i].Yaw) && double.IsFinite(list[i].Pitch)).ToArray();
        if (finiteIndexes.Length >= 2)
        {
            double angular = 0;
            for (int k = 1; k < finiteIndexes.Length; k++)
            {
                KinSample a = list[finiteIndexes[k - 1]], b = list[finiteIndexes[k]];
                double dyaw = Math.Abs(Mod(b.Yaw - a.Yaw + 180.0, 360.0) - 180.0);
                angular += dyaw + Math.Abs(b.Pitch - a.Pitch);
            }
            result["KinAngularPathRad"] = angular * Math.PI / 180.0;
        }

        if (list.Length >= 2)
        {
            var speeds = new List<double>(list.Length - 1);
            for (int i = 0; i < list.Length - 1; i++)
            {
                double dt = t[i + 1] - t[i];
                if (dt <= 1e-3) dt = 1e-3;
                speeds.Add(step[i + 1] / dt);
            }
            if (speeds.Count > 0)
            {
                double[] s = speeds.ToArray();
                double mean = s.Average();
                result["KinMeanSpeed"] = mean;
                result["KinP95Speed"] = Percentile(s, 95);
                double m2 = s.Select(x => (x - mean) * (x - mean)).Average();
                if (m2 > 1e-12)
                {
                    double m4 = s.Select(x => Math.Pow(x - mean, 4)).Average();
                    result["KinSpeedKurtosis"] = m4 / (m2 * m2) - 3.0;   // 超额峰度
                }
                else result["KinSpeedKurtosis"] = 0.0;
            }
        }
        return result;
    }

    // numpy 的 np.mod：结果符号跟随除数（正），与 C# 的 % 不同（踩过这类差异）。
    private static double Mod(double value, double divisor)
    {
        double r = value % divisor;
        return r < 0 ? r + divisor : r;
    }

    // numpy.percentile(x, q) 默认线性插值：idx = q/100 × (n−1)
    public static double Percentile(IReadOnlyList<double> values, double q)
    {
        double[] sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return double.NaN;
        if (sorted.Length == 1) return sorted[0];
        double idx = q / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        double fraction = idx - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * fraction;
    }

    // 上传包与快照统一用这套键（缺项不填，由模型按训练均值填充并回报缺失数）。
    public static Dictionary<string, double> Merge(Dictionary<string, double>? into, IReadOnlyDictionary<string, double>? kinematics)
    {
        into ??= [];
        if (kinematics == null) return into;
        foreach ((string key, double value) in kinematics)
            if (double.IsFinite(value)) into[key] = value;
        return into;
    }
}
