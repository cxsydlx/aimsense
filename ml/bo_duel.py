"""
布尔对战：现有 SensitivitySearch（对数三等分 + 配对比较） vs 贝叶斯优化（1 维 GP + EI / Thompson）

目的：验证《可行性评估》路线 A 里那句"同类问题通常省 30–50% 试次"在我们**自己的决策规则**下是否成立。
方法：在合成的一维噪声损失面上，用两种策略各自搜索，比较
  (a) 达到"自身置信判据"所需块数；
  (b) 停止时的答案误差（是否命中真值附近）；
  (c) 与判据无关的指标：第一次给出"距真值 ≤1cm 的推荐"所需块数。
只用 numpy。诚实声明见文末。
"""
import math, random, statistics
import numpy as np

LOG = math.log
# 参数取自真实会话的搜索区间（28.8–45 cm/360）与实测损失跨度（约 13–22 分）
LOW, HIGH = 28.8, 45.0
CURVATURE = 65.0      # 损失面曲率：在 ln 尺度上二次
NOISE = 4.0           # 单块损失的噪声（分）
BLOCKS_PER_ROUND = 3  # 现有策略：每轮 3 个候选各 1 块


def true_loss(cm, best_cm):
    return 10.0 + CURVATURE * (LOG(cm) - LOG(best_cm)) ** 2


def observe(cm, best_cm, rng):
    return true_loss(cm, best_cm) + rng.gauss(0, NOISE)


# ---------------------------------------------------------------- 现有策略
def run_ternary(best_cm, rng, max_rounds=40, k=1.0):
    low, high = LOW, HIGH
    blocks = 0
    per_candidate = {}          # cm -> [losses]
    first_good = None
    for _ in range(max_rounds):
        cands = [low, math.sqrt(low * high), high]
        for cm in cands:
            per_candidate.setdefault(round(cm, 4), []).append(observe(cm, best_cm, rng))
            blocks += 1
        stats = []
        for cm, losses in per_candidate.items():
            mean = statistics.fmean(losses)
            se = (statistics.stdev(losses) / math.sqrt(len(losses))) if len(losses) > 1 else float("inf")
            stats.append((cm, mean, se))
        stats.sort(key=lambda t: t[1])
        gap = stats[1][1] - stats[0][1]
        tol = k * max(2.0, math.sqrt(min(stats[0][2], 1e9) ** 2 + min(stats[1][2], 1e9) ** 2)) if all(
            math.isfinite(s[2]) for s in stats[:2]) else float("inf")
        best = stats[0][0]
        if first_good is None and abs(LOG(best) - LOG(best_cm)) <= LOG(1 + 1.0 / best_cm):
            first_good = blocks
        if gap > tol:
            return blocks, best, first_good
        half = LOG(high / low) / 4
        low = max(5.0, best * math.exp(-half))
        high = min(150.0, best * math.exp(half))
    return blocks, best, first_good


# ---------------------------------------------------------------- 贝叶斯优化
def rbf(a, b, length):
    return np.exp(-0.5 * ((a[:, None] - b[None, :]) / length) ** 2)


def gp_fit_predict(xs, ys, noise_var, length, grid):
    """返回 grid 上的后验均值与标准差；核超参固定，噪声按 1/n 聚合。"""
    k = rbf(xs, xs, length) + np.diag(np.full(len(xs), noise_var))
    k_s = rbf(xs, grid, length)
    k_ss = np.diag(rbf(grid, grid, length))
    try:
        chol = np.linalg.cholesky(k)
        alpha = np.linalg.solve(chol.T, np.linalg.solve(chol, ys))
        v = np.linalg.solve(chol, k_s)
    except np.linalg.LinAlgError:
        jitter = np.diag(np.full(len(xs), 1e-6))
        chol = np.linalg.cholesky(k + jitter)
        alpha = np.linalg.solve(chol.T, np.linalg.solve(chol, ys))
        v = np.linalg.solve(chol, k_s)
    mean = k_s.T @ alpha
    var = np.maximum(k_ss - np.sum(v ** 2, axis=0), 1e-9)
    return mean, np.sqrt(var)


