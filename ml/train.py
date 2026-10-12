#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AimSense 离线训练管线（纯 numpy，无 sklearn / 无 torch）。

目标 1（默认，--target feel）：以候选档位级特征预测主观手感标签 Feel >= 7（二分类）。
目标 2（--target preference）：用成对偏好标签做 pairwise logistic（Bradley-Terry 式）。

硬性口径：
  * 必须按 session 分组交叉验证（手写 GroupKFold），绝不能把同一 session 的候选分到两边；
  * 样本量 < 200 或 session 数 < 10 时，报告里必须写明"数据不足，仅管线验证，不可用于结论"，
    且不允许出现任何"模型有效"的说法；
  * 没有标签时正常退出（退出码 0）并说明原因，不崩溃。

用法：
    python train.py                       # 真实数据 + feel 目标
    python train.py --target preference   # 成对偏好
    python train.py --self-test           # 合成数据端到端跑通（临时目录）
"""

from __future__ import annotations

import argparse
import collections
import datetime as _dt
import json
import math
import os
import sys
import tempfile

import numpy as np

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:  # pragma: no cover
    pass


# --------------------------------------------------------------------------------------
# 常量
# --------------------------------------------------------------------------------------

MODEL_VERSION = "preference-logreg-v1"
FEATURE_VERSION = "candidate-features-1"
LABEL_SCHEMA_VERSION = 1
DEFAULT_SCORE_VERSION = "angular-block-loss-v3"

# 模型 JSON 的 "Game" 字段取值：某个游戏 Id（只对该游戏预测），
# 或 "ALL"（C# 侧不做游戏闸门；仅当用户显式要求跨游戏时才允许）。
GAME_ALL = "ALL"


def game_slug(game: str) -> str:
    """把游戏 Id 变成安全的文件名片段。"""
    cleaned = "".join(ch if ch.isalnum() else "-" for ch in str(game).strip().lower())
    cleaned = "-".join(part for part in cleaned.split("-") if part)
    return cleaned or "game"


def local_appdata() -> str:
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        base = os.path.join(os.path.expanduser("~"), "AppData", "Local")
    return base


def missing_rate_table(matrices: list[np.ndarray]) -> dict[str, float]:
    """各契约列在训练数据里的缺失率（用于写进模型 Notes 的"哪些列缺失最多"）。"""
    usable = [m for m in matrices if m.size > 0]
    if not usable:
        return {}
    stacked = np.vstack(usable)
    return {FEATURES[j]: float(np.mean(~np.isfinite(stacked[:, j]))) for j in range(len(FEATURES))}


def publish_model(model_path: str, eligible: bool, reason: str) -> tuple[bool, str]:
    """把训练好的模型复制到 C# 侧读取路径 %LOCALAPPDATA%\\AimSense\\model\\preference-v1.json。

    只复制这一个文件；不达标（无真实标签 / 合成数据 / 目标不匹配）时坚决不写，
    避免用没有真实依据的模型污染真实推理。
    """
    target_path = os.path.join(local_appdata(), "AimSense", "model", "preference-v1.json")
    if not eligible:
        return False, f"未复制到 C# 读取路径（{reason}）。目标：{target_path}"
    try:
        os.makedirs(os.path.dirname(target_path), exist_ok=True)
        with open(model_path, "r", encoding="utf-8") as fh:
            payload = fh.read()
        with open(target_path, "w", encoding="utf-8") as fh:
            fh.write(payload)
        return True, f"已复制到 C# 读取路径：{target_path}"
    except OSError as exc:
        return False, f"复制到 {target_path} 失败（{exc}）；C# 侧会继续显示“未安装离线模型”"

# 候选级特征（candidate-features-1）。
# ★ 这 26 个名字必须与 C# 侧 AimSense\MainWindow.xaml.cs -> CandidateFeatures() 及
#   AimSense\OfflineModel.cs 的 FeatureVersion 严格一致，否则影子模式推理取不到值。
#   顺序即模型 JSON 中 Features / Mean / Scale / Weights 的顺序。
#   注意：x_ 前缀的离线探索特征（运动学等）绝不进模型——C# 侧提供不了。
FEATURES: list[str] = [
    "blocks",             # 该档位的块数（含无效块；不含 Pilot）
    "validBlocks",        # 有效块数
    "meanScore",          # 有效块表现分均值
    "maxScore",           # 有效块表现分最大
    "minScore",           # 有效块表现分最小
    "scoreStd",           # 有效块表现分标准差（样本标准差，单块为 0）
    "clickScore",         # 点击模式平均分（该模式无有效块则缺失）
    "trackScore",         # 跟随模式平均分（同上）
    "movingClickScore",   # 移动点击模式平均分（同上）
    "hitRate",            # 点击类块命中比例
    "timeoutRate",        # 点击类块超时比例
    "missRate",           # 点击类块错点比例
    "trackingShare",      # 跟随类块平均贴靶时间比例
    "meanAngularError",   # 跟随类块平均角误差（单位：头部角半径）
    "meanShotSeconds",    # 点击类块平均命中耗时（秒）
    "overshootsPerBlock", # 每有效块平均过冲次数
    "correctionsPerBlock",# 每有效块平均回拉次数
    "meanOvershootRadii", # 平均过冲幅度（单位：头部角半径）
    "meanTurnResponse",   # 平均换向响应时间（秒）
    "meanRecovery",       # 平均离靶恢复时间（秒）
    "cm360",              # 档位
    "logCm360",           # ln(档位)
    "centerRatio",        # 档位 / 会话起始档位 CenterCm
    "dpi",                # 鼠标 DPI
    "sensitivity",        # 游戏内灵敏度
    "edpi",               # eDPI = DPI × sensitivity
]

MIN_SAMPLES = 200
MIN_GROUPS = 10

INSUFFICIENT_BANNER = "数据不足，仅管线验证，不可用于结论"


# --------------------------------------------------------------------------------------
# 工具
# --------------------------------------------------------------------------------------

def read_jsonl(path: str) -> list[dict]:
    rows: list[dict] = []
    if not path or not os.path.isfile(path):
        return rows
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                continue
            if isinstance(obj, dict):
                rows.append(obj)
    return rows


def load_manifest(out_dir: str) -> dict:
    path = os.path.join(out_dir, "manifest.json")
    if not os.path.isfile(path):
        return {}
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            obj = json.load(fh)
        return obj if isinstance(obj, dict) else {}
    except Exception:
        return {}


def as_float(value) -> float:
    if isinstance(value, bool) or value is None:
        return float("nan")
    if isinstance(value, (int, float)):
        f = float(value)
        return f if math.isfinite(f) else float("nan")
    return float("nan")


def candidate_feature_vector(row: dict) -> dict[str, float]:
    """把一条候选记录变成 {契约特征名: 值}（缺值为 nan）。

    契约特征在 export_dataset.py 里已经按 C# CandidateFeatures() 的口径算好并直接命名，
    这里只做取数，绝不做重命名或二次派生——否则训练/服务两侧口径会漂移。
    """
    return {name: as_float(row.get(name)) for name in FEATURES}


def to_matrix(rows: list[dict]) -> np.ndarray:
    X = np.empty((len(rows), len(FEATURES)), dtype=float)
    for i, row in enumerate(rows):
        vec = candidate_feature_vector(row)
        for j, name in enumerate(FEATURES):
            X[i, j] = vec.get(name, float("nan"))
    return X


# --------------------------------------------------------------------------------------
# 指标
# --------------------------------------------------------------------------------------

def auc_score(y: np.ndarray, score: np.ndarray) -> float | None:
    """Mann-Whitney U 形式的 AUC，正确处理并列（平均秩）。单类别时返回 None。"""
    y = np.asarray(y, dtype=float)
    score = np.asarray(score, dtype=float)
    pos = y == 1
    neg = y == 0
    n_pos = int(pos.sum())
    n_neg = int(neg.sum())
    if n_pos == 0 or n_neg == 0:
        return None
    order = np.argsort(score, kind="mergesort")
    sorted_score = score[order]
    ranks = np.empty(len(score), dtype=float)
    i = 0
    while i < len(sorted_score):
        j = i
        while j + 1 < len(sorted_score) and sorted_score[j + 1] == sorted_score[i]:
            j += 1
        ranks[order[i:j + 1]] = (i + j) / 2.0 + 1.0
        i = j + 1
    rank_sum_pos = float(ranks[pos].sum())
    return (rank_sum_pos - n_pos * (n_pos + 1) / 2.0) / (n_pos * n_neg)


def confusion(y: np.ndarray, pred: np.ndarray) -> tuple[int, int, int, int]:
    y = np.asarray(y).astype(int)
    pred = np.asarray(pred).astype(int)
    tn = int(np.sum((y == 0) & (pred == 0)))
    fp = int(np.sum((y == 0) & (pred == 1)))
    fn = int(np.sum((y == 1) & (pred == 0)))
    tp = int(np.sum((y == 1) & (pred == 1)))
    return tn, fp, fn, tp


# --------------------------------------------------------------------------------------
# 手写 GroupKFold
# --------------------------------------------------------------------------------------

def group_kfold(groups: list[str], k: int, seed: int) -> list[list[int]]:
    """按 session 分组切分。组内样本绝不跨折；同时按组大小做均衡分配。

    返回长度为 k 的列表，每项是该折的"验证集样本下标"。
    """
    uniq = sorted(set(groups))
    rng = np.random.default_rng(seed)
    order = list(uniq)
    rng.shuffle(order)
    # 大组优先，轮转分配到当前最小的折，避免某一折特别大
    sizes = collections.Counter(groups)
    order.sort(key=lambda g: -sizes[g])
    folds: list[list[int]] = [[] for _ in range(k)]
    load = [0] * k
    for g in order:
        target = int(np.argmin(np.asarray(load, dtype=float)))
        load[target] += sizes[g]
        folds[target].append(g)
    index_of_group = {g: i for i, g in enumerate(uniq)}
    group_pos: list[list[int]] = [[] for _ in range(len(uniq))]
    for i, g in enumerate(groups):
        group_pos[index_of_group[g]].append(i)
    result: list[list[int]] = []
    for fold_groups in folds:
        idx: list[int] = []
        for g in fold_groups:
            idx.extend(group_pos[index_of_group[g]])
        result.append(sorted(idx))
    return result


# --------------------------------------------------------------------------------------
# 逻辑回归 / pairwise 逻辑回归（纯 numpy 手写梯度下降 + L2）
# --------------------------------------------------------------------------------------

def fit_logreg(X: np.ndarray, y: np.ndarray, l2: float = 1.0,
               lr: float = 0.5, epochs: int = 3000) -> tuple[np.ndarray, float]:
    n, d = X.shape
    w = np.zeros(d, dtype=float)
    b = 0.0
    if n == 0:
        return w, b
    y = y.astype(float)
    for _ in range(epochs):
        z = X @ w + b
        p = 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))
        err = p - y
        gw = X.T @ err / n + l2 * w / n
        gb = float(err.mean())
        w -= lr * gw
        b -= lr * gb
    return w, b


def fit_pairwise(X_b: np.ndarray, X_w: np.ndarray, l2: float = 1.0,
                 lr: float = 0.5, epochs: int = 3000) -> tuple[np.ndarray, float]:
    """Bradley-Terry 式成对逻辑回归：P(更好 > 更差) = sigmoid(s_b - s_w)。"""
    n, d = X_b.shape
    w = np.zeros(d, dtype=float)
    b = 0.0
    if n == 0:
        return w, b
    diff = X_b - X_w
    for _ in range(epochs):
        z = diff @ w  # bias 在差分里自动抵消
        p = 1.0 / (1.0 + np.exp(-np.clip(z, -30, 30)))
        g = (p - 1.0)
        gw = diff.T @ g / n + l2 * w / n
        w -= lr * gw
    return w, b


def standardize(train: np.ndarray, other: list[np.ndarray]) -> tuple[list[np.ndarray], np.ndarray, np.ndarray, list[str]]:
    """用训练折统计量做"均值填充 + 标准化"。

    返回 (变换后的若干矩阵, Mean, Scale, 全缺失特征名)。
    """
    mean = np.nanmean(np.where(np.isfinite(train), train, np.nan), axis=0)
    all_missing = [FEATURES[j] for j in range(train.shape[1]) if not np.isfinite(mean[j])]
    mean = np.where(np.isfinite(mean), mean, 0.0)

    filled = np.where(np.isfinite(train), train, mean)
    scale = filled.std(axis=0, ddof=0)
    scale = np.where((scale > 1e-9) & np.isfinite(scale), scale, 1.0)

    out = []
    for m in [train] + list(other):
        f = np.where(np.isfinite(m), m, mean)
        out.append((f - mean) / scale)
    return out, mean, scale, all_missing


# --------------------------------------------------------------------------------------
# 数据集装载
# --------------------------------------------------------------------------------------

def discover_games(out_dir: str, target: str) -> tuple[list[str], list[str]]:
    """返回 (有标签的游戏列表, 全部候选涉及的游戏列表)，均已排序去重。"""
    candidates = read_jsonl(os.path.join(out_dir, "candidates.jsonl"))
    all_games = sorted({str(c.get("Game") or "").strip() for c in candidates if str(c.get("Game") or "").strip()})

    labeled: set[str] = set()
    if target == "feel":
        for c in candidates:
            if c.get("LabelFeel") is None:
                continue
            g = str(c.get("Game") or "").strip()
            if g:
                labeled.add(g)
    else:
        by_key = {c.get("CandidateKey"): c for c in candidates}
        for p in read_jsonl(os.path.join(out_dir, "preference_pairs.jsonl")):
            b = by_key.get(p.get("BetterCandidateKey"))
            w = by_key.get(p.get("WorseCandidateKey"))
            if b is None or w is None:
                continue
            g = str(p.get("Game") or b.get("Game") or "").strip()
            if g:
                labeled.add(g)
    return sorted(labeled), all_games


def load_feel_dataset(out_dir: str, positive_threshold: int = 7, game: str = GAME_ALL
                      ) -> tuple[list[dict], np.ndarray, np.ndarray, np.ndarray, dict]:
    """返回 (原始候选行, X, y, groups, 统计信息)。只保留有 Feel 标签的候选。"""
    candidates = read_jsonl(os.path.join(out_dir, "candidates.jsonl"))
    if game != GAME_ALL:
        wanted = game.strip().lower()
        candidates = [c for c in candidates if str(c.get("Game") or "").strip().lower() == wanted]
    stats = {
        "candidates_total": len(candidates),
        "candidates_without_feel": 0,
        "candidates_with_feel": 0,
        "positive": 0,
        "negative": 0,
        "feel_value_counts": collections.Counter(),
        "unmatched_feel_rows": [],
    }
    rows: list[dict] = []
    y_list: list[int] = []
    for row in candidates:
        feel = row.get("LabelFeel")
        if feel is None or isinstance(feel, bool):
            stats["candidates_without_feel"] += 1
            continue
        try:
            feel_int = int(feel)
        except Exception:
            stats["candidates_without_feel"] += 1
            continue
        stats["candidates_with_feel"] += 1
        stats["feel_value_counts"][feel_int] += 1
        label = 1 if feel_int >= positive_threshold else 0
        stats["positive" if label else "negative"] += 1
        rows.append(row)
        y_list.append(label)

    if not rows:
        return [], np.zeros((0, len(FEATURES))), np.zeros(0, dtype=float), np.zeros(0, dtype=object), stats

    X = to_matrix(rows)
    y = np.asarray(y_list, dtype=float)
    groups = np.asarray([str(r.get("SessionId") or "") for r in rows], dtype=object)
    stats["feel_value_counts"] = dict(sorted(stats["feel_value_counts"].items()))
    return rows, X, y, groups, stats


def load_preference_dataset(out_dir: str, game: str = GAME_ALL
                            ) -> tuple[list[dict], np.ndarray, np.ndarray, np.ndarray, dict]:
    """返回 (原始对行, Xb, Xw, groups, 统计信息)。只保留两侧候选都存在的偏好对。"""
    pairs = read_jsonl(os.path.join(out_dir, "preference_pairs.jsonl"))
    by_key = {c.get("CandidateKey"): c for c in read_jsonl(os.path.join(out_dir, "candidates.jsonl"))}
    stats = {
        "pairs_total": len(pairs),
        "pairs_unusable": 0,
        "pairs_usable": 0,
        "pairs_other_game": 0,
        "unusable_examples": [],
    }
    rows: list[dict] = []
    wanted = None if game == GAME_ALL else game.strip().lower()
    for p in pairs:
        b = by_key.get(p.get("BetterCandidateKey"))
        w = by_key.get(p.get("WorseCandidateKey"))
        if b is None or w is None:
            stats["pairs_unusable"] += 1
            if len(stats["unusable_examples"]) < 5:
                stats["unusable_examples"].append(
                    f"{p.get('LabelId')}：更好 {p.get('BetterCm360')} / 更差 {p.get('WorseCm360')} 缺少候选特征")
            continue
        pair_game = str(p.get("Game") or b.get("Game") or "").strip()
        if wanted is not None and pair_game.lower() != wanted:
            stats["pairs_other_game"] += 1
            continue
        p = dict(p)
        p["_better"] = b
        p["_worse"] = w
        p["_game"] = pair_game
        rows.append(p)
        stats["pairs_usable"] += 1

    if not rows:
        empty = np.zeros((0, len(FEATURES)))
        return [], empty, empty, np.zeros(0, dtype=object), stats

    Xb = to_matrix([r["_better"] for r in rows])
    Xw = to_matrix([r["_worse"] for r in rows])
    groups = np.asarray([str(r.get("SessionId") or "") for r in rows], dtype=object)
    return rows, Xb, Xw, groups, stats


# --------------------------------------------------------------------------------------
# 训练 + 交叉验证
# --------------------------------------------------------------------------------------

def run_cv(X: np.ndarray, y: np.ndarray, groups: np.ndarray, k: int, seed: int,
           l2: float, lr: float, epochs: int, pairwise_X: tuple[np.ndarray, np.ndarray] | None = None
           ) -> tuple[list[dict], np.ndarray, dict]:
    """按 session 分组 k 折。pairwise_X 不为空时走成对逻辑回归。"""
    n = len(groups)
    uniq = sorted(set(str(g) for g in groups))
    k = max(2, min(k, len(uniq))) if len(uniq) >= 2 else 1
    folds = group_kfold([str(g) for g in groups], k, seed) if k >= 2 else [list(range(n))]

    oof = np.full(n, np.nan, dtype=float)
    fold_reports: list[dict] = []

    for fi, val_idx in enumerate(folds):
        val_idx_arr = np.asarray(val_idx, dtype=int)
        mask = np.ones(n, dtype=bool)
        mask[val_idx_arr] = False
        tr_idx = np.flatnonzero(mask)
        if len(tr_idx) == 0 or len(val_idx_arr) == 0:
            continue

        if pairwise_X is None:
            Xtr, Xva = standardize(X[tr_idx], [X[val_idx_arr]])[0]
            w, b = fit_logreg(Xtr, y[tr_idx], l2=l2, lr=lr, epochs=epochs)
            score = Xva @ w + b
            oof[val_idx_arr] = score
            prob = 1.0 / (1.0 + np.exp(-np.clip(score, -30, 30)))
            pred = (prob >= 0.5).astype(int)
            yva = y[val_idx_arr]
            fold_auc = auc_score(yva, score)
            acc = float((pred == yva).mean()) if len(yva) else None
            tn, fp, fn, tp = confusion(yva, pred)
            fold_reports.append({
                "fold": fi + 1, "train": int(len(tr_idx)), "val": int(len(val_idx_arr)),
                "groups_train": len({str(g) for g in groups[tr_idx]}),
                "groups_val": len({str(g) for g in groups[val_idx_arr]}),
                "auc": fold_auc, "accuracy": acc,
                "tn": tn, "fp": fp, "fn": fn, "tp": tp,
                "val_groups": sorted({str(g) for g in groups[val_idx_arr]}),
            })
        else:
            Xb, Xw = pairwise_X
            n_tr = len(tr_idx)
            (tr_std, va_b, va_w), _, _, _ = standardize(
                np.vstack([Xb[tr_idx], Xw[tr_idx]]), [Xb[val_idx_arr], Xw[val_idx_arr]])
            w, b = fit_pairwise(tr_std[:n_tr], tr_std[n_tr:], l2=l2, lr=lr, epochs=epochs)
            z = va_b @ w + b - (va_w @ w + b)
            oof[val_idx_arr] = z
            correct = float((z > 0).mean()) if len(z) else None
            fold_reports.append({
                "fold": fi + 1, "train": int(len(tr_idx)), "val": int(len(val_idx_arr)),
                "groups_train": len({str(g) for g in groups[tr_idx]}),
                "groups_val": len({str(g) for g in groups[val_idx_arr]}),
                "pairwise_accuracy": correct,
                # 成对排序准确率在数值上等价于"更好/更差"二分类的 AUC
                "auc": correct,
                "accuracy": correct,
                "val_groups": sorted({str(g) for g in groups[val_idx_arr]}),
            })

    summary = {"folds": fold_reports, "k": k, "groups_total": len(uniq)}
    if pairwise_X is None:
        valid = np.isfinite(oof)
        summary["oof_auc"] = auc_score(y[valid], oof[valid]) if valid.any() else None
        if valid.any():
            pred = (1.0 / (1.0 + np.exp(-np.clip(oof[valid], -30, 30))) >= 0.5).astype(int)
            tn, fp, fn, tp = confusion(y[valid], pred)
            summary["oof_accuracy"] = float((pred == y[valid]).mean())
            summary["oof_confusion"] = (tn, fp, fn, tp)
        else:
            summary["oof_accuracy"] = None
            summary["oof_confusion"] = None
    else:
        valid = np.isfinite(oof)
        summary["oof_pairwise_accuracy"] = float((oof[valid] > 0).mean()) if valid.any() else None
        summary["oof_auc"] = summary["oof_pairwise_accuracy"]
    return fold_reports, oof, summary


# --------------------------------------------------------------------------------------
# 编排
# --------------------------------------------------------------------------------------

def run_training(out_dir: str, target: str, folds: int, seed: int, l2: float, lr: float,
                 epochs: int, min_samples: int, min_groups: int,
                 model_path: str | None = None, report_path: str | None = None,
                 model_version: str | None = None, quiet: bool = False,
                 synthetic: bool = False, game: str = GAME_ALL,
                 publish: bool = True) -> dict:
    manifest = load_manifest(out_dir)
    score_version = str(manifest.get("ScoreVersionFilter") or DEFAULT_SCORE_VERSION)
    if isinstance(manifest.get("ScoreVersionFilter"), list) and len(manifest["ScoreVersionFilter"]) == 1:
        score_version = manifest["ScoreVersionFilter"][0]

    os.makedirs(os.path.join(out_dir, "model"), exist_ok=True)
    if model_path is None:
        name = "preference-v1.json" if target == "feel" else "preference-pairwise-v1.json"
        model_path = os.path.join(out_dir, "model", name)
    if report_path is None:
        report_path = os.path.splitext(model_path)[0] + ".report.txt"
    os.makedirs(os.path.dirname(os.path.abspath(model_path)), exist_ok=True)

    lines: list[str] = []
    add = lines.append
    add("=" * 78)
    add("AimSense 离线训练报告（train.py）")
    add("=" * 78)
    add(f"生成时间：{_dt.datetime.now().astimezone().isoformat(timespec='seconds')}")
    add(f"训练目标：{'Feel >= 7（主观手感二分类）' if target == 'feel' else 'preference（成对偏好，Bradley-Terry 式 pairwise logistic）'}")
    add(f"适用游戏：{'ALL（跨游戏训练，预测需谨慎）' if game == GAME_ALL else game}"
        + ("" if game == GAME_ALL else "（C# 侧只对同游戏会话给出影子预测）"))
    add(f"数据来源：{os.path.abspath(out_dir)}")
    if manifest:
        add(f"  导出时间：{manifest.get('GeneratedAt')}")
        add(f"  trace 目录：{manifest.get('TracesDir')}")
        add(f"  标签文件：{manifest.get('LabelsFile')}")
        add(f"  FeatureVersion：{manifest.get('FeatureVersion')}"
            + ("" if manifest.get("FeatureVersion") == FEATURE_VERSION
               else f"  ← 与训练脚本的 {FEATURE_VERSION} 不一致，请重新导出！"))
        add(f"  评分版本：{score_version}")
        add(f"  导出块数：{manifest.get('BlocksExported')}，候选数：{manifest.get('Candidates')}")
        labels_info = manifest.get("Labels") or {}
        add("  标签条数：" + ("、".join(f"{k}={v}" for k, v in sorted(labels_info.items())) if labels_info else "无"))
    else:
        add("  [告警] 未找到 manifest.json，请先运行 export_dataset.py")
    add("")

    label_count = 0
    if target == "feel":
        rows, X, y, groups, dstats = load_feel_dataset(out_dir, game=game)
        label_count = int(dstats["candidates_with_feel"])
        add("-- 1. 样本与标签 " + "-" * 61)
        add(f"候选总数：{dstats['candidates_total']}；有 Feel 标签的候选：{dstats['candidates_with_feel']}；"
            f"无标签被排除：{dstats['candidates_without_feel']}")
        if label_count:
            add(f"标签分布：正类（Feel>=7）={dstats['positive']}，负类={dstats['negative']}")
            add(f"Feel 取值直方图：{dstats['feel_value_counts']}")
            add(f"涉及 session 数：{len(set(str(g) for g in groups))}")
        add("")
    else:
        rows, Xb, Xw, groups, dstats = load_preference_dataset(out_dir, game=game)
        X = Xb
        y = np.ones(len(rows), dtype=float)
        label_count = int(dstats["pairs_usable"])
        add("-- 1. 样本与标签 " + "-" * 61)
        add(f"偏好对总数：{dstats['pairs_total']}；两侧候选都齐全可用：{dstats['pairs_usable']}；"
            f"被排除：{dstats['pairs_unusable']}"
            + (f"；因不属于游戏 {game} 而排除：{dstats['pairs_other_game']}" if game != GAME_ALL else ""))
        for ex in dstats["unusable_examples"]:
            add(f"  [排除示例] {ex}")
        if label_count:
            add(f"涉及 session 数：{len(set(str(g) for g in groups))}")
        add("")

    n_samples = len(rows)
    n_groups = len(set(str(g) for g in groups)) if n_samples else 0
    insufficient = (n_samples < min_samples) or (n_groups < min_groups)

    # ---- 没有标签：正常退出，不训练 ----
    if n_samples == 0:
        add("-- 2. 结论 " + "-" * 65)
        add(f"{INSUFFICIENT_BANNER}。")
        add("原因：导出数据中没有任何可用于该目标的标签。")
        add("  · feel 目标需要标签文件 %LOCALAPPDATA%\\AimSense\\labels\\labels-v1.jsonl 中的 \"feel\" 记录；")
        add("  · preference 目标需要 \"preference\" 记录，且更好/更差两侧的档位都要有有效块。")
        add("当前标签数为 0（用户刚开始采集，尚未跑新会话），因此无法训练任何模型。")
        add("这不是错误：管线已跑通，攒够标签后重跑本脚本即可。")
        add("")
        model = {
            "ModelVersion": model_version or MODEL_VERSION,
            "LabelSchemaVersion": LABEL_SCHEMA_VERSION,
            "FeatureVersion": FEATURE_VERSION,
            "ScoreVersion": score_version,
            "Game": game,
            "Target": "Feel>=7" if target == "feel" else "preference(Better>Worse)",
            "Features": list(FEATURES),
            "Mean": [0.0] * len(FEATURES),
            "Scale": [1.0] * len(FEATURES),
            "Weights": [0.0] * len(FEATURES),
            "Bias": 0.0,
            "TrainedAt": _dt.datetime.now().astimezone().isoformat(timespec="seconds"),
            "Samples": 0,
            "Groups": 0,
            "Auc": 0.0,
            "Notes": (f"{INSUFFICIENT_BANNER}。标签数为 0，未进行任何训练；"
                      "本文件只用于固定输出格式，Weights 全为 0，禁止用于任何实际预测。"
                      + ("" if game == GAME_ALL else f"适用游戏：{game}。")),
        }
        attach_limitations(add, label_count, n_groups, dstats, manifest, target, insufficient=True,
                           heading=3)
        published, publish_note = publish_model(
            model_path, eligible=False,
            reason="本次没有任何真实标签，产出的只是占位模型（Weights 全为 0）")
        add("-- 4. 输出 " + "-" * 65)
        add(f"  模型 JSON：{os.path.abspath(model_path)}")
        add(f"  文本报告：{os.path.abspath(report_path)}")
        add(f"  C# 影子模式：{publish_note}")
        with open(model_path, "w", encoding="utf-8") as fh:
            json.dump(model, fh, ensure_ascii=False, indent=2)
        with open(report_path, "w", encoding="utf-8") as fh:
            fh.write("\n".join(lines) + "\n")
        if not quiet:
            print("\n".join(lines))
        return {"model": model, "report_path": report_path, "model_path": model_path,
                "insufficient": True, "no_labels": True, "summary": {},
                "published": published, "publish_note": publish_note}

    # ---- 交叉验证 ----
    add("-- 2. 分组交叉验证 " + "-" * 58)
    add(f"折数：{folds}（按 session 分组，同一 session 的候选绝不跨折）；随机种子：{seed}")
    add(f"样本量：{n_samples}；session 数：{n_groups}；判定阈值：样本 >= {min_samples} 且 session >= {min_groups}")
    if insufficient:
        add("")
        add(f"*** {INSUFFICIENT_BANNER} ***")
        add(f"*** 当前样本 {n_samples} / session {n_groups}，低于最低要求 "
            f"{min_samples} / {min_groups}；下面所有指标都只是管线自证，***")
        add("*** 它们不能说明模型学到了任何真实规律，禁止据此做任何决策。 ***")
    add("")

    pairwise_X = None if target == "feel" else (Xb, Xw)
    fold_reports, oof, summary = run_cv(X, y, groups, folds, seed, l2, lr, epochs, pairwise_X)

    for fr in fold_reports:
        if target == "feel":
            auc_txt = f"{fr['auc']:.4f}" if fr["auc"] is not None else "不可计算（该折只有单一类别）"
            add(f"  第 {fr['fold']} 折：训练 {fr['train']}（{fr['groups_train']} session）"
                f" / 验证 {fr['val']}（{fr['groups_val']} session）"
                f"  AUC={auc_txt}  准确率={fr['accuracy']:.4f}"
                f"  混淆[[{fr['tn']},{fr['fp']}],[{fr['fn']},{fr['tp']}]]")
        else:
            acc_txt = f"{fr['pairwise_accuracy']:.4f}" if fr["pairwise_accuracy"] is not None else "不可计算"
            add(f"  第 {fr['fold']} 折：训练 {fr['train']} 对（{fr['groups_train']} session）"
                f" / 验证 {fr['val']} 对（{fr['groups_val']} session）  成对排序准确率={acc_txt}")
    add("")

    if target == "feel":
        oof_auc = summary.get("oof_auc")
        add("-- 3. 折外（out-of-fold）总体指标 " + "-" * 45)
        add(f"总体 AUC：{oof_auc:.4f}" if oof_auc is not None else "总体 AUC：不可计算（只有一个类别）")
        if summary.get("oof_accuracy") is not None:
            tn, fp, fn, tp = summary["oof_confusion"]
            add(f"总体准确率：{summary['oof_accuracy']:.4f}")
            add(f"总体混淆矩阵：TN={tn}  FP={fp}  FN={fn}  TP={tp}")
            add(f"  基率（全判负的准确率）：{max(dstats['negative'], 0) / max(n_samples, 1):.4f}")
        add("")
    else:
        add("-- 3. 折外（out-of-fold）总体指标 " + "-" * 45)
        add(f"总体成对排序准确率：{summary.get('oof_pairwise_accuracy'):.4f}"
            if summary.get("oof_pairwise_accuracy") is not None else "总体成对排序准确率：不可计算")
        add("")
        oof_auc = summary.get("oof_auc")

    oof_auc = summary.get("oof_auc")

    # ---- 全量拟合 + 导出 ----
    add("-- 4. 全量拟合模型 " + "-" * 62)
    if target == "feel":
        (Xt,), mean, scale, all_missing = standardize(X, [])
        w, b = fit_logreg(Xt, y, l2=l2, lr=lr, epochs=epochs)
        target_name = "Feel>=7"
    else:
        n_rows = len(rows)
        (both_std,), mean, scale, all_missing = standardize(np.vstack([Xb, Xw]), [])
        w, b = fit_pairwise(both_std[:n_rows], both_std[n_rows:], l2=l2, lr=lr, epochs=epochs)
        target_name = "preference(Better>Worse)"

    order = np.argsort(-np.abs(w))
    add("按 |权重| 排序的前 10 个特征（标准化尺度，仅描述拟合结果，不代表因果或显著性）：")
    for j in order[:10]:
        add(f"  {FEATURES[j]:<22} w={w[j]:+.4f}  （缺失填充均值 {mean[j]:.4f}，尺度 {scale[j]:.4f}）")
    if all_missing:
        add(f"全缺失特征（已填 0，权重无意义）：{'、'.join(all_missing)}")
    add(f"偏置 Bias={b:+.4f}；L2={l2}；学习率={lr}；迭代={epochs}")
    add("")

    miss_rate = missing_rate_table([Xb, Xw] if pairwise_X is not None else [X])
    worst = [(k, v) for k, v in sorted(miss_rate.items(), key=lambda x: -x[1]) if v > 0]
    add("契约特征的缺失率（缺失值统一按训练集均值填充；同一列在影子模式推理时也走均值）：")
    if worst:
        for name, rate in worst:
            add(f"  {name:<22} 缺失 {rate * 100:5.1f}%")
    else:
        add("  （无缺失）")
    add("")

    notes_parts = [
        "标准化：(x-mean)/scale；缺失值用训练集特征均值填充（见 Mean 字段），"
        "C# 影子模式推理遇到缺失特征时同样回填该均值",
        f"GroupKFold 按 session 分组，{summary.get('k')} 折，种子 {seed}",
    ]
    if worst:
        notes_parts.append("缺失率最高的列：" +
                           "、".join(f"{k} {v * 100:.0f}%" for k, v in worst[:6]))
    else:
        notes_parts.append("训练数据中契约特征无缺失")
    if all_missing:
        notes_parts.append("全缺失列已置 0，权重无意义：" + "、".join(all_missing))
    notes_parts.append("meanTurnResponse / meanRecovery 单位为秒，meanAngularError / "
                       "meanOvershootRadii 以头部角半径为单位")
    notes_parts.append("centerRatio 的分母 CenterCm 来自 “session” 标签快照；"
                       "trace 的 session 记录没有该字段，缺失时回退 sqrt(Low*High) 代理，"
                       "与 C# 推理时的真实 _centerCm 可能有轻微偏差")
    notes_parts.append(f"训练只使用 ScoreVersion={score_version} 的块；"
                       "C# 侧 OfflineModel.Load 校验 FeatureVersion，"
                       "OfflineModel.CanPredict(scoreVersion) 在不一致时拒绝预测，"
                       "因此跨评分版本不会给出影子预测")
    if game == GAME_ALL:
        notes_parts.append("Game=ALL：标签跨游戏混合训练，C# 侧不做游戏闸门；"
                           "不同游戏的灵敏度语义与动作分布不同，预测需谨慎，优先按游戏分别训练")
    else:
        notes_parts.append(f"Game={game}：本模型只对该游戏的会话预测，"
                           "C# 侧对其它游戏不会给出影子预测")
    if insufficient:
        notes_parts.append(f"{INSUFFICIENT_BANNER}（样本 {n_samples} < {min_samples} 或 session {n_groups} < {min_groups}）")
    if target != "feel":
        notes_parts.append("Auc 字段在此目标下表示成对排序准确率（数值上等于成对 AUC）")
    notes_parts.append("Score 是轨迹特征的确定性函数，与候选特征同源，存在自指泄漏风险；本模型不是因果结论")

    auc_out = float(oof_auc) if oof_auc is not None and math.isfinite(float(oof_auc)) else 0.0
    model = {
        "ModelVersion": model_version or MODEL_VERSION,
        "LabelSchemaVersion": LABEL_SCHEMA_VERSION,
        "FeatureVersion": FEATURE_VERSION,
        "ScoreVersion": score_version,
        "Game": game,
        "Target": target_name,
        "Features": list(FEATURES),
        "Mean": [float(v) for v in mean],
        "Scale": [float(v) for v in scale],
        "Weights": [float(v) for v in w],
        "Bias": float(b),
        "TrainedAt": _dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "Samples": int(n_samples),
        "Groups": int(n_groups),
        "Auc": auc_out,
        "Notes": "；".join(notes_parts) + "。",
    }
    # 自检：长度必须严格一致
    assert len(model["Features"]) == len(model["Mean"]) == len(model["Scale"]) == len(model["Weights"]), \
        "Features/Mean/Scale/Weights 长度不一致"

    add("-- 5. 被排除的数据 " + "-" * 62)
    excluded = manifest.get("Excluded") or {}
    if excluded:
        for reason, count in sorted(excluded.items(), key=lambda x: -x[1]):
            add(f"  {reason}: {count}")
    else:
        add("  （manifest 未记录排除项；请确认已运行 export_dataset.py）")
    if target == "feel":
        add(f"  有候选但无 Feel 标签：{dstats['candidates_without_feel']} 条")
    else:
        add(f"  偏好对缺一侧候选：{dstats['pairs_unusable']} 条")
    add(f"  本目标实际参与训练：{n_samples} 条 / {n_groups} session")
    add("")

    attach_limitations(add, label_count, n_groups, dstats, manifest, target, insufficient)

    add("-- 7. 输出 " + "-" * 65)
    add(f"  模型 JSON：{os.path.abspath(model_path)}")
    add(f"  文本报告：{os.path.abspath(report_path)}")

    with open(model_path, "w", encoding="utf-8") as fh:
        json.dump(model, fh, ensure_ascii=False, indent=2)
    with open(report_path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")

    if not quiet:
        print("\n".join(lines))

    # ---- 发布到 C# 影子模式读取路径 ----
    # 只有"真实标签训练出的 feel 模型"才允许发布；合成/自检/数据不足/按游戏分文件一律拒绝。
    if synthetic:
        eligible, why = False, "本次运行使用的是合成数据（--self-test），绝不能用合成模型污染真实推理"
    elif target != "feel":
        eligible, why = False, "当前目标是 preference 成对偏好，而 C# 影子模式只消费 Feel>=7 模型"
    elif insufficient:
        eligible, why = False, (f"真实数据不足（样本 {n_samples} < {min_samples} 或 "
                               f"session {n_groups} < {min_groups}），{INSUFFICIENT_BANNER}")
    elif not publish:
        eligible, why = False, ("本次是跨游戏分别训练出的按游戏模型，"
                               "C# 侧目前只有单一读取路径 preference-v1.json，没有按游戏选择模型的逻辑")
    else:
        eligible, why = True, ""
    published, publish_note = publish_model(model_path, eligible, why)
    if not quiet:
        print(f"C# 影子模式：{publish_note}")
    with open(report_path, "a", encoding="utf-8") as fh:
        fh.write(f"\n-- 8. 发布到 C# 影子模式 " + "-" * 50 + "\n")
        fh.write(f"  {publish_note}\n")
        if published:
            fh.write("  提示：C# 侧 OfflineModel.Load 会校验 FeatureVersion==candidate-features-1，"
                     "文件损坏或版本不符时它会安全地拒绝加载。\n")

    return {"model": model, "report_path": report_path, "model_path": model_path,
            "insufficient": insufficient, "no_labels": False, "summary": summary,
            "fold_reports": fold_reports, "oof": oof,
            "published": published, "publish_note": publish_note}


def attach_limitations(add, label_count: int, n_groups: int, dstats: dict, manifest: dict,
                       target: str, insufficient: bool, heading: int = 6) -> None:
    add(f"-- {heading}. 明确的局限性 " + "-" * 60)
    if insufficient:
        add(f"  ★ {INSUFFICIENT_BANNER}。以上所有指标只证明代码能跑通，不含任何有效性证据。")
    add("  · 样本量：当前标签来自单个用户刚开始采集的会话，不同 session 之间还共享同一台机器、")
    add("    同一个游戏、同一段时期的手感，分组交叉验证只能去掉一部分会话内泄漏，不是独立同分布。")
    add("  · 主观噪声：Feel 是 0–10 的自评整数，同一档位重测可能给出不同分数；")
    add("    用 >=7 一刀切会把这个噪声变成标签翻转，样本越少影响越大。")
    add("  · 自指泄漏：Score/Loss 是轨迹特征的确定性函数，它和运动学特征来自同一批样本。")
    add("    用 Score 相关特征预测 Feel 时，模型可能只是在复述评分公式，而不是在解释手感。")
    add("    绝不能用 Score 当标签——那是自证，不是学习。")
    add("  · 混杂因素：cm/360 与 eDPI、游戏、DPI、显示条件、训练适应度相关；")
    add("    只有同一 session 内交错测试的档位才有可比的对照关系。")
    add(f"  · 过拟合：契约特征 {len(FEATURES)} 个，样本可能只有几十条，逻辑回归在如此维度下必然过拟合；")
    add("    折间 AUC 波动会非常大，单折高 AUC 不能当作发现。")
    add("  · 训练/服务一致性：C# 影子模式会校验 FeatureVersion（Load）与 ScoreVersion（CanPredict），")
    add("    Game 字段则为游戏闸门（\"ALL\" 或缺省时不做闸门）。这三者任何一个不匹配，预测都不该被采信；")
    add("    但闸门只保证口径对得上，不保证模型本身有意义。")
    add("  · 自评的时间漂移：手感标签是在测试当天打的，训练适应度会随日期变化，")
    add("    晚期会话的标签与早期不可直接比较。")
    add("  · 结论门槛：至少数百条有标签候选、且覆盖 >= 10 个独立 session，才谈得上「模型是否有效」。")
    add("    在那之前，本脚本的输出只是「管线可复现」的证明。")
    add("")


# --------------------------------------------------------------------------------------
# 自检
# --------------------------------------------------------------------------------------

def make_synthetic_candidates(n_groups: int, per_group: int, seed: int,
                              game: str = "CS2", prefix: str = "synth") -> tuple[list[dict], list[dict]]:
    """合成候选表：Feel 由少数几个契约特征线性决定 + 明显噪声，其余特征纯噪声。

    列名严格使用 candidate-features-1 契约名，确保自检跑的就是真实产线口径。
    """
    rng = np.random.default_rng(seed)
    cm_pool = [26.5, 28.8, 31.1, 33.1, 36.0, 38.9, 40.8, 45.0]
    candidates: list[dict] = []
    pairs: list[dict] = []
    for g in range(n_groups):
        session = f"{prefix}-{g:04d}"
        dpi = 800.0
        center_cm = 32.0
        best = float(rng.choice(cm_pool))
        for i in range(per_group):
            cm = cm_pool[i % len(cm_pool)]
            sens = 360 * 2.54 / (dpi * cm * 0.022)
            # 真实信号：离个人最佳档位越近越好；表现分更高更好；换向响应更快更好
            closeness = math.exp(-abs(cm - best) / 4.0)
            mean_score = 40 + 35 * closeness + float(rng.normal(0, 6))
            turn = max(0.02, 0.32 - 0.22 * closeness + float(rng.normal(0, 0.03)))
            recovery = max(0.01, 0.22 - 0.14 * closeness + float(rng.normal(0, 0.03)))
            z = (2.6 * closeness + 0.06 * (mean_score - 55) - 3.0 * turn
                 + float(rng.normal(0, 0.9)))
            feel = int(np.clip(round(5.0 + z), 0, 10))
            valid_blocks = int(rng.integers(3, 7))
            scores = [mean_score + float(rng.normal(0, 5)) for _ in range(valid_blocks)]
            sc = np.asarray(scores, dtype=float)
            cand = {
                "CandidateKey": f"{session}|angular-block-loss-v3|{cm:.4f}",
                "SessionId": session, "Game": game,
                "ScoreVersion": "angular-block-loss-v3", "TrackingFamily": False,
                "TraceSessionId": session, "VirtualCandidate": False,
                "blocks": valid_blocks + int(rng.integers(0, 2)),
                "validBlocks": valid_blocks,
                "meanScore": float(sc.mean()),
                "maxScore": float(sc.max()),
                "minScore": float(sc.min()),
                "scoreStd": float(sc.std(ddof=1)) if sc.size > 1 else 0.0,
                "clickScore": mean_score + float(rng.normal(0, 2)),
                "trackScore": mean_score + float(rng.normal(0, 3)),
                "movingClickScore": mean_score + float(rng.normal(0, 3)),
                "hitRate": float(np.clip(rng.normal(0.8, 0.1), 0, 1)),
                "timeoutRate": float(np.clip(rng.normal(0.12, 0.06), 0, 1)),
                "missRate": float(np.clip(rng.normal(0.08, 0.05), 0, 1)),
                "trackingShare": float(np.clip(rng.normal(0.6, 0.08), 0, 1)),
                "meanAngularError": float(abs(rng.normal(1.5, 0.4))),
                "meanShotSeconds": float(abs(rng.normal(0.65, 0.1))),
                "overshootsPerBlock": float(abs(rng.normal(1.4, 1.0))),
                "correctionsPerBlock": float(abs(rng.normal(0.9, 0.8))),
                "meanOvershootRadii": float(abs(rng.normal(1.4, 0.5))),
                "meanTurnResponse": turn,
                "meanRecovery": recovery,
                "cm360": cm,
                "logCm360": math.log(cm),
                "centerRatio": cm / center_cm,
                "dpi": dpi,
                "sensitivity": sens,
                "edpi": dpi * sens,
                "x_WeightedScore": mean_score + float(rng.normal(0, 3)),
                "x_KinMeanSpeed": float(abs(rng.normal(900, 150))),
                "LabelFeel": feel,
                "LabelFeelBinary": 1 if feel >= 7 else 0,
                "LabelComparedToCurrent": "更好" if feel >= 7 else "更差",
                "LabelId": f"synth-feel-{g}-{i}",
                "LabelCreated": "2026-01-01T00:00:00+08:00",
            }
            # 故意挖缺失值，验证"均值填充 + Notes 里写缺失率"这条路径
            if rng.random() < 0.15:
                cand["trackScore"] = None
            if rng.random() < 0.10:
                cand["meanRecovery"] = None
            if rng.random() < 0.08:
                cand["centerRatio"] = None
            candidates.append(cand)

        # 成对偏好：同一 session 内按合成 Feel 抽更好/更差
        group_cands = candidates[-per_group:]
        if len(group_cands) >= 2:
            j, k = rng.choice(len(group_cands), size=2, replace=False)
            a, b = group_cands[j], group_cands[k]
            if a["LabelFeel"] != b["LabelFeel"]:
                better, worse = (a, b) if a["LabelFeel"] > b["LabelFeel"] else (b, a)
                pairs.append({
                    "PairKey": f"{prefix}-pref-{g}", "LabelId": f"{prefix}-pref-{g}",
                    "SessionId": session, "Game": game,
                    "BetterCm360": better["cm360"], "WorseCm360": worse["cm360"],
                    "Basis": "合成", "Note": "", "Created": "2026-01-01T00:00:00+08:00",
                    "Usable": True,
                    "BetterCandidateKey": better["CandidateKey"],
                    "WorseCandidateKey": worse["CandidateKey"],
                })
    return candidates, pairs


def self_test() -> int:
    print("=" * 78)
    print("train.py --self-test：合成数据（真实信号 + 噪声）端到端跑通")
    print("=" * 78)
    ok = True
    publish_target = os.path.join(local_appdata(), "AimSense", "model", "preference-v1.json")
    publish_target_preexisting = os.path.exists(publish_target)
    print(f"自检前 C# 模型路径是否存在：{publish_target_preexisting}（{publish_target}）")
    with tempfile.TemporaryDirectory(prefix="aimsense-train-selftest-") as root:
        out = os.path.join(root, "out")
        os.makedirs(out, exist_ok=True)
        candidates, pairs = make_synthetic_candidates(n_groups=40, per_group=8, seed=20260101)
        with open(os.path.join(out, "candidates.jsonl"), "w", encoding="utf-8") as fh:
            for c in candidates:
                fh.write(json.dumps(c, ensure_ascii=False) + "\n")
        with open(os.path.join(out, "preference_pairs.jsonl"), "w", encoding="utf-8") as fh:
            for p in pairs:
                fh.write(json.dumps(p, ensure_ascii=False) + "\n")
        with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as fh:
            json.dump({
                "FeatureVersion": FEATURE_VERSION, "LabelSchemaVersion": LABEL_SCHEMA_VERSION,
                "ScoreVersionFilter": ["angular-block-loss-v3"],
                "GeneratedAt": "2026-01-01T00:00:00+08:00", "TracesDir": "<synthetic>",
                "LabelsFile": "<synthetic>", "BlocksExported": 480, "Candidates": len(candidates),
                "Labels": {"feel": len(candidates), "preference": len(pairs)},
                "Excluded": {"synthetic_example": 3},
            }, fh, ensure_ascii=False, indent=2)

        print()
        print(">>> 目标 1：Feel >= 7（单一游戏）")
        r1 = run_training(out, "feel", folds=5, seed=7, l2=1.0, lr=0.5, epochs=3000,
                          min_samples=MIN_SAMPLES, min_groups=MIN_GROUPS, quiet=True,
                          synthetic=True, game="CS2")
        auc1 = r1["model"]["Auc"]
        print(f"   合成数据 AUC = {auc1:.4f}（样本 {r1['model']['Samples']}，"
              f"session {r1['model']['Groups']}，退出码 0）")
        for fr in r1["fold_reports"]:
            a = f"{fr['auc']:.4f}" if fr["auc"] is not None else "n/a"
            print(f"     第 {fr['fold']} 折  AUC={a}  准确率={fr['accuracy']:.4f}  "
                  f"验证 {fr['val']}（{fr['groups_val']} session）")
        print(f"   折外总体 AUC = {r1['summary']['oof_auc']:.4f}，"
              f"准确率 = {r1['summary']['oof_accuracy']:.4f}，"
              f"混淆矩阵 {r1['summary']['oof_confusion']}")
        print(f"   模型：{r1['model_path']}")
        print(f"   报告：{r1['report_path']}")
        with open(r1["report_path"], encoding="utf-8") as fh:
            rep1 = fh.read()
        print(f"   Notes 里写了缺失率最高列：{'缺失率最高的列' in r1['model']['Notes']}")
        ok = ok and (auc1 > 0.6)
        ok = ok and (r1["model"]["Samples"] >= MIN_SAMPLES)
        ok = ok and (len(r1["model"]["Features"]) == len(r1["model"]["Mean"]) ==
                     len(r1["model"]["Scale"]) == len(r1["model"]["Weights"]) == len(FEATURES))
        ok = ok and INSUFFICIENT_BANNER not in r1["model"]["Notes"]
        ok = ok and ("缺失率最高的列" in r1["model"]["Notes"])
        ok = ok and (not r1["published"])  # 合成数据绝不发布
        ok = ok and (r1["model"]["Game"] == "CS2")
        ok = ok and (not os.path.exists(publish_target))
        ok = ok and ("适用游戏：CS2" in rep1)

        print()
        print(">>> 目标 2：preference（成对偏好）")
        r2 = run_training(out, "preference", folds=5, seed=7, l2=1.0, lr=0.5, epochs=3000,
                          min_samples=MIN_SAMPLES, min_groups=MIN_GROUPS, quiet=True,
                          synthetic=True, game="CS2")
        print(f"   成对排序准确率 = {r2['model']['Auc']:.4f}（对 {r2['model']['Samples']} 对，"
              f"session {r2['model']['Groups']}）")
        for fr in r2["fold_reports"]:
            a = f"{fr['pairwise_accuracy']:.4f}" if fr["pairwise_accuracy"] is not None else "n/a"
            print(f"     第 {fr['fold']} 折  成对准确率={a}  验证 {fr['val']} 对")
        ok = ok and (r2["model"]["Auc"] >= 0.5)
        ok = ok and (not r2["published"])
        ok = ok and (r2["model"]["Game"] == "CS2")

        print()
        print(">>> 目标 3：跨游戏数据 + auto —— 必须按游戏分别出模型")
        multi_out = os.path.join(root, "multi")
        os.makedirs(multi_out, exist_ok=True)
        mc1, mp1 = make_synthetic_candidates(30, 8, seed=11, game="CS2", prefix="cs2")
        mc2, mp2 = make_synthetic_candidates(30, 8, seed=12, game="Valorant", prefix="val")
        with open(os.path.join(multi_out, "candidates.jsonl"), "w", encoding="utf-8") as fh:
            for c in mc1 + mc2:
                fh.write(json.dumps(c, ensure_ascii=False) + "\n")
        with open(os.path.join(multi_out, "preference_pairs.jsonl"), "w", encoding="utf-8") as fh:
            for p in mp1 + mp2:
                fh.write(json.dumps(p, ensure_ascii=False) + "\n")
        with open(os.path.join(multi_out, "manifest.json"), "w", encoding="utf-8") as fh:
            json.dump({"FeatureVersion": FEATURE_VERSION, "ScoreVersionFilter": ["angular-block-loss-v3"],
                       "Labels": {"feel": len(mc1) + len(mc2)}, "Excluded": {}}, fh, ensure_ascii=False)
        labeled_games, all_games = discover_games(multi_out, "feel")
        print(f"   识别到有标签的游戏：{labeled_games}")
        results_multi = run_all(multi_out, "feel", "auto", folds=5, seed=7, l2=1.0, lr=0.5,
                                epochs=3000, min_samples=MIN_SAMPLES, min_groups=MIN_GROUPS,
                                model_file=None, report_file=None, quiet=True, synthetic=True)
        names = [os.path.basename(r["model_path"]) for r in results_multi]
        print(f"   产出模型文件：{names}")
        for r in results_multi:
            print(f"     {r['model']['Game']}: 样本 {r['model']['Samples']} · "
                  f"session {r['model']['Groups']} · AUC {r['model']['Auc']:.4f} · "
                  f"已发布={r['published']}")
        ok = ok and (labeled_games == ["CS2", "Valorant"])
        ok = ok and len(results_multi) == 2
        ok = ok and sorted(names) == ["preference-v1-cs2.json", "preference-v1-valorant.json"]
        ok = ok and all(not r["published"] for r in results_multi)
        ok = ok and all(r["model"]["Game"] in ("CS2", "Valorant") for r in results_multi)
        ok = ok and (not os.path.exists(publish_target))

        print()
        print(">>> 目标 4：跨游戏数据 + --game ALL —— 只允许显式要求")
        ra = run_all(multi_out, "feel", "ALL", folds=5, seed=7, l2=1.0, lr=0.5, epochs=3000,
                     min_samples=MIN_SAMPLES, min_groups=MIN_GROUPS, model_file=None,
                     report_file=None, quiet=True, synthetic=True)
        print(f"   模型文件：{os.path.basename(ra[0]['model_path'])} · Game={ra[0]['model']['Game']} · "
              f"样本 {ra[0]['model']['Samples']}")
        print(f"   Notes 含跨游戏警示：{'跨游戏混合训练' in ra[0]['model']['Notes']}")
        ok = ok and len(ra) == 1
        ok = ok and ra[0]["model"]["Game"] == "ALL"
        ok = ok and ("跨游戏混合训练" in ra[0]["model"]["Notes"])
        ok = ok and (not ra[0]["published"])
        ok = ok and (not os.path.exists(publish_target))

        print()
        print(">>> 目标 5：数据不足分支（把阈值抬到不可能满足）")
        r3 = run_training(out, "feel", folds=5, seed=7, l2=1.0, lr=0.5, epochs=500,
                          min_samples=10_000_000, min_groups=10_000, quiet=True,
                          synthetic=True, game="CS2")
        with open(r3["report_path"], encoding="utf-8") as fh:
            rep3 = fh.read()
        has_banner = INSUFFICIENT_BANNER in rep3 and INSUFFICIENT_BANNER in r3["model"]["Notes"]
        print(f"   报告含'数据不足'横幅：{has_banner}；"
              f"模型 Notes 含横幅：{INSUFFICIENT_BANNER in r3['model']['Notes']}；"
              f"已拒绝发布：{not r3['published']}")
        ok = ok and has_banner and (not r3["published"])

        print()
        print(">>> 目标 6：零标签分支（空数据必须退出码 0、不崩溃）")
        empty_out = os.path.join(root, "empty")
        os.makedirs(empty_out, exist_ok=True)
        with open(os.path.join(empty_out, "manifest.json"), "w", encoding="utf-8") as fh:
            json.dump({"ScoreVersionFilter": ["angular-block-loss-v3"], "Labels": {}}, fh)
        r4 = run_training(empty_out, "feel", folds=5, seed=7, l2=1.0, lr=0.5, epochs=100,
                          min_samples=MIN_SAMPLES, min_groups=MIN_GROUPS, quiet=True,
                          synthetic=True, game=GAME_ALL)
        print(f"   无标签时正常返回：{r4['no_labels']}，模型 Samples={r4['model']['Samples']}，"
              f"已拒绝发布：{not r4['published']}")
        ok = ok and r4["no_labels"] and r4["model"]["Samples"] == 0 and (not r4["published"])
        if not publish_target_preexisting:
            leaked = os.path.exists(publish_target)
            print(f"   未污染 C# 模型路径：{not leaked}（{publish_target}）")
            ok = ok and (not leaked)

    print()
    print("自检结果：" + ("全部通过（退出码 0）" if ok else "存在失败项（退出码 2）"))
    return 0 if ok else 2


# --------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------

def run_all(out_dir: str, target: str, game_arg: str, folds: int, seed: int, l2: float, lr: float,
            epochs: int, min_samples: int, min_groups: int, model_file: str | None,
            report_file: str | None, quiet: bool, synthetic: bool = False,
            allow_publish: bool = True) -> list[dict]:
    """决定"训几个模型"：单一游戏出一个；跨游戏默认按游戏各出一个。

    game_arg:
      "auto"  —— 按标签实际涉及的游戏自动决定（默认）
      "ALL"   —— 显式要求跨游戏混合训练（会写进 Notes 警示）
      "CS2" 等 —— 只用该游戏的数据
    """
    # 默认文件名按目标区分：feel 用 C# 读取的 preference-v1.json，
    # 成对模型用 preference-pairwise-v1.json，**绝不能**覆盖 C# 读的那个文件。
    base_stem = "preference-v1" if target == "feel" else "preference-pairwise-v1"
    default_model = os.path.join(out_dir, "model", f"{base_stem}.json")
    if game_arg == "auto":
        labeled, all_games = discover_games(out_dir, target)
        pool = labeled or all_games
        if len(pool) <= 1:
            runs = [pool[0] if pool else GAME_ALL]
        else:
            runs = pool
    else:
        runs = [game_arg]

    multi = len(runs) > 1
    results: list[dict] = []
    for g in runs:
        if model_file:
            stem = os.path.splitext(os.path.basename(model_file))[0]
            mp = model_file if len(runs) == 1 else \
                os.path.join(os.path.dirname(model_file), f"{stem}-{game_slug(g)}.json")
        elif multi:
            mp = os.path.join(out_dir, "model", f"{base_stem}-{game_slug(g)}.json")
        else:
            mp = default_model
        rp = report_file if (report_file and len(runs) == 1) else os.path.splitext(mp)[0] + ".report.txt"
        if not quiet and multi:
            print()
            print("=" * 78)
            print(f"按游戏分别训练：{g}")
            print("=" * 78)
        results.append(run_training(
            out_dir, target, folds, seed, l2, lr, epochs, min_samples, min_groups,
            model_path=mp, report_path=rp, quiet=quiet, synthetic=synthetic,
            game=g, publish=(allow_publish and not multi)))
    return results


def parse_args(argv: list[str]) -> argparse.Namespace:
    here = os.path.dirname(os.path.abspath(__file__))
    p = argparse.ArgumentParser(description="AimSense 离线训练（纯 numpy，按 session 分组交叉验证）")
    p.add_argument("--out", default=os.path.join(here, "out"), help="export_dataset.py 的输出目录")
    p.add_argument("--target", choices=["feel", "preference"], default="feel",
                   help="feel=预测 Feel>=7；preference=成对偏好")
    p.add_argument("--game", default="auto",
                   help="auto=按标签涉及的游戏自动决定（默认）；ALL=显式跨游戏混合训练；"
                        "或直接写游戏 Id（如 CS2）")
    p.add_argument("--folds", type=int, default=5, help="分组交叉验证折数")
    p.add_argument("--seed", type=int, default=20260101, help="随机种子（折分与合成数据）")
    p.add_argument("--l2", type=float, default=1.0, help="L2 正则强度")
    p.add_argument("--lr", type=float, default=0.5, help="梯度下降学习率")
    p.add_argument("--epochs", type=int, default=3000, help="梯度下降迭代次数")
    p.add_argument("--min-samples", type=int, default=MIN_SAMPLES, help="判定'数据足够'的最小样本量")
    p.add_argument("--min-groups", type=int, default=MIN_GROUPS, help="判定'数据足够'的最小 session 数")
    p.add_argument("--model-file", default=None, help="模型 JSON 输出路径（默认 out/model/preference-v1.json）")
    p.add_argument("--report-file", default=None, help="文本报告输出路径")
    p.add_argument("--no-publish", action="store_true",
                   help="禁止把模型复制到 %%LOCALAPPDATA%%\\AimSense\\model\\preference-v1.json")
    p.add_argument("--self-test", action="store_true", help="用合成数据端到端跑通")
    return p.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.self_test:
        return self_test()
    if not os.path.isdir(args.out):
        print(f"错误：找不到导出目录 {args.out}，请先运行 export_dataset.py", file=sys.stderr)
        return 1
    results = run_all(args.out, args.target, args.game, args.folds, args.seed, args.l2, args.lr,
                      args.epochs, args.min_samples, args.min_groups,
                      args.model_file, args.report_file, quiet=False,
                      allow_publish=not args.no_publish)
    print()
    for result in results:
        man = result["model"]
        print(f"完成：游戏 {man['Game']} · 样本 {man['Samples']} · session {man['Groups']} · "
              f"AUC {man['Auc']:.4f} · 模型 {result['model_path']}")
        if result["insufficient"]:
            print(f"  注意：{INSUFFICIENT_BANNER}")
        print(f"  C# 影子模式：{result['publish_note']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