def run_bo(best_cm, rng, strategy="ei", max_blocks=80):
    xs = [LOG(LOW), LOG(math.sqrt(LOW * HIGH)), LOG(HIGH)]
    ys = [observe(math.exp(x), best_cm, rng) for x in xs]
    counts = [1, 1, 1]
    blocks = 3
    grid = np.linspace(LOG(LOW), LOG(HIGH), 90)
    first_good = None
    length, noise_var = 0.12, (NOISE ** 2)
    while blocks < max_blocks:
        xa = np.array(xs); ya = np.array(ys)
        mean, sd = gp_fit_predict(xa, ya, noise_var, length, grid)
        bi = int(np.argmin(mean))
        best_x, best_mean = grid[bi], mean[bi]
        # 置信判据（与现有策略同构）：最优与次优候选的后验差距超过不确定性
        order = np.argsort(mean)
        runner = mean[order[1]]
        confident = (runner - best_mean) > 2.0 * sd[bi]
        guess = math.exp(best_x)
        if first_good is None and abs(LOG(guess) - LOG(best_cm)) <= LOG(1 + 1.0 / guess):
            first_good = blocks
        if confident:
            return blocks, guess, first_good
        if strategy == "ei":
            improve = (best_mean - mean)
            z = improve / sd
            from math import erf, sqrt
            phi = np.exp(-0.5 * z ** 2) / sqrt(2 * math.pi)
            cdf = 0.5 * (1 + np.vectorize(erf)(z / sqrt(2)))
            score = improve * cdf + sd * phi
        else:  # Thompson
            sample = mean + sd * np.random.default_rng(rng.randrange(1 << 30)).normal(size=len(grid))
            score = -sample
        nxt = grid[int(np.argmax(score))]
        cm = math.exp(nxt)
        xs.append(nxt); ys.append(observe(cm, best_cm, rng)); counts.append(1)
        blocks += 1
    return blocks, math.exp(grid[int(np.argmin(mean))]), first_good


def run_racing(best_cm, rng, drop_worst=True, shrink=False, max_blocks=80):
    """Racing（逐轮淘汰）：每轮只给当前最好的两个候选各加 1 块，明确差的那个不再投入。
    瓶颈是噪声时，把块数集中到前两名比"三个候选平均复制"更省块——这是与现有策略最直接的对照。"""
    low, high = LOW, HIGH
    cands = [low, math.sqrt(low * high), high]
    data = {round(c, 4): [] for c in cands}
    blocks = 0
    first_good = None
    while blocks < max_blocks:
        # 首轮三个候选各 1 块；之后只投前两名
        targets = list(data.keys()) if blocks == 0 else _top_two(data, center=math.sqrt(low * high))
        for cm in targets:
            data.setdefault(cm, []).append(observe(cm, best_cm, rng))
            blocks += 1
        stats = _stats(data)
        best, second = stats[0], stats[1]
        gap = second[1] - best[1]
        tol = max(2.0, math.sqrt(best[2] ** 2 + second[2] ** 2))
        if first_good is None and abs(LOG(best[0]) - LOG(best_cm)) <= LOG(1 + 1.0 / best[0]):
            first_good = blocks
        if gap > tol:
            return blocks, best[0], first_good
        if shrink:
            half = LOG(high / low) / 6
            low = max(5.0, best[0] * math.exp(-half))
            high = min(150.0, best[0] * math.exp(half))
            data = {round(c, 4): data.get(round(c, 4), []) for c in [low, math.sqrt(low * high), high]}
    return blocks, _stats(data)[0][0], first_good


def _stats(data):
    out = []
    for cm, losses in data.items():
        if not losses:
            continue
        mean = statistics.fmean(losses)
        se = (statistics.stdev(losses) / math.sqrt(len(losses))) if len(losses) > 1 else float("inf")
        out.append((cm, mean, se))
    out.sort(key=lambda t: t[1])
    return out


def _top_two(data, center):
    stats = _stats(data)
    if len(stats) < 2:
        return [s[0] for s in stats] or [center]
    return [stats[0][0], stats[1][0]]


def main():
    rng = random.Random(20261012)
    n = 120
    names = ("ternary", "ei", "thompson", "racing")
    res = {k: [] for k in names}
    acc = {k: 0 for k in names}
    first = {k: [] for k in names}
    for _ in range(n):
        best = rng.uniform(30.0, 42.0)
        for name in names:
            if name == "ternary":
                blocks, guess, fg = run_ternary(best, random.Random(rng.randrange(1 << 30)))
            elif name == "racing":
                blocks, guess, fg = run_racing(best, random.Random(rng.randrange(1 << 30)))
            else:
                blocks, guess, fg = run_bo(best, random.Random(rng.randrange(1 << 30)), strategy=name)
            res[name].append(blocks)
            acc[name] += abs(LOG(guess) - LOG(best)) <= LOG(1 + 1.5 / guess)
            if fg:
                first[name].append(fg)

    labels = {"ternary": "现有三等分", "ei": "BO·EI", "thompson": "BO·Thompson",
              "racing": "Racing(淘汰)"}
    print("实验：120 个随机真值（cm* ∈ 30–42，损失曲率 65，单块噪声 σ=4；上限 80 块）")
    print()
    print(f"{'策略':<16}{'中位块数':>9}{'均值块数':>9}{'P90':>7}{'命中(±1.5cm)':>14}{'首次接近中位':>13}")
    for name in names:
        v = sorted(res[name]); f = sorted(first[name]) if first[name] else [float('nan')]
        print(f"{labels[name]:<16}{statistics.median(v):>9.1f}{statistics.fmean(v):>9.1f}{v[int(.9*(len(v)-1))]:>7}"
              f"{acc[name]/n*100:>13.0f}%{statistics.median(f):>13.1f}")
    print()
    base = statistics.median(res["ternary"])
    for name in names[1:]:
        d = (base - statistics.median(res[name])) / base * 100
        print(f"{labels[name]} 相对现有策略：中位块数 {d:+.0f}%，命中率 {acc[name]/n*100 - acc['ternary']/n*100:+.0f} 个百分点")
    print()
    print("诚实声明：合成损失面上的对照（真值对两种策略都未知，噪声同分布）。")
    print("真实 trace 只有 9 个不同 cm/360、10 个候选，做不了统计意义上的实战对战。")



def k_sweep():
    """判据强度扫描：tol = k × max(2, √(SE1²+SE2²))。k=1 就是现有实现。
    看"正确率 vs 块数"的曲线，用来选一个可接受的平衡点，而不是凭感觉调常数。"""
    rng = random.Random(20261012)
    n = 200
    print("判据强度 k 扫描（现有三等分策略，200 次随机真值，上限 40 块）")
    print(f"{'k':>5}{'中位块数':>10}{'均值块数':>10}{'命中(±1.5cm)':>14}{'命中(±2.5cm)':>14}")
    for k in (1.0, 1.5, 2.0, 2.5, 3.0):
        blocks, hit15, hit25 = [], 0, 0
        for _ in range(n):
            best = rng.uniform(30.0, 42.0)
            b, guess, _ = run_ternary(best, random.Random(rng.randrange(1 << 30)), k=k)
            blocks.append(b)
            hit15 += abs(LOG(guess) - LOG(best)) <= LOG(1 + 1.5 / guess)
            hit25 += abs(LOG(guess) - LOG(best)) <= LOG(1 + 2.5 / guess)
        v = sorted(blocks)
        print(f"{k:>5.1f}{statistics.median(v):>10.1f}{statistics.fmean(v):>10.1f}{hit15/n*100:>13.0f}%{hit25/n*100:>13.0f}%")

if __name__ == "__main__":
    k_sweep()





