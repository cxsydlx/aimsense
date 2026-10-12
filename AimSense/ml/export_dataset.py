#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""AimSense 离线数据管线：trace JSONL + 人工标签 -> 块级 / 候选级 / 偏好对特征表。

设计约束（见 ml/README.md）：
  * 只读真实数据，绝不写入 %LOCALAPPDATA%\\AimSense 下的任何文件；
  * trace 可能单文件上百 MB，逐行流式解析，不把整份 trace 读进内存；
  * 逐帧 sampleBatch 只做一次在线聚合，不保留原始帧；
  * 只依赖标准库 + numpy（本机 `python` 没有 pandas，所以不强依赖 pandas）。

用法：
    python export_dataset.py                      # 对真实 trace + 真实标签导出
    python export_dataset.py --self-test          # 用内置合成数据在临时目录跑通
    python export_dataset.py --out ml/out --score-version angular-block-loss-v3
"""

from __future__ import annotations

import argparse
import collections
import csv
import datetime as _dt
import json
import math
import os
import sys
import tempfile
import time

import numpy as np

try:  # 避免 GBK 控制台打印中文/符号时抛 UnicodeEncodeError
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:  # pragma: no cover - 老版本或无控制台
    pass


# --------------------------------------------------------------------------------------
# 常量：与 AimSense C# 侧保持同口径
# --------------------------------------------------------------------------------------

DEFAULT_SCORE_VERSION = "angular-block-loss-v3"
FEATURE_VERSION = "candidate-features-1"
LABEL_SCHEMA_VERSION = 1

# 契约特征：名字与顺序必须与 C# 侧 AimSense\MainWindow.xaml.cs -> CandidateFeatures()
# 及 AimSense\OfflineModel.cs -> FeatureVersion "candidate-features-1" 严格一致。
# C# 影子模式推理会按这些名字回填特征，名字写错 = 那一列永远缺失。
CONTRACT_FEATURES = [
    "blocks", "validBlocks", "meanScore", "maxScore", "minScore", "scoreStd",
    "clickScore", "trackScore", "movingClickScore",
    "hitRate", "timeoutRate", "missRate", "trackingShare", "meanAngularError", "meanShotSeconds",
    "overshootsPerBlock", "correctionsPerBlock", "meanOvershootRadii", "meanTurnResponse", "meanRecovery",
    "cm360", "logCm360", "centerRatio", "dpi", "sensitivity", "edpi",
]

MODE_NAMES = {0: "Click", 1: "Track", 2: "MovingClick"}
PURPOSE_NAMES = {0: "Pilot", 1: "Search", 2: "Validation"}

# 与 AimSense\CalibrationDomain.cs -> GameProfile.Weight 完全一致。
# TrackingFamily=True（Apex/OW2 类跟枪型）：Click .20 / MovingClick .30 / Track .50
# TrackingFamily=False（CS2/Valorant 类点击型）：Click .45 / MovingClick .35 / Track .20
MODE_WEIGHTS_TRACKING = {0: 0.20, 2: 0.30, 1: 0.50}
MODE_WEIGHTS_TACTICAL = {0: 0.45, 2: 0.35, 1: 0.20}

# 与 AimSense\CalibrationDomain.cs -> GameProfiles 一致，用于在缺少 blockStart 记录时
# 自行反推 Sensitivity / eDPI。
GAME_YAW_PER_COUNT = {
    "cs2": 0.022,
    "valorant": 0.07,
    "apex": 0.022,
    "apex legends": 0.022,
    "ow2": 0.0066,
    "overwatch 2": 0.0066,
    "overwatch": 0.0066,
}

# 与 AimSense\LabelAnalysis.cs -> CmTolerance 一致：0.5% 相对容差，下限 0.01。
CM_TOLERANCE = 0.005


def same_cm(a: float | None, b: float | None) -> bool:
    if a is None or b is None:
        return False
    return abs(a - b) <= max(0.01, max(abs(a), abs(b)) * CM_TOLERANCE)


# --------------------------------------------------------------------------------------
# 小工具
# --------------------------------------------------------------------------------------

def local_appdata() -> str:
    base = os.environ.get("LOCALAPPDATA")
    if not base:
        base = os.path.join(os.path.expanduser("~"), "AppData", "Local")
    return base


def default_traces_dir() -> str:
    return os.path.join(local_appdata(), "AimSense", "traces")


def default_labels_file() -> str:
    return os.path.join(local_appdata(), "AimSense", "labels", "labels-v1.jsonl")


def default_history_dir() -> str:
    return os.path.join(local_appdata(), "AimSense", "history-v2")


def num_or_none(value):
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, (int, float)):
        f = float(value)
        return f if math.isfinite(f) else None
    return None


def int_or_none(value):
    f = num_or_none(value)
    return None if f is None else int(round(f))


def wrap_deg(x: float) -> float:
    """把角度差折到 (-180, 180]。"""
    return (x + 180.0) % 360.0 - 180.0


def percentile(values: list[float], q: float) -> float | None:
    if not values:
        return None
    return float(np.percentile(np.asarray(values, dtype=float), q))


def csv_cell(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        if not math.isfinite(value):
            return ""
        return repr(round(value, 6))
    if isinstance(value, (list, tuple)):
        return ";".join(str(v) for v in value)
    if isinstance(value, dict):
        return json.dumps(value, ensure_ascii=False, sort_keys=True)
    return str(value)


def write_csv(path: str, columns: list[str], rows: list[dict]) -> None:
    # utf-8-sig：Excel 打开中文表头不乱码
    with open(path, "w", encoding="utf-8-sig", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=columns, extrasaction="ignore")
        writer.writeheader()
        for row in rows:
            writer.writerow({c: csv_cell(row.get(c)) for c in columns})


def write_jsonl(path: str, rows: list[dict]) -> None:
    with open(path, "w", encoding="utf-8") as fh:
        for row in rows:
            fh.write(json.dumps(row, ensure_ascii=False, sort_keys=True) + "\n")


def human_size(n: int) -> str:
    if n < 1024:
        return f"{n} B"
    value = float(n)
    for unit in ("KB", "MB", "GB"):
        value /= 1024.0
        if value < 1024.0 or unit == "GB":
            return f"{value:.2f} {unit}"
    return f"{n} B"


# --------------------------------------------------------------------------------------
# 逐帧样本的在线运动学聚合
# --------------------------------------------------------------------------------------

KIN_KEYS = [
    "KinSamples",
    "KinScoringShare",
    "KinMeanFrameDtMs",
    "KinP95FrameDtMs",
    "KinMeanAbsDx",
    "KinMeanAbsDy",
    "KinMaxAbsDx",
    "KinDirectionReversals",
    "KinDirectionReversalRate",
    "KinPathLength",
    "KinAngularPathRad",
    "KinMeanSpeed",
    "KinP95Speed",
    "KinSpeedKurtosis",
    "KinDurationSeconds",
]


def aggregate_samples(samples: list) -> dict:
    """把一批逐帧样本（MainWindow.RawMoved 里 new { Tick, Time, Phase, DX, DY, ... }）
    聚合成运动学特征。单次遍历，不保留原始帧。"""
    out = {k: None for k in KIN_KEYS}
    n = 0
    times: list[float] = []
    dxs: list[float] = []
    dys: list[float] = []
    phases: collections.Counter = collections.Counter()
    yaws: list[float] = []
    pitches: list[float] = []

    for s in samples:
        if not isinstance(s, dict):
            continue
        t = num_or_none(s.get("Time"))
        dx = num_or_none(s.get("DX"))
        dy = num_or_none(s.get("DY"))
        if t is None or dx is None or dy is None:
            continue
        n += 1
        times.append(t)
        dxs.append(dx)
        dys.append(dy)
        ph = s.get("Phase")
        if isinstance(ph, str):
            phases[ph] += 1
        cy = num_or_none(s.get("CameraYaw"))
        cp = num_or_none(s.get("CameraPitch"))
        yaws.append(cy if cy is not None else float("nan"))
        pitches.append(cp if cp is not None else float("nan"))

    out["KinSamples"] = n
    if n == 0:
        return out

    t_arr = np.asarray(times, dtype=float)
    dx_arr = np.asarray(dxs, dtype=float)
    dy_arr = np.asarray(dys, dtype=float)

    scoring = sum(v for k, v in phases.items() if k == "Scoring")
    out["KinScoringShare"] = scoring / n if n else None

    if n >= 2:
        dt_ms = np.diff(t_arr) * 1000.0
        dt_ms = dt_ms[dt_ms > 0]
        if dt_ms.size:
            out["KinMeanFrameDtMs"] = float(dt_ms.mean())
            out["KinP95FrameDtMs"] = float(np.percentile(dt_ms, 95))

    out["KinMeanAbsDx"] = float(np.abs(dx_arr).mean())
    out["KinMeanAbsDy"] = float(np.abs(dy_arr).mean())
    out["KinMaxAbsDx"] = float(np.abs(dx_arr).max())

    # 方向反转：忽略 0 计数后 DX 符号翻转的次数
    signs = np.sign(dx_arr)
    signs = signs[signs != 0]
    reversals = int(np.count_nonzero(signs[1:] != signs[:-1])) if signs.size >= 2 else 0
    out["KinDirectionReversals"] = reversals

    span = float(t_arr[-1] - t_arr[0]) if n >= 2 else 0.0
    out["KinDurationSeconds"] = span if span > 0 else None
    out["KinDirectionReversalRate"] = (reversals / span) if span > 0 else None

    step = np.hypot(dx_arr, dy_arr)
    path = float(step.sum())
    out["KinPathLength"] = path

    # 角路径：相机 yaw/pitch 的绝对变化量之和（弧度），比鼠标计数更贴近"视角移动量"
    yaw_arr = np.asarray(yaws, dtype=float)
    pitch_arr = np.asarray(pitches, dtype=float)
    valid_yaw = np.isfinite(yaw_arr)
    if valid_yaw.sum() >= 2:
        vy = yaw_arr[valid_yaw]
        vp = pitch_arr[valid_yaw]
        dyaw = np.abs(np.mod(np.diff(vy) + 180.0, 360.0) - 180.0)
        dpitch = np.abs(np.diff(vp))
        out["KinAngularPathRad"] = float((dyaw + dpitch).sum() * math.pi / 180.0)

    if n >= 2:
        dt_s = np.diff(t_arr)
        dt_s = np.where(dt_s > 1e-3, dt_s, 1e-3)
        speed = step[1:] / dt_s  # counts / second
        if speed.size:
            out["KinMeanSpeed"] = float(speed.mean())
            out["KinP95Speed"] = float(np.percentile(speed, 95))
            m = speed.mean()
            d = speed - m
            m2 = float((d ** 2).mean())
            if m2 > 1e-12:
                m4 = float((d ** 4).mean())
                # 超额峰度（正态分布为 0）：越大说明速度分布越尖、爆发越集中
                out["KinSpeedKurtosis"] = m4 / (m2 ** 2) - 3.0
            else:
                out["KinSpeedKurtosis"] = 0.0
    return out


def merge_kinematics(a: dict | None, b: dict | None) -> dict | None:
    """同一 BlockId 出现多批样本时取样本数更多的那批（通常是被打断批 vs 完整批）。"""
    if a is None:
        return b
    if b is None:
        return a
    na = a.get("KinSamples") or 0
    nb = b.get("KinSamples") or 0
    return b if nb > na else a


# --------------------------------------------------------------------------------------
# 读取标签
# --------------------------------------------------------------------------------------

def read_labels(path: str) -> tuple[dict[str, list[dict]], list[str]]:
    """读 append-only JSONL 标签；坏行跳过。返回 {kind: [label, ...]} 与坏行描述。"""
    buckets: dict[str, list[dict]] = collections.defaultdict(list)
    problems: list[str] = []
    if not path or not os.path.isfile(path):
        return buckets, problems
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for lineno, line in enumerate(fh, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except Exception as exc:
                    problems.append(f"标签第 {lineno} 行 JSON 解析失败：{exc}")
                    continue
                if not isinstance(obj, dict):
                    problems.append(f"标签第 {lineno} 行不是 JSON 对象")
                    continue
                kind = obj.get("Kind")
                if not isinstance(kind, str):
                    problems.append(f"标签第 {lineno} 行缺少 Kind")
                    continue
                sv = obj.get("LabelSchemaVersion")
                if int_or_none(sv) not in (None, LABEL_SCHEMA_VERSION):
                    problems.append(f"标签第 {lineno} 行 LabelSchemaVersion={sv} 与 v{LABEL_SCHEMA_VERSION} 不同，已保留但请人工确认")
                buckets[kind].append(obj)
    except OSError as exc:
        problems.append(f"标签文件不可读：{exc}")
    return buckets, problems


def read_history(path: str) -> dict[str, dict]:
    """读 history-v2\\*.json，按 trace 文件名主干建立索引，用于补齐缺失的 session 元数据。"""
    index: dict[str, dict] = {}
    if not path or not os.path.isdir(path):
        return index
    for name in sorted(os.listdir(path)):
        if not name.lower().endswith(".json"):
            continue
        full = os.path.join(path, name)
        try:
            with open(full, "r", encoding="utf-8", errors="replace") as fh:
                obj = json.load(fh)
        except Exception:
            continue
        if not isinstance(obj, dict):
            continue
        tp = obj.get("TracePath")
        key = os.path.splitext(os.path.basename(tp))[0] if isinstance(tp, str) else os.path.splitext(name)[0]
        index[key] = obj
    return index


# --------------------------------------------------------------------------------------
# 主扫描
# --------------------------------------------------------------------------------------

class Scan():
    def __init__(self) -> None:
        self.sessions: dict[str, dict] = {}          # session_id -> 元数据
        self.legacy_records = collections.Counter()  # 旧格式（"Event" 而非 "Kind"）记录统计
        self.legacy_files: set[str] = set()
        self.ignored_records = collections.Counter()  # 与本次导出无关但被读到的记录
        self.block_records = 0
        self.prior_validation_records = 0
        self.interrupted_records = 0
        self.sample_batches = 0
        self.carried_batches = 0
        self.sampled_batches = 0
        self.total_frames = 0
        self.bad_lines = 0
        self.bad_line_examples: list[str] = []
        self.files = 0


def iter_trace_files(traces_dir: str) -> list[str]:
    if not os.path.isdir(traces_dir):
        return []
    found = [os.path.join(traces_dir, n) for n in sorted(os.listdir(traces_dir)) if n.lower().endswith(".jsonl")]
    return [f for f in found if os.path.isfile(f)]


def session_from_record(record: dict, session_id: str, trace_path: str, history: dict) -> dict:
    game = record.get("Game")
    game_id = ""
    tracking = None
    yaw = None
    if isinstance(game, dict):
        game_id = str(game.get("Id") or "")
        tracking = game.get("TrackingFamily")
        yaw = num_or_none(game.get("YawDegreesPerCount"))
    elif isinstance(game, str):
        game_id = game
    if not game_id:
        hist = history.get(session_id) or {}
        hg = hist.get("Game")
        if isinstance(hg, str):
            game_id = hg
    if yaw is None and game_id:
        yaw = GAME_YAW_PER_COUNT.get(game_id.strip().lower())
    dpi = num_or_none(record.get("Dpi"))
    score_version = record.get("ScoreVersion")
    if not isinstance(score_version, str) or not score_version:
        score_version = None
    return {
        "SessionId": session_id,
        "TracePath": trace_path,
        "SchemaVersion": int_or_none(record.get("SchemaVersion")),
        "ScoreVersion": score_version,
        "MotionVersion": record.get("MotionVersion") if isinstance(record.get("MotionVersion"), str) else None,
        "Dpi": dpi,
        "Fov": num_or_none(record.get("Fov")),
        "Game": game_id,
        "TrackingFamily": bool(tracking) if isinstance(tracking, bool) else None,
        "YawDegreesPerCount": yaw,
        "Seed": int_or_none(record.get("Seed")),
        "Low": num_or_none(record.get("Low")),
        "High": num_or_none(record.get("High")),
        "Modes": record.get("Modes") if isinstance(record.get("Modes"), list) else None,
        "Started": record.get("Started") if isinstance(record.get("Started"), str) else None,
        "ResumedFrom": record.get("ResumedFrom") if isinstance(record.get("ResumedFrom"), str) else None,
        "Legacy": False,
    }


def fallback_session(session_id: str, trace_path: str, history: dict) -> dict | None:
    hist = history.get(session_id)
    if not hist:
        return None
    return {
        "SessionId": session_id,
        "TracePath": trace_path,
        "SchemaVersion": int_or_none(hist.get("SchemaVersion")),
        "ScoreVersion": hist.get("ScoreVersion") if isinstance(hist.get("ScoreVersion"), str) else None,
        "MotionVersion": None,
        "Dpi": num_or_none(hist.get("Dpi")),
        "Fov": None,
        "Game": hist.get("Game") if isinstance(hist.get("Game"), str) else "",
        "TrackingFamily": None,
        "YawDegreesPerCount": GAME_YAW_PER_COUNT.get(str(hist.get("Game") or "").strip().lower()),
        "Seed": None,
        "Low": None,
        "High": None,
        "Modes": None,
        "Started": hist.get("Date") if isinstance(hist.get("Date"), str) else None,
        "ResumedFrom": None,
        "Legacy": False,
        "FromHistoryOnly": True,
    }


def scan_traces(traces_dir: str, history: dict) -> tuple[Scan, dict]:
    """流式扫描所有 trace，抽出 session / blockStart / sampleBatch / block / priorValidation。

    逐帧样本即时聚合成运动学特征后丢弃，因此内存占用与 300MB 无关。
    """
    scan = Scan()
    pending_kin: dict[tuple[str, str], dict] = {}   # (session, BlockId) -> kin
    pending_start: dict[tuple[str, str], dict] = {}  # (session, BlockId) -> blockStart
    blocks: dict[tuple[str, str], dict] = {}         # (session, BlockId) -> 块记录（含 Carried 标记）
    block_order: list[tuple[str, str]] = []
    prior_blocks: list[tuple[str, dict, str]] = []   # (trace_session_id, Result, 文件名)

    for path in iter_trace_files(traces_dir):
        scan.files += 1
        session_id = os.path.splitext(os.path.basename(path))[0]
        saw_session = False
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for lineno, line in enumerate(fh, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    record = json.loads(line)
                except Exception:
                    scan.bad_lines += 1
                    if len(scan.bad_line_examples) < 5:
                        scan.bad_line_examples.append(f"{os.path.basename(path)}:{lineno}")
                    continue
                if not isinstance(record, dict):
                    continue

                kind = record.get("Kind")

                # ---- 旧版格式：没有 Kind，用 Event（2026-10-02 的 ballsheet 时代记录）----
                if kind is None:
                    ev = record.get("Event")
                    scan.legacy_records[ev if isinstance(ev, str) else "<无Event>"] += 1
                    scan.legacy_files.add(os.path.basename(path))
                    if ev == "session" and session_id not in scan.sessions:
                        scan.sessions[session_id] = {
                            "SessionId": session_id, "TracePath": path, "SchemaVersion": 1,
                            "ScoreVersion": None, "MotionVersion": None,
                            "Dpi": num_or_none(record.get("Dpi")),
                            "Fov": None,
                            "Game": record.get("Game") if isinstance(record.get("Game"), str) else "",
                            "TrackingFamily": None,
                            "YawDegreesPerCount": GAME_YAW_PER_COUNT.get(str(record.get("Game") or "").strip().lower()),
                            "Seed": None, "Low": None, "High": None, "Modes": None,
                            "Started": record.get("Time") if isinstance(record.get("Time"), str) else None,
                            "ResumedFrom": None, "Legacy": True,
                        }
                        saw_session = True
                    continue

                if kind == "session":
                    scan.sessions[session_id] = session_from_record(record, session_id, path, history)
                    saw_session = True
                elif kind == "blockStart":
                    req = record.get("Request") or {}
                    bid = req.get("Id")
                    if isinstance(bid, str):
                        pending_start[(session_id, bid)] = {
                            "Script": int_or_none(record.get("Script")),
                            "Distance": num_or_none(record.get("Distance")),
                            "PathScale": num_or_none(record.get("PathScale")),
                            "BlockYaw": num_or_none(record.get("BlockYaw")),
                            "CameraYawAtStart": num_or_none(record.get("CameraYaw")),
                            "CameraPitchAtStart": num_or_none(record.get("CameraPitch")),
                            "BlockFov": num_or_none(record.get("Fov")),
                            "Aspect": num_or_none(record.get("Aspect")),
                            "Sensitivity": num_or_none(record.get("Sensitivity")),
                            "EDpi": num_or_none(record.get("EDpi")),
                            "Difficulty": record.get("Difficulty") if isinstance(record.get("Difficulty"), dict) else None,
                        }
                elif kind == "sampleBatch":
                    scan.sample_batches += 1
                    carried = bool(record.get("Carried"))
                    if carried:
                        scan.carried_batches += 1
                    samples = record.get("Samples")
                    if isinstance(samples, list) and samples:
                        scan.sampled_batches += 1
                        scan.total_frames += len(samples)
                        kin = aggregate_samples(samples)
                        bid = record.get("BlockId")
                        if isinstance(bid, str):
                            key = (session_id, bid)
                            pending_kin[key] = merge_kinematics(pending_kin.get(key), kin)
                elif kind == "block":
                    result = record.get("Result")
                    if not isinstance(result, dict):
                        scan.ignored_records["block 记录缺少 Result"] += 1
                        continue
                    req = (result.get("Block") or {}).get("Request") or {}
                    bid = req.get("Id")
                    if not isinstance(bid, str):
                        bid = f"__noid_{scan.block_records}"
                    scan.block_records += 1
                    key = (session_id, bid)
                    if key in blocks:
                        # 同一会话内同 Id 重复出现：保留先出现的那条
                        scan.ignored_records["同会话重复 BlockId（保留首个）"] += 1
                    else:
                        blocks[key] = {"Result": result, "Carried": False, "SourceFile": os.path.basename(path)}
                        block_order.append(key)
                elif kind == "priorValidation":
                    scan.prior_validation_records += 1
                    res = record.get("Result")
                    if isinstance(res, dict):
                        prior_blocks.append((session_id, res, os.path.basename(path)))
                elif kind == "interrupted":
                    scan.interrupted_records += 1
                else:
                    scan.ignored_records[f"记录类型 {kind}（与训练无关）"] += 1

        if not saw_session and session_id not in scan.sessions:
            fb = fallback_session(session_id, path, history)
            if fb is not None:
                scan.sessions[session_id] = fb

    return scan, {"blocks": blocks, "order": block_order, "kin": pending_kin,
                  "starts": pending_start, "prior": prior_blocks}


# --------------------------------------------------------------------------------------
# 块级行构建
# --------------------------------------------------------------------------------------

BLOCK_COLUMNS = [
    "BlockKey", "BlockId", "SessionId", "TraceSessionId", "CarriedResume", "SourceFile",
    "Mode", "ModeName", "Purpose", "PurposeName", "Cm360", "ScriptId",
    "DurationSeconds", "ExpectedShots",
    "Hits", "Misses", "Timeouts", "TrackingShare", "MeanAngularErrorRadii", "MeanShotSeconds",
    "Overshoots", "Corrections", "MeanOvershootRadii", "MeanTurnResponseSeconds",
    "MeanRecoverySeconds", "TurnSamples", "RecoverySamples",
    "Valid", "InvalidReasons", "Score", "Loss",
    "FailureLoss", "TimeLoss", "OffTargetLoss", "AngularErrorLoss",
    "ScoreVersion", "SessionScoreVersion",
    "SessionIdOwner", "Game", "Dpi", "Fov", "Sensitivity", "EDpi", "Seed", "MotionVersion",
    "Started", "SchemaVersion", "TrackingFamily",
    "BlockFov", "Distance", "PathScale", "BlockYaw", "SessionLow", "SessionHigh",
    "GameYawPerCount",
] + KIN_KEYS


def derive_sensitivity_edpi(session: dict, cm360: float | None) -> tuple[float | None, float | None]:
    dpi = session.get("Dpi")
    yaw = session.get("YawDegreesPerCount")
    if dpi and cm360 and yaw:
        sens = 360.0 * 2.54 / (dpi * cm360 * yaw)
        return sens, dpi * sens
    return None, None


def build_block_row(session: dict, trace_session_id: str, carried: bool,
                    result: dict, start: dict | None, kin: dict | None,
                    source_file: str, owner_session: str) -> dict:
    req = (result.get("Block") or {}).get("Request") or {}
    block = result.get("Block") or {}
    diag = result.get("Diagnostics") or {}
    breakdown = result.get("Breakdown") or {}

    mode = int_or_none(req.get("Mode"))
    purpose = int_or_none(req.get("Purpose"))
    cm360 = num_or_none(req.get("Cm360"))

    sens = None
    edpi = None
    if start:
        sens = start.get("Sensitivity")
        edpi = start.get("EDpi")
    if sens is None or edpi is None:
        ds, de = derive_sensitivity_edpi(session, cm360)
        sens = sens if sens is not None else ds
        edpi = edpi if edpi is not None else de

    score_version = result.get("ScoreVersion")
    if not isinstance(score_version, str) or not score_version:
        score_version = session.get("ScoreVersion")

    row = {
        "BlockKey": f"{owner_session}|{req.get('Id')}",
        "BlockId": req.get("Id"),
        "SessionId": owner_session,
        "TraceSessionId": trace_session_id,
        "CarriedResume": carried,
        "SourceFile": source_file,
        "Mode": mode,
        "ModeName": MODE_NAMES.get(mode, f"Mode{mode}"),
        "Purpose": purpose,
        "PurposeName": PURPOSE_NAMES.get(purpose, f"Purpose{purpose}"),
        "Cm360": cm360,
        "ScriptId": int_or_none(req.get("ScriptId")),
        "DurationSeconds": num_or_none(block.get("DurationSeconds")),
        "ExpectedShots": int_or_none(block.get("ExpectedShots")),
        "Hits": int_or_none(result.get("Hits")),
        "Misses": int_or_none(result.get("Misses")),
        "Timeouts": int_or_none(result.get("Timeouts")),
        "TrackingShare": num_or_none(result.get("TrackingShare")),
        "MeanAngularErrorRadii": num_or_none(result.get("MeanAngularErrorRadii")),
        "MeanShotSeconds": num_or_none(result.get("MeanShotSeconds")),
        "Overshoots": int_or_none(diag.get("Overshoots")),
        "Corrections": int_or_none(diag.get("Corrections")),
        "MeanOvershootRadii": num_or_none(diag.get("MeanOvershootRadii")),
        "MeanTurnResponseSeconds": num_or_none(diag.get("MeanTurnResponseSeconds")),
        "MeanRecoverySeconds": num_or_none(diag.get("MeanRecoverySeconds")),
        "TurnSamples": int_or_none(diag.get("TurnSamples")),
        "RecoverySamples": int_or_none(diag.get("RecoverySamples")),
        "Valid": bool(result.get("Valid")),
        "InvalidReasons": list(result.get("InvalidReasons") or []),
        "Score": num_or_none(result.get("Score")),
        "Loss": num_or_none(result.get("Loss")),
        "FailureLoss": num_or_none(breakdown.get("FailureLoss")),
        "TimeLoss": num_or_none(breakdown.get("TimeLoss")),
        "OffTargetLoss": num_or_none(breakdown.get("OffTargetLoss")),
        "AngularErrorLoss": num_or_none(breakdown.get("AngularErrorLoss")),
        "ScoreVersion": score_version,
        "SessionScoreVersion": session.get("ScoreVersion"),
        "SessionIdOwner": owner_session,
        "Game": session.get("Game") or "",
        "Dpi": session.get("Dpi"),
        "Fov": session.get("Fov"),
        "Sensitivity": sens,
        "EDpi": edpi,
        "Seed": session.get("Seed"),
        "MotionVersion": session.get("MotionVersion"),
        "Started": session.get("Started"),
        "SchemaVersion": session.get("SchemaVersion"),
        "TrackingFamily": session.get("TrackingFamily"),
        "BlockFov": start.get("BlockFov") if start else None,
        "Distance": start.get("Distance") if start else None,
        "PathScale": start.get("PathScale") if start else None,
        "BlockYaw": start.get("BlockYaw") if start else None,
        "SessionLow": session.get("Low"),
        "SessionHigh": session.get("High"),
        "GameYawPerCount": session.get("YawDegreesPerCount"),
    }
    if kin:
        row.update(kin)
    else:
        row.update({k: None for k in KIN_KEYS})
        row["KinSamples"] = 0
    return row


# --------------------------------------------------------------------------------------
# 候选级（session × cm/360 × ScoreVersion）聚合
#
# ★ 契约：下面的 26 个列名与顺序必须和 C# 侧
#   AimSense\MainWindow.xaml.cs -> CandidateFeatures() 完全一致，
#   否则 C# 影子模式推理会因为取不到同名特征而把整列按均值填充。
#   本函数逐条复刻 CandidateFeatures() 的语义（含"无数据就不填"的省略规则）。
# --------------------------------------------------------------------------------------

CANDIDATE_IDENT = [
    "CandidateKey", "SessionId", "Game", "ScoreVersion", "Started", "TrackingFamily",
    "Scripts", "Modes", "TraceSessionId", "VirtualCandidate",
]
CANDIDATE_EXTRA = [
    "CenterCm", "CenterCmSource", "SessionLow", "SessionHigh",
    "InvalidBlocks", "PilotBlocks", "SearchBlocks", "ValidationBlocks", "CarriedBlocks",
    # 以下 x_ 前缀列是本管线为自己留的离线探索特征：C# 侧不提供，禁止进模型。
    "x_WeightedScore", "x_ModeSpread", "x_KinBlocks", "x_KinCoverage", "x_KinMeanSpeed",
    "x_KinSpeedKurtosis", "x_KinDirectionReversalRate", "x_KinMeanAbsDx",
    "x_KinP95FrameDtMs", "x_KinPathLength",
]
CANDIDATE_LABELS = [
    "LabelFeel", "LabelFeelBinary", "LabelComparedToCurrent", "LabelBaselineCm360",
    "LabelScoreVersion", "LabelId", "LabelCreated",
    "PreferenceAsBetter", "PreferenceAsWorse", "MatchLabelCount", "MatchMeanRating",
]
CANDIDATE_COLUMNS = (CANDIDATE_IDENT + CONTRACT_FEATURES + CANDIDATE_EXTRA + CANDIDATE_LABELS)

def mean_of(rows: list[dict], key: str) -> float | None:
    vals = [r[key] for r in rows if num_or_none(r.get(key)) is not None]
    if not vals:
        return None
    return float(np.mean(np.asarray(vals, dtype=float)))


def center_cm_map(session_labels: list[dict]) -> dict[str, tuple[float, str]]:
    """从 "session" 标签（SessionSummaryLabel）里取会话开始时的当前档位 CenterCm。

    这是 C# 侧 _centerCm 的唯一落盘位置：trace 的 session 记录里没有 CenterCm 字段。
    """
    out: dict[str, tuple[float, str]] = {}
    for rec in session_labels:
        sid = rec.get("SessionId")
        cc = num_or_none(rec.get("CenterCm"))
        if isinstance(sid, str) and cc and cc > 0:
            out[sid] = (cc, "session标签.CenterCm")
    return out


def contract_features(rows: list[dict], cm: float, center_cm: float | None,
                      dpi: float | None, yaw: float | None) -> dict:
    """复刻 C# CandidateFeatures(results, cm, centerCm)。

    rows = 该 session、该 cm 下的全部块（已按 Pilot/ScoreVersion 过滤）。
    「该模式没有有效块就整项不填」是刻意的：C# 也是这么做的，模型用均值填充，
    这样训练与服务两侧的缺失模式一致。
    """
    f: dict = {name: None for name in CONTRACT_FEATURES}
    valid = [r for r in rows if r.get("Valid")]

    f["blocks"] = len(rows)
    f["validBlocks"] = len(valid)

    scores = [float(r["Score"]) for r in valid if num_or_none(r.get("Score")) is not None]
    if scores:
        arr = np.asarray(scores, dtype=float)
        f["meanScore"] = float(arr.mean())
        f["maxScore"] = float(arr.max())
        f["minScore"] = float(arr.min())
        # C# 用样本标准差（n-1），单块时为 0
        f["scoreStd"] = float(arr.std(ddof=1)) if arr.size > 1 else 0.0

    for mode, name in ((0, "clickScore"), (1, "trackScore"), (2, "movingClickScore")):
        mv = [float(r["Score"]) for r in valid
              if r.get("Mode") == mode and num_or_none(r.get("Score")) is not None]
        if mv:
            f[name] = float(np.mean(np.asarray(mv, dtype=float)))

    # C#: clickBlocks = 有效块中 Mode != Track 的（点击 + 移动点击）
    click_blocks = [r for r in valid if r.get("Mode") != 1]
    if click_blocks:
        hits = sum(int(r.get("Hits") or 0) for r in click_blocks)
        misses = sum(int(r.get("Misses") or 0) for r in click_blocks)
        timeouts = sum(int(r.get("Timeouts") or 0) for r in click_blocks)
        total = hits + misses + timeouts
        if total > 0:
            f["hitRate"] = hits / total
            f["missRate"] = misses / total
            f["timeoutRate"] = timeouts / total
        shots = [float(r["MeanShotSeconds"]) for r in click_blocks
                 if int(r.get("Hits") or 0) > 0 and num_or_none(r.get("MeanShotSeconds")) is not None]
        if shots:
            f["meanShotSeconds"] = float(np.mean(np.asarray(shots, dtype=float)))

    track_blocks = [r for r in valid if r.get("Mode") == 1]
    if track_blocks:
        f["trackingShare"] = mean_of(track_blocks, "TrackingShare")
        f["meanAngularError"] = mean_of(track_blocks, "MeanAngularErrorRadii")

    if valid:
        f["overshootsPerBlock"] = mean_of(valid, "Overshoots")
        f["correctionsPerBlock"] = mean_of(valid, "Corrections")
        f["meanOvershootRadii"] = mean_of(valid, "MeanOvershootRadii")
        turns = [r["MeanTurnResponseSeconds"] for r in valid
                 if num_or_none(r.get("MeanTurnResponseSeconds")) is not None]
        if turns:
            f["meanTurnResponse"] = float(np.mean(np.asarray(turns, dtype=float)))
        recoveries = [r["MeanRecoverySeconds"] for r in valid
                      if num_or_none(r.get("MeanRecoverySeconds")) is not None]
        if recoveries:
            f["meanRecovery"] = float(np.mean(np.asarray(recoveries, dtype=float)))

    f["cm360"] = cm
    f["logCm360"] = math.log(cm) if cm > 0 else None
    if center_cm and center_cm > 0:
        f["centerRatio"] = cm / center_cm
    if dpi and dpi > 0 and yaw:
        sens = 360.0 * 2.54 / (dpi * cm * yaw)
        f["dpi"] = dpi
        f["sensitivity"] = sens
        f["edpi"] = dpi * sens
    return f


def build_candidates(block_rows: list[dict], labels: dict[str, list[dict]],
                     sessions: dict[str, dict]) -> tuple[list[dict], dict]:
    # 按 session × ScoreVersion 分组，组内再按 cm 聚合（cm 用 0.5% 容差近似同一档位）
    by_session: dict[tuple[str, str], list[dict]] = collections.OrderedDict()
    for row in block_rows:
        sv = row.get("ScoreVersion") or "<未知>"
        by_session.setdefault((row["SessionId"], sv), []).append(row)

    feels = labels.get("feel", [])
    prefs = labels.get("preference", [])
    matches = labels.get("match", [])
    session_labels = labels.get("session", [])
    centers = center_cm_map(session_labels)

    feel_used: set[int] = set()
    candidates: list[dict] = []
    stats = {
        "ambiguous_feel": 0, "feel_version_blocked": 0, "virtual_candidates": 0,
        "center_from_label": 0, "center_from_range": 0, "center_unknown": 0,
    }

    def cluster_cms(rows: list[dict]) -> list[tuple[float, list[dict]]]:
        """把同一个 session 里近似的 cm 归成同一档位（与 LabelAnalysis.SameCm 同口径）。"""
        out: list[tuple[float, list[dict]]] = []
        for r in sorted(rows, key=lambda x: num_or_none(x.get("Cm360")) or 0.0):
            cm = num_or_none(r.get("Cm360"))
            if cm is None:
                continue
            if out and same_cm(out[-1][0], cm):
                out[-1][1].append(r)
            else:
                out.append((cm, [r]))
        return out

    for (session_id, score_version), rows in by_session.items():
        session = sessions.get(session_id, {})
        dpi = num_or_none(rows[0].get("Dpi"))
        # 注意：携带块的主人（ResumedFrom 源会话）通常已不在 traces 目录里，
        # 所以 YawDegreesPerCount 必须从块行上取（块行继承自已回退到续测会话的元数据）。
        yaw = num_or_none(rows[0].get("GameYawPerCount")) or num_or_none(session.get("YawDegreesPerCount"))
        low = num_or_none(rows[0].get("SessionLow"))
        high = num_or_none(rows[0].get("SessionHigh"))

        # CenterCm 来源优先级：会话标签快照 > sqrt(Low*High) 代理 > 未知
        if session_id in centers:
            center_cm, center_src = centers[session_id]
            stats["center_from_label"] += 1
        elif low and high and low > 0 and high > 0:
            center_cm, center_src = math.sqrt(low * high), "sqrt(Low*High)代理（无session标签）"
            stats["center_from_range"] += 1
        else:
            center_cm, center_src = None, "未知"
            stats["center_unknown"] += 1

        # 候选档位来自"有有效块"的 cm（与 C# LabelCandidates() 一致：只用有效块取档位），
        # 但该档位的 blocks 计数要把同 cm 的无效块也算进去（同样与 C# 一致）。
        valid_rows = [r for r in rows if r.get("Valid")]
        clusters = cluster_cms(valid_rows)
        clusters = [
            (cm, [r for r in rows if num_or_none(r.get("Cm360")) is not None
                  and same_cm(cm, num_or_none(r.get("Cm360")))])
            for cm, _ in clusters
        ]
        # C# 的 LabelCandidates() 会把 _centerCm 也当成候选，即使那一档没有有效块；
        # 这里只在"该 cm 真的有标签"时补一条虚拟候选，避免产出无人评价的空行。
        labeled_cms: list[float] = []
        for fl in feels:
            if isinstance(fl.get("SessionId"), str) and fl["SessionId"] == session_id:
                cm = num_or_none(fl.get("Cm360"))
                if cm is not None and not any(same_cm(cm, c) for c, _ in clusters) \
                        and not any(same_cm(cm, c) for c in labeled_cms):
                    labeled_cms.append(cm)

        for cm, cluster_rows in clusters:
            candidates.append(_build_candidate_row(
                session_id, score_version, cm, cluster_rows, center_cm, center_src,
                dpi, yaw, low, high, session, feels, prefs, matches, feel_used, stats,
                virtual=False, trace_session_id=rows[0].get("TraceSessionId")))
        for cm in labeled_cms:
            candidates.append(_build_candidate_row(
                session_id, score_version, cm, [], center_cm, center_src,
                dpi, yaw, low, high, session, feels, prefs, matches, feel_used, stats,
                virtual=True, trace_session_id=rows[0].get("TraceSessionId")))

    stats["feel_total"] = len(feels)
    stats["feel_matched"] = len(feel_used)
    stats["feel_without_candidate"] = len(feels) - len(feel_used)
    stats["preference_total"] = len(prefs)
    stats["match_total"] = len(matches)
    stats["session_label_total"] = len(session_labels)
    candidates.sort(key=lambda r: (r["SessionId"], r["ScoreVersion"], r["cm360"] or 0))
    return candidates, stats


def _build_candidate_row(session_id: str, score_version: str, cm: float, cluster_rows: list[dict],
                         center_cm: float | None, center_src: str, dpi: float | None, yaw: float | None,
                         low: float | None, high: float | None, session: dict,
                         feels: list[dict], prefs: list[dict], matches: list[dict],
                         feel_used: set[int], stats: dict, virtual: bool,
                         trace_session_id: str | None) -> dict:
    row: dict = {name: None for name in CONTRACT_FEATURES}
    row.update(contract_features(cluster_rows, cm, center_cm, dpi, yaw))

    valid = [r for r in cluster_rows if r.get("Valid")]
    kin_rows = [r for r in valid if (r.get("KinSamples") or 0) > 0]
    weights = MODE_WEIGHTS_TRACKING if cluster_rows and cluster_rows[0].get("TrackingFamily") \
        else MODE_WEIGHTS_TACTICAL
    mode_scores = {}
    for m in (0, 1, 2):
        mv = [r["Score"] for r in valid if r.get("Mode") == m and num_or_none(r.get("Score")) is not None]
        if mv:
            mode_scores[m] = float(np.mean(np.asarray(mv, dtype=float)))
    wsum = sum(weights[m] for m in mode_scores)
    weighted = (sum(weights[m] * mode_scores[m] for m in mode_scores) / wsum) if wsum > 0 else None

    row.update({
        "CandidateKey": f"{session_id}|{score_version}|{cm:.4f}",
        "SessionId": session_id,
        "Game": (cluster_rows[0].get("Game") if cluster_rows else session.get("Game")) or "",
        "ScoreVersion": score_version,
        "Started": cluster_rows[0].get("Started") if cluster_rows else session.get("Started"),
        "TrackingFamily": cluster_rows[0].get("TrackingFamily") if cluster_rows else session.get("TrackingFamily"),
        "Scripts": sorted({r.get("ScriptId") for r in cluster_rows if r.get("ScriptId") is not None}),
        "Modes": sorted({r.get("ModeName") for r in cluster_rows if r.get("ModeName")}),
        "TraceSessionId": trace_session_id,
        "VirtualCandidate": virtual,
        "CenterCm": center_cm,
        "CenterCmSource": center_src,
        "SessionLow": low,
        "SessionHigh": high,
        "InvalidBlocks": len(cluster_rows) - len(valid),
        "PilotBlocks": sum(1 for r in cluster_rows if r.get("Purpose") == 0),
        "SearchBlocks": sum(1 for r in cluster_rows if r.get("Purpose") == 1),
        "ValidationBlocks": sum(1 for r in cluster_rows if r.get("Purpose") == 2),
        "CarriedBlocks": sum(1 for r in cluster_rows if r.get("CarriedResume")),
        "x_WeightedScore": weighted,
        "x_ModeSpread": (max(mode_scores.values()) - min(mode_scores.values())) if len(mode_scores) > 1 else None,
        "x_KinBlocks": len(kin_rows),
        "x_KinCoverage": (len(kin_rows) / len(valid)) if valid else None,
        "x_KinMeanSpeed": mean_of(kin_rows, "KinMeanSpeed"),
        "x_KinSpeedKurtosis": mean_of(kin_rows, "KinSpeedKurtosis"),
        "x_KinDirectionReversalRate": mean_of(kin_rows, "KinDirectionReversalRate"),
        "x_KinMeanAbsDx": mean_of(kin_rows, "KinMeanAbsDx"),
        "x_KinP95FrameDtMs": mean_of(kin_rows, "KinP95FrameDtMs"),
        "x_KinPathLength": mean_of(kin_rows, "KinPathLength"),
    })
    for col in CANDIDATE_LABELS:
        row[col] = None

    # ---- feel 标签匹配：同 SessionId + 同 cm；标签自带 ScoreVersion 时必须同版本 ----
    hits: list[tuple[int, dict]] = []
    for i, fl in enumerate(feels):
        if not isinstance(fl.get("SessionId"), str) or fl["SessionId"] != session_id:
            continue
        if not same_cm(num_or_none(fl.get("Cm360")), cm):
            continue
        lsv = fl.get("ScoreVersion")
        if isinstance(lsv, str) and lsv and score_version != "<未知>" and lsv != score_version:
            stats["feel_version_blocked"] += 1
            continue
        hits.append((i, fl))
    if len(hits) > 1:
        stats["ambiguous_feel"] += 1
    if hits:
        idx, fl = sorted(hits, key=lambda x: str(x[1].get("Created") or ""))[-1]
        feel_used.add(idx)
        feel_val = int_or_none(fl.get("Feel"))
        row["LabelFeel"] = feel_val
        row["LabelFeelBinary"] = None if feel_val is None else (1 if feel_val >= 7 else 0)
        row["LabelComparedToCurrent"] = fl.get("ComparedToCurrent")
        row["LabelBaselineCm360"] = num_or_none(fl.get("BaselineCm360"))
        row["LabelScoreVersion"] = fl.get("ScoreVersion")
        row["LabelId"] = fl.get("Id")
        row["LabelCreated"] = fl.get("Created")

    better = worse = 0
    for pl in prefs:
        if not isinstance(pl.get("SessionId"), str) or pl["SessionId"] != session_id:
            continue
        if same_cm(num_or_none(pl.get("BetterCm360")), cm):
            better += 1
        if same_cm(num_or_none(pl.get("WorseCm360")), cm):
            worse += 1
    row["PreferenceAsBetter"] = better
    row["PreferenceAsWorse"] = worse

    game = (row["Game"] or "").strip().lower()
    matched = [ml for ml in matches
               if str(ml.get("Game") or "").strip().lower() == game
               and same_cm(num_or_none(ml.get("Cm360")), cm)]
    row["MatchLabelCount"] = len(matched)
    ratings = [num_or_none(ml.get("Rating")) for ml in matched]
    ratings = [r for r in ratings if r is not None]
    row["MatchMeanRating"] = float(np.mean(ratings)) if ratings else None
    if virtual:
        stats["virtual_candidates"] += 1
    return row

# 配对所需的最小有效块数：低于它的"更好/更差"判断本身是噪声，不该进训练集。
# 上游（App）只负责如实带 validBlocks，判断在这一层做。
MIN_PAIR_EVIDENCE = 3


def build_preference_pairs(candidates: list[dict], prefs: list[dict]) -> tuple[list[dict], dict]:
    by_session_cm: dict[str, list[dict]] = collections.defaultdict(list)
    for c in candidates:
        by_session_cm[c["SessionId"]].append(c)

    rows: list[dict] = []
    stats = {"usable": 0, "missing_side": 0, "session_unknown": 0, "low_evidence": 0}
    for i, pl in enumerate(prefs):
        session_id = pl.get("SessionId") if isinstance(pl.get("SessionId"), str) else None
        better = num_or_none(pl.get("BetterCm360"))
        worse = num_or_none(pl.get("WorseCm360"))
        pool = by_session_cm.get(session_id or "", [])
        b = next((c for c in pool if same_cm(c["cm360"], better)), None)
        w = next((c for c in pool if same_cm(c["cm360"], worse)), None)
        # 两侧都找齐只是"能配成对"，还必须**证据量够**才算可用样本：实测见过 2 块 vs 13 块的档位
        # 被当成同等证据（13 块那档 scoreStd=10.75，2 块那档只有 0.71）。
        found = b is not None and w is not None
        enough = found and (b["validBlocks"] or 0) >= MIN_PAIR_EVIDENCE and (w["validBlocks"] or 0) >= MIN_PAIR_EVIDENCE
        if enough:
            stats["usable"] += 1
        elif not found:
            stats["missing_side"] += 1
            if not pool:
                stats["session_unknown"] += 1
        else:
            stats["low_evidence"] += 1
        row = {
            "PairKey": f"{pl.get('Id')}",
            "LabelId": pl.get("Id"),
            "SessionId": session_id,
            "Game": pl.get("Game") if isinstance(pl.get("Game"), str) else "",
            "BetterCm360": better,
            "WorseCm360": worse,
            "Basis": pl.get("Basis"),
            "Note": pl.get("Note"),
            "Created": pl.get("Created"),
            "Usable": enough,
            "LowEvidence": found and not enough,
            "BetterCandidateKey": b["CandidateKey"] if b else None,
            "WorseCandidateKey": w["CandidateKey"] if w else None,
            "BetterWeightedScore": b["x_WeightedScore"] if b else None,
            "WorseWeightedScore": w["x_WeightedScore"] if w else None,
            "BetterValidBlocks": b["validBlocks"] if b else None,
            "WorseValidBlocks": w["validBlocks"] if w else None,
        }
        rows.append(row)
    return rows, stats


PREFERENCE_COLUMNS = [
    "PairKey", "LabelId", "SessionId", "Game", "BetterCm360", "WorseCm360", "Basis", "Note",
    "Created", "Usable", "BetterCandidateKey", "WorseCandidateKey",
    "BetterWeightedScore", "WorseWeightedScore", "BetterValidBlocks", "WorseValidBlocks",
]


# --------------------------------------------------------------------------------------
# 编排
# --------------------------------------------------------------------------------------

def run_pipeline(traces_dir: str, labels_file: str, out_dir: str, history_dir: str,
                 score_versions: set[str] | None, include_pilot: bool, include_prior: bool,
                 exclude_carried: bool, require_samples: bool, quiet: bool = False,
                 drop_invalid: bool = False) -> dict:
    t0 = time.time()
    os.makedirs(out_dir, exist_ok=True)

    history = read_history(history_dir)
    labels, label_problems = read_labels(labels_file)
    scan, bag = scan_traces(traces_dir, history)

    blocks = bag["blocks"]
    order = bag["order"]
    kin_map = bag["kin"]
    start_map = bag["starts"]

    # ---- 去重：resume 会话会把来源会话的块原样再发一遍 ----
    # 归属规则：carried 块的真正主人是 ResumedFrom 指向的源会话；同一 (主人, BlockId) 只保留一份。
    owners: dict[tuple[str, str], tuple[str, bool, str]] = {}
    for key in order:
        trace_session_id, block_id = key
        session = scan.sessions.get(trace_session_id, {})
        resumed_from = session.get("ResumedFrom")
        if isinstance(resumed_from, str) and resumed_from:
            owner = os.path.splitext(os.path.basename(resumed_from))[0]
            carried = True
        else:
            owner = trace_session_id
            carried = False
        owners[key] = (owner, carried, trace_session_id)

    excluded = collections.Counter()
    excluded_detail = collections.Counter()
    kept: list[dict] = []
    seen_owner_block: dict[tuple[str, str], str] = {}
    duplicate_examples: list[str] = []

    for key in order:
        trace_session_id, block_id = key
        owner, carried, _ = owners[key]
        result = blocks[key]["Result"]
        session = scan.sessions.get(owner) or scan.sessions.get(trace_session_id) or {}
        req = (result.get("Block") or {}).get("Request") or {}
        sv = result.get("ScoreVersion") or session.get("ScoreVersion") or "<未知>"
        purpose = int_or_none(req.get("Purpose"))
        valid = bool(result.get("Valid"))
        kin = kin_map.get(key) or kin_map.get((owner, block_id))

        # 排除判定（顺序即优先级，先命中的原因计入统计）
        reason = None
        if carried and exclude_carried:
            reason = "carried_resume_block（--exclude-carried 要求排除续测携带块）"
        elif carried:
            ob = (owner, block_id)
            if ob in seen_owner_block:
                reason = "carried_resume_duplicate（同一源会话块的重复副本）"
                if len(duplicate_examples) < 3:
                    duplicate_examples.append(f"{owner}|{block_id} 已在 {seen_owner_block[ob]} 出现")
            else:
                seen_owner_block[ob] = trace_session_id
        if reason is None and score_versions is not None and sv not in score_versions:
            reason = f"score_version_mismatch（ScoreVersion={sv}）"
        if reason is None and purpose == 0 and not include_pilot:
            reason = "pilot_purpose（Pilot 用途块，--include-pilot 可放宽）"
        if reason is None and not valid and drop_invalid:
            bad = "；".join(str(x) for x in (result.get("InvalidReasons") or [])) or "未给出原因"
            reason = "invalid_block（Valid=false，--drop-invalid 要求剔除）"
            excluded_detail[f"invalid: {bad}"] += 1
        elif reason is None and not valid:
            # ★ 刻意保留：C# CandidateFeatures() 的 blocks 计数包含无效块，
            #   只是无效块不参与任何分数/比例聚合。丢掉它们会让训练侧与服务侧口径不一致。
            bad = "；".join(str(x) for x in (result.get("InvalidReasons") or [])) or "未给出原因"
            excluded_detail[f"保留但不计分（invalid）: {bad}"] += 1
        if reason is None and require_samples and (kin is None or (kin.get("KinSamples") or 0) == 0):
            reason = "no_frame_samples（--require-samples 要求逐帧样本）"
        if reason is None and not session:
            reason = "no_session_metadata（既无 session 记录也无 history 兜底）"

        if reason is not None:
            excluded[reason.split("（")[0]] += 1
            continue

        row = build_block_row(session, trace_session_id, carried, result, start_map.get(key), kin,
                              blocks[key]["SourceFile"], owner)
        if not row.get("Game"):
            excluded_detail["块缺少 Game（session 记录缺失 Game 字段）"] += 1
        kept.append(row)

    # ---- priorValidation 块：默认排除（它属于"历史复核"，ExcludedFromCurrentReview=true）----
    if include_prior:
        prior_extra = 0
        for trace_session_id, res, source_file in bag.get("prior", []):
            req = (res.get("Block") or {}).get("Request") or {}
            sv = res.get("ScoreVersion") or (scan.sessions.get(trace_session_id) or {}).get("ScoreVersion") or "<未知>"
            if score_versions is not None and sv not in score_versions:
                excluded[f"score_version_mismatch（ScoreVersion={sv}）"] += 1
                continue
            if not bool(res.get("Valid")):
                excluded["invalid_block（Valid=false）"] += 1
                continue
            session = scan.sessions.get(trace_session_id) or {}
            if not session:
                excluded["no_session_metadata（既无 session 记录也无 history 兜底）"] += 1
                continue
            bid = req.get("Id") or f"__prior_{prior_extra}"
            row = build_block_row(session, trace_session_id, False, res, None, None,
                                  source_file, trace_session_id)
            row["BlockKey"] = f"{trace_session_id}|prior|{bid}"
            kept.append(row)
            prior_extra += 1
    else:
        excluded["prior_validation_block"] = scan.prior_validation_records
    excluded["interrupted_partial_block（无 Result 的中断块）"] = scan.interrupted_records

    no_sample_kept = sum(1 for r in kept if (r.get("KinSamples") or 0) == 0)

    candidates, candidate_stats = build_candidates(kept, labels, scan.sessions)
    pairs, pair_stats = build_preference_pairs(candidates, labels.get("preference", []))

    # ---- 落盘 ----
    out_blocks_jsonl = os.path.join(out_dir, "blocks.jsonl")
    out_blocks_csv = os.path.join(out_dir, "blocks.csv")
    out_cand_jsonl = os.path.join(out_dir, "candidates.jsonl")
    out_cand_csv = os.path.join(out_dir, "candidates.csv")
    out_pair_jsonl = os.path.join(out_dir, "preference_pairs.jsonl")
    out_pair_csv = os.path.join(out_dir, "preference_pairs.csv")
    out_manifest = os.path.join(out_dir, "manifest.json")
    out_report = os.path.join(out_dir, "export_report.txt")

    write_jsonl(out_blocks_jsonl, kept)
    write_csv(out_blocks_csv, BLOCK_COLUMNS, kept)
    write_jsonl(out_cand_jsonl, candidates)
    write_csv(out_cand_csv, CANDIDATE_COLUMNS, candidates)
    write_jsonl(out_pair_jsonl, pairs)
    write_csv(out_pair_csv, PREFERENCE_COLUMNS, pairs)

    manifest = {
        "FeatureVersion": FEATURE_VERSION,
        "ContractFeatures": list(CONTRACT_FEATURES),
        "ContractFeatureMissingRate": (
            {name: (sum(1 for c in candidates if num_or_none(c.get(name)) is None) / len(candidates))
             for name in CONTRACT_FEATURES} if candidates else {}
        ),
        "LabelSchemaVersion": LABEL_SCHEMA_VERSION,
        "DefaultScoreVersion": DEFAULT_SCORE_VERSION,
        "ScoreVersionFilter": sorted(score_versions) if score_versions else "all",
        "GeneratedAt": _dt.datetime.now().astimezone().isoformat(timespec="seconds"),
        "TracesDir": traces_dir,
        "LabelsFile": labels_file,
        "HistoryDir": history_dir,
        "TraceFiles": scan.files,
        "BlockRecordsRead": scan.block_records,
        "BlocksExported": len(kept),
        "BlocksWithoutFrames": no_sample_kept,
        "Sessions": len({r["SessionId"] for r in kept}),
        "Candidates": len(candidates),
        "PreferencePairs": len(pairs),
        "PreferencePairsUsable": pair_stats["usable"],
        "FrameSamplesScanned": scan.total_frames,
        "LegacyRecords": int(sum(scan.legacy_records.values())),
        "LegacyFiles": sorted(scan.legacy_files),
        "BadLines": scan.bad_lines,
        "IgnoredRecords": {k: v for k, v in scan.ignored_records.items()},
        "Labels": {k: len(v) for k, v in labels.items()},
        "Excluded": dict(excluded),
        "Options": {
            "include_pilot": include_pilot,
            "include_prior": include_prior,
            "exclude_carried": exclude_carried,
            "require_samples": require_samples,
            "drop_invalid": drop_invalid,
        },
        "BlockColumns": BLOCK_COLUMNS,
        "CandidateColumns": CANDIDATE_COLUMNS,
        "ReportPath": out_report,
    }
    with open(out_manifest, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, ensure_ascii=False, indent=2, sort_keys=True)

    report = render_report(scan, kept, candidates, pairs, excluded, excluded_detail,
                           candidate_stats, pair_stats, labels, label_problems,
                           no_sample_kept, duplicate_examples, manifest,
                           [out_blocks_jsonl, out_blocks_csv, out_cand_jsonl, out_cand_csv,
                            out_pair_jsonl, out_pair_csv, out_manifest, out_report], t0)
    with open(out_report, "w", encoding="utf-8") as fh:
        fh.write(report)

    if not quiet:
        print(report)

    return {
        "manifest": manifest,
        "report_path": out_report,
        "blocks": kept,
        "candidates": candidates,
        "pairs": pairs,
        "excluded": excluded,
    }


def render_report(scan: Scan, kept: list[dict], candidates: list[dict], pairs: list[dict],
                  excluded: collections.Counter, excluded_detail: collections.Counter,
                  candidate_stats: dict, pair_stats: dict, labels: dict, label_problems: list[str],
                  no_sample_kept: int, duplicate_examples: list[str], manifest: dict,
                  outputs: list[str], t0: float) -> str:
    L: list[str] = []
    add = L.append
    add("=" * 78)
    add("AimSense 离线导出报告（export_dataset.py）")
    add("=" * 78)
    add(f"生成时间：{manifest['GeneratedAt']}")
    add(f"trace 目录：{manifest['TracesDir']}")
    add(f"标签文件：{manifest['LabelsFile']}"
        + ("" if os.path.isfile(manifest['LabelsFile']) else "  ← 不存在（按 0 标签处理）"))
    add(f"history 目录：{manifest['HistoryDir']}")
    add(f"评分版本过滤：{manifest['ScoreVersionFilter']}")
    add("")
    add("-- 1. 读取规模 " + "-" * 62)
    add(f"trace 文件数：{scan.files}")
    add(f"新格式记录：block={scan.block_records}，sampleBatch={scan.sample_batches}"
        f"（其中携带块 {scan.carried_batches}，含逐帧样本 {scan.sampled_batches}），"
        f"priorValidation={scan.prior_validation_records}，interrupted={scan.interrupted_records}")
    add(f"逐帧样本总数：{scan.total_frames}")
    add(f"JSON 坏行数：{scan.bad_lines}" + (f"（示例：{', '.join(scan.bad_line_examples)}）" if scan.bad_line_examples else ""))
    if scan.legacy_records:
        kinds = "、".join(f"{k}×{v}" for k, v in sorted(scan.legacy_records.items(), key=lambda x: -x[1]))
        add(f"旧格式记录（\"Event\" 而非 \"Kind\"，未纳入训练）：{sum(scan.legacy_records.values())} 条：{kinds}")
        add(f"  涉及文件：{', '.join(sorted(scan.legacy_files))}")
    other = {k: v for k, v in scan.ignored_records.items() if "与训练无关" not in k}
    if other:
        add("其它被读取但未参与导出的记录：" + "；".join(f"{k}={v}" for k, v in sorted(other.items())))
    add("")
    add("-- 2. 导出结果 " + "-" * 62)
    add(f"块级样本：{len(kept)} 条，覆盖 session {manifest['Sessions']} 个")
    add(f"  其中缺少逐帧样本（无法算运动学特征）的块：{no_sample_kept} 条")
    add(f"候选档位（session × ScoreVersion × cm/360）：{len(candidates)} 条")
    add(f"偏好对：{len(pairs)} 条，其中两侧候选都存在、可用于 pairwise 训练：{pair_stats['usable']} 条")
    add("")
    by_sv = collections.Counter(r["ScoreVersion"] for r in kept)
    add("按 ScoreVersion 分布（默认不同版本不混算）：")
    for sv, n in sorted(by_sv.items()):
        add(f"  {sv}: {n} 块")
    by_mode = collections.Counter(r.get("ModeName") for r in kept)
    add("按 Mode 分布：" + "、".join(f"{k}={v}" for k, v in sorted(by_mode.items(), key=lambda x: str(x[0]))))
    by_purpose = collections.Counter(r.get("PurposeName") for r in kept)
    add("按 Purpose 分布：" + "、".join(f"{k}={v}" for k, v in sorted(by_purpose.items(), key=lambda x: str(x[0]))))
    by_game = collections.Counter(r.get("Game") or "<空>" for r in kept)
    add("按 Game 分布：" + "、".join(f"{k}={v}" for k, v in sorted(by_game.items())))
    add("")
    add("-- 3. 块级排除原因统计 " + "-" * 54)
    if excluded:
        for reason, n in excluded.most_common():
            add(f"  {reason}: {n}")
    else:
        add("  （无）")
    add("  说明：无效块（Valid=false）默认**保留在块级表里**，因为 C# CandidateFeatures() 的")
    add("        blocks 计数包含它们；它们只是不参与任何分数/比例聚合。")
    add("        用 --drop-invalid 可强制剔除，但那样训练侧与服务侧的 blocks 口径会不一致。")
    if excluded_detail:
        add("  细分原因：")
        for reason, n in excluded_detail.most_common():
            add(f"    {reason}: {n}")
    if duplicate_examples:
        add("  重复副本示例：" + "；".join(duplicate_examples))
        add("  说明：续测会话（session.ResumedFrom）会把来源会话的块重发一次；")
        add("        本脚本把携带块归回来源 session 并按 (来源session, BlockId) 去重，避免同一样本被算两遍。")
    add("")
    add("-- 4. 候选级契约特征（candidate-features-1，与 C# CandidateFeatures() 同名同义） " + "-" * 6)
    add(f"候选契约特征数：{len(CONTRACT_FEATURES)}；候选行数：{len(candidates)}")
    if candidates:
        add("各契约列的缺失率（C# 侧无数据时同样不填，由模型按训练均值填充）：")
        miss = []
        for name in CONTRACT_FEATURES:
            absent = sum(1 for c in candidates if num_or_none(c.get(name)) is None)
            miss.append((name, absent / len(candidates)))
        for name, rate in sorted(miss, key=lambda x: -x[1]):
            add(f"  {name:<22} 缺失 {rate * 100:5.1f}%")
        add("centerRatio 的 CenterCm 来源统计：")
        add(f"  session 标签快照（权威）：{candidate_stats.get('center_from_label', 0)} 个 session 分组")
        add(f"  sqrt(Low*High) 代理（trace session 记录里没有 CenterCm 字段）：{candidate_stats.get('center_from_range', 0)} 个")
        add(f"  完全未知（centerRatio 缺失）：{candidate_stats.get('center_unknown', 0)} 个")
        virtual = sum(1 for c in candidates if c.get("VirtualCandidate"))
        if virtual:
            add(f"  虚拟候选（有标签但该档位没有有效块，如会话起始档位）：{virtual} 条")
    add("")

    add("-- 5. 标签情况 " + "-" * 62)
    if not labels:
        add("标签文件不存在或不含任何记录：本次导出未附加任何人工标签。")
        add("这完全正常——新会话尚未跑过；scripts 不会因此失败。")
    else:
        for kind in ("feel", "preference", "match", "adoption", "retest"):
            add(f"  {kind}: {len(labels.get(kind, []))} 条")
        add(f"feel 标签匹配到候选：{candidate_stats.get('feel_matched', 0)}/{candidate_stats.get('feel_total', 0)}"
            f"（未匹配 {candidate_stats.get('feel_without_candidate', 0)}，"
            f"同一候选多条取最新 {candidate_stats.get('ambiguous_feel', 0)}，"
            f"因评分版本不同被挡下 {candidate_stats.get('feel_version_blocked', 0)}）")
        add(f"preference 标签：{candidate_stats.get('preference_total', 0)} 条；"
            f"两侧候选缺失 {pair_stats['missing_side']} 条（其中候选完全不存在 {pair_stats['session_unknown']} 条）")
        add(f"match（外部对局成绩）标签：{candidate_stats.get('match_total', 0)} 条，仅作补充列，不参与建模")
    for problem in label_problems[:10]:
        add(f"  [标签告警] {problem}")
    add("")
    add("-- 6. 数据质量提示 " + "-" * 58)
    if len(candidates) == 0:
        add("  · 没有任何候选档位：无法进入候选级建模。")
    if len(candidates) < 200:
        add(f"  · 候选样本只有 {len(candidates)} 条（<200）：train.py 会强制标注“数据不足，仅管线验证”。")
    if no_sample_kept and len(kept):
        add(f"  · {no_sample_kept}/{len(kept)} 个块的逐帧样本为空（续测携带块在来源 trace 中已丢失），"
            "运动学特征为缺失值，train.py 会用训练折均值填充。")
    if scan.legacy_records:
        add("  · 存在 2026-10-02 的旧版（SchemaVersion 1）trace：字段完全不同，被整体跳过，")
        add("    如需使用必须先写单独的转换器，不要假定它和 v2 同口径。")
    if by_sv and len(by_sv) > 1:
        add(f"  · 本次导出含 {len(by_sv)} 个评分版本；train.py 要求同一 ScoreVersion，请分批处理。")
    if labels.get("feel") and candidate_stats.get("feel_without_candidate", 0):
        add("  · 有 feel 标签没能匹配到任何候选：多半是标签的 SessionId/Cm360 与有效块对不上，请核对。")
    add("")
    add("-- 7. 训练/服务一致性提醒 " + "-" * 52)
    add("  · 本导出的候选特征刻意复刻 C# MainWindow.CandidateFeatures()：Pilot 块不进候选、")
    add("    无效块计入 blocks 但不计入各项分数、某模式没有有效块时该列留空。")
    add("    用 --include-pilot 会把 Pilot 块算进来，训练分布将与影子模式推理不一致。")
    add(f"  · 影子模式推理侧有三道闸门：OfflineModel.Load 校验 FeatureVersion，")
    add(f"    OfflineModel.CanPredict(scoreVersion) 校验 ScoreVersion，模型 JSON 的 Game 字段做游戏闸门")
    add(f"    （Game=\"ALL\" 或缺省时不做游戏闸门）。任一不匹配 C# 都会拒绝给出影子预测。")
    add(f"    本次导出只保留 {manifest['ScoreVersionFilter']}；若推理当次的评分版本不同，就不会有影子预测。")
    add("  · 标签跨多个游戏时，train.py 默认按游戏分别训练并各自输出模型文件；")
    add("    只有显式 --game ALL 才做跨游戏混合训练（并会在 Notes 里警示）。")
    add("  · x_ 前缀列是本管线自留的离线探索特征，C# 侧不提供，禁止放进模型。")
    add("")
    add("-- 8. 输出文件 " + "-" * 62)
    seen_paths: set[str] = set()
    for path in outputs + [manifest.get("ReportPath", "")]:
        if not path or not os.path.isfile(path):
            continue
        real = os.path.abspath(path)
        if real in seen_paths:
            continue
        seen_paths.add(real)
        add(f"  {real}  ({human_size(os.path.getsize(path))})")
    add("")
    add(f"耗时：{time.time() - t0:.2f} 秒")
    add("")
    add("诚实提醒：本报告只描述数据规模与分布，不构成任何模型有效性结论。")
    add("=" * 78)
    return "\n".join(L) + "\n"


# --------------------------------------------------------------------------------------
# 自检：内置合成数据
# --------------------------------------------------------------------------------------

def _synthetic_session_trace(session_id: str, dpi: float, game: dict, cm_values: list[float],
                             score_version: str, resumed_from: str | None,
                             seed: int, with_frames: bool = True) -> list[dict]:
    rng = np.random.default_rng(seed)
    recs: list[dict] = [{
        "Kind": "session", "SchemaVersion": 2, "ScoreVersion": score_version,
        "MotionVersion": "character-motion-1", "Dpi": dpi, "Fov": 90,
        "CharacterDistanceMeters": 6.0, "Game": game, "Seed": int(seed), "Low": 28.8, "High": 45.0,
        "Modes": [0, 1, 2], "Started": "2026-01-01T00:00:00+08:00", "ResumedFrom": resumed_from,
    }]
    if resumed_from:
        # 模拟续测：把来源会话的块原样重发（Carried=true + 空样本）
        for i in range(2):
            bid = f"Click-search-{i + 1}"
            recs.append({"Kind": "sampleBatch", "BlockId": bid, "Events": [], "Samples": [], "Carried": True})
            recs.append({"Kind": "block", "Result": _synthetic_block_result(bid, 0, 0, 1, cm_values[0], i % 3, rng, True)})
        return recs

    idx = 0
    for cm in cm_values:
        for mode in (0, 1, 2):
            for purpose in (0, 1):
                idx += 1
                bid = f"{MODE_NAMES[mode]}-{'pilot' if purpose == 0 else 'search'}-{idx}"
                script = idx % 3
                # 真实信号：cm/360 越接近"甜蜜点"32、手感越好，误差越小
                quality = math.exp(-((cm - 32.0) ** 2) / 300.0)
                recs.append({
                    "Kind": "blockStart",
                    "Request": {"Id": bid, "Mode": mode, "Purpose": purpose, "Cm360": cm, "ScriptId": script},
                    "Difficulty": {"SpeedScale": 1.0, "DistanceScale": 1.0, "Deadline": 1.2},
                    "Script": script, "Distance": 6.0, "PathScale": 1.0, "BlockYaw": 0.0,
                    "CameraYaw": 0.0, "CameraPitch": 0.0, "Fov": 90.0, "Aspect": 1.6,
                    "Sensitivity": 360 * 2.54 / (dpi * cm * game["YawDegreesPerCount"]),
                    "EDpi": dpi * 360 * 2.54 / (dpi * cm * game["YawDegreesPerCount"]),
                })
                samples = []
                if with_frames:
                    t = 0.0
                    n = 240
                    for k in range(n):
                        t += float(rng.normal(1 / 120.0, 0.002))
                        speed = (1.0 + 0.6 * quality) * float(abs(rng.normal(300, 90)))
                        samples.append({
                            "Tick": k, "Time": round(t, 5), "Phase": "Scoring" if t < 12 else "Preview",
                            "DX": float(rng.normal(0, speed * 0.01)), "DY": float(rng.normal(0, speed * 0.005)),
                            "CameraYaw": float(rng.normal(0, 5)), "CameraPitch": float(rng.normal(0, 2)),
                            "HeadX": 0.0, "HeadY": 1.7, "HeadZ": 6.0,
                            "RootX": 0.0, "RootY": 0.0, "RootZ": 6.0,
                            "Crouch": 0.0, "FacingYaw": 0.0, "GaitPhase": 0.0,
                        })
                recs.append({"Kind": "sampleBatch", "BlockId": bid, "Events": [
                    {"Kind": "begin", "Time": 0.0, "Deadline": 1.2},
                    {"Kind": "observe", "Time": 0.1, "Yaw": 0.0, "Pitch": 0.0, "TargetYaw": 0.0,
                     "TargetPitch": 0.0, "Radius": 0.01, "OnHead": True},
                    {"Kind": "shot", "Time": 0.5, "Hit": True, "Timeout": False},
                ], "Samples": samples})
                recs.append({"Kind": "block", "Result": _synthetic_block_result(bid, mode, purpose, script, cm, idx % 3, rng, quality > 0.5)})
    return recs


def _synthetic_block_result(bid: str, mode: int, purpose: int, script: int, cm: float,
                            script_id: int, rng, valid: bool) -> dict:
    if mode == 1:
        share = float(np.clip(rng.normal(0.62, 0.08), 0, 1))
        err = float(max(0.05, rng.normal(1.4, 0.3)))
        failure, time_loss, off, ang = 0.0, 0.0, 75 * (1 - share), 25 * min(err / 3, 1)
    else:
        hits = int(np.clip(round(rng.normal(5, 1)), 0, 6))
        timeouts = 6 - hits - int(rng.integers(0, 2))
        timeouts = max(0, timeouts)
        misses = max(0, 6 - hits - timeouts)
        ratio = float(np.clip(rng.normal(0.6, 0.15), 0, 1))
        share = float(np.clip(rng.normal(0.4, 0.1), 0, 1))
        err = float(max(0.05, rng.normal(2.0, 0.5)))
        failure, time_loss, off, ang = 75 * (1 - hits / 6), 25 * ratio, 0.0, 0.0
    loss = min(100.0, failure + time_loss + off + ang)
    return {
        "Block": {"Request": {"Id": bid, "Mode": mode, "Purpose": purpose, "Cm360": cm, "ScriptId": script_id},
                  "DurationSeconds": 12, "ExpectedShots": 6},
        "Breakdown": {"FailureLoss": failure, "TimeLoss": time_loss, "OffTargetLoss": off,
                      "AngularErrorLoss": ang, "Loss": loss, "Score": 100 - loss},
        "Valid": valid,
        "InvalidReasons": [] if valid else ["合成数据的无效块示例"],
        "Hits": hits if mode != 1 else 0,
        "Misses": misses if mode != 1 else 0,
        "Timeouts": timeouts if mode != 1 else 0,
        "TrackingShare": share,
        "MeanAngularErrorRadii": err,
        "MeanShotSeconds": float(abs(rng.normal(0.7, 0.1))),
        "Diagnostics": {"Overshoots": int(rng.integers(0, 4)), "Corrections": int(rng.integers(0, 3)),
                        "MeanOvershootRadii": float(abs(rng.normal(1.5, 0.5))),
                        "MeanTurnResponseSeconds": float(abs(rng.normal(0.2, 0.05))),
                        "MeanRecoverySeconds": float(abs(rng.normal(0.15, 0.04))),
                        "TurnSamples": int(rng.integers(0, 6)), "RecoverySamples": int(rng.integers(0, 6))},
        "ScoreVersion": "angular-block-loss-v3", "Score": 100 - loss, "Loss": loss,
    }


def make_synthetic_data(root: str) -> tuple[str, str, str]:
    traces = os.path.join(root, "traces")
    labels_dir = os.path.join(root, "labels")
    history = os.path.join(root, "history-v2")
    os.makedirs(traces, exist_ok=True)
    os.makedirs(labels_dir, exist_ok=True)
    os.makedirs(history, exist_ok=True)

    game_cs2 = {"Id": "CS2", "YawDegreesPerCount": 0.022, "TrackingFamily": False,
                "Source": "synthetic", "SourceVersion": "synthetic"}
    sessions = [f"2026010{i}-000000-synthetic00000000000000000000000{i}" for i in range(1, 5)]
    for i, sid in enumerate(sessions):
        recs = _synthetic_session_trace(sid, 800, game_cs2, [28.8, 32.0, 36.0],
                                        "angular-block-loss-v3", None, seed=100 + i)
        # 每个合成会话掺一条旧格式记录和一条 interrupted，验证容错与排除统计
        recs.insert(1, {"Event": "session", "Time": "2026-01-01T00:00:00+08:00", "Game": "CS2", "Dpi": 800})
        recs.insert(2, {"Event": "target", "Mode": "点击", "Round": 1, "Cm360": 30.0,
                        "Metrics": {"Loss": 10, "Hit": True, "DataValid": True}, "Samples": []})
        recs.append({"Kind": "interrupted", "BlockId": "Click-search-99", "Reason": "用户中断",
                     "Events": [], "Samples": []})
        with open(os.path.join(traces, f"{sid}.jsonl"), "w", encoding="utf-8") as fh:
            for r in recs:
                fh.write(json.dumps(r, ensure_ascii=False) + "\n")
        with open(os.path.join(history, f"{sid}.json"), "w", encoding="utf-8") as fh:
            json.dump({"SchemaVersion": 2, "ScoreVersion": "angular-block-loss-v3",
                       "Date": "2026-01-01T00:00:00+08:00", "Game": "CS2", "Dpi": 800,
                       "Report": "合成数据", "TracePath": os.path.join(traces, f"{sid}.jsonl")},
                      fh, ensure_ascii=False)

    # 续测会话：携带来源会话的块（来源 trace 故意不存在，模拟真实情况）
    resume_id = "20260105-000000-synthetic0000000000000000000000005"
    recs = _synthetic_session_trace(resume_id, 800, game_cs2, [30.0],
                                    "angular-block-loss-v3", resumed_from="20260106-000000-GONE.jsonl", seed=999)
    # 再补一个真正的重复副本：同一来源块在第二个续测会话里再出现一次
    resume2_id = "20260106-000000-synthetic0000000000000000000000006"
    recs2 = _synthetic_session_trace(resume2_id, 800, game_cs2, [30.0],
                                     "angular-block-loss-v3", resumed_from="20260106-000000-GONE.jsonl", seed=999)
    for sid, rr in ((resume_id, recs), (resume2_id, recs2)):
        with open(os.path.join(traces, f"{sid}.jsonl"), "w", encoding="utf-8") as fh:
            for r in rr:
                fh.write(json.dumps(r, ensure_ascii=False) + "\n")

    # 标签：feel + preference
    label_path = os.path.join(labels_dir, "labels-v1.jsonl")
    with open(label_path, "w", encoding="utf-8") as fh:
        created = 0
        for sid in sessions:
            for cm, feel, cmp_ in ((28.8, 5, "更差"), (32.0, 8, "更好"), (36.0, 6, "一样")):
                created += 1
                fh.write(json.dumps({
                    "Id": f"lab-{created}", "Kind": "feel", "LabelSchemaVersion": 1,
                    "Created": f"2026-01-0{min(9, created)}T10:00:00+08:00",
                    "SessionId": sid, "TracePath": os.path.join(traces, f"{sid}.jsonl"),
                    "Game": "CS2", "Dpi": 800, "Cm360": cm,
                    "Sensitivity": 360 * 2.54 / (800 * cm * 0.022),
                    "EDpi": 800 * 360 * 2.54 / (800 * cm * 0.022),
                    "Feel": feel, "ComparedToCurrent": cmp_, "BaselineCm360": 30.0,
                    "ScoreVersion": "angular-block-loss-v3", "Note": "合成标签",
                }, ensure_ascii=False) + "\n")
        n = 0
        for sid in sessions:
            for better, worse in ((32.0, 28.8), (36.0, 28.8)):
                n += 1
                fh.write(json.dumps({
                    "Id": f"pref-{n}", "Kind": "preference", "LabelSchemaVersion": 1,
                    "Created": "2026-01-09T10:00:00+08:00", "SessionId": sid, "Game": "CS2",
                    "BetterCm360": better, "WorseCm360": worse, "Basis": "合成", "Note": "",
                }, ensure_ascii=False) + "\n")
        # 会话候选快照：C# 侧 _centerCm（会话开始时的当前档位）只落在这里，
        # 是 centerRatio 的权威来源。
        for m, sid in enumerate(sessions):
            fh.write(json.dumps({
                "Id": f"summary-{m}", "Kind": "session", "LabelSchemaVersion": 1,
                "Created": "2026-01-09T11:00:00+08:00", "SessionId": sid,
                "TracePath": os.path.join(traces, f"{sid}.jsonl"), "Game": "CS2", "Dpi": 800,
                "ScoreVersion": "angular-block-loss-v3", "CenterCm": 30.0,
                "Candidates": [{"Cm360": 28.8, "Sensitivity": 1.0, "EDPi": 1000.0,
                                "MeanScore": 60.0, "ValidBlocks": 4,
                                "ModeScores": {"Click": 60.0}}],
            }, ensure_ascii=False) + "\n")
        fh.write('{"Kind":"feel","broken\n')  # 故意坏行，验证容错
    return traces, label_path, history


def self_test() -> int:
    print("=" * 78)
    print("export_dataset.py --self-test：使用内置合成数据在临时目录跑通全流程")
    print("=" * 78)
    with tempfile.TemporaryDirectory(prefix="aimsense-selftest-") as root:
        traces, labels, history = make_synthetic_data(root)
        out = os.path.join(root, "out")
        result = run_pipeline(traces, labels, out, history, {"angular-block-loss-v3"},
                              include_pilot=True, include_prior=False, exclude_carried=False,
                              require_samples=False)
        man = result["manifest"]
        print()
        print("自检断言：")
        checks = [
            ("块级行 > 0", man["BlocksExported"] > 0),
            ("候选 > 0", man["Candidates"] > 0),
            ("偏好对可用 > 0", man["PreferencePairsUsable"] > 0),
            ("旧格式记录被识别并跳过", man["LegacyRecords"] > 0),
            ("续测携带块被去重", any("duplicate" in k for k in man["Excluded"])),
            ("逐帧样本被聚合", man["FrameSamplesScanned"] > 0),
            ("输出目录含 blocks.jsonl", os.path.isfile(os.path.join(out, "blocks.jsonl"))),
            ("输出目录含 candidates.jsonl", os.path.isfile(os.path.join(out, "candidates.jsonl"))),
            ("输出目录含 export_report.txt", os.path.isfile(os.path.join(out, "export_report.txt"))),
            ("未写入 %LOCALAPPDATA%\\AimSense",
             not os.path.abspath(out).lower().startswith(
                 os.path.join(local_appdata(), "AimSense").lower())),
        ]
        ok = True
        for name, good in checks:
            print(f"  [{'通过' if good else '失败'}] {name}")
            ok = ok and good
        print()
        if ok:
            print("自检结果：全部通过（退出码 0）")
            return 0
        print("自检结果：存在失败项（退出码 2）")
        return 2


# --------------------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------------------

def parse_args(argv: list[str]) -> argparse.Namespace:
    p = argparse.ArgumentParser(
        description="AimSense 离线数据导出：trace JSONL + 人工标签 -> 块级 / 候选级特征表",
        formatter_class=argparse.RawDescriptionHelpFormatter)
    here = os.path.dirname(os.path.abspath(__file__))
    p.add_argument("--traces", default=default_traces_dir(), help="trace JSONL 目录（只读）")
    p.add_argument("--labels", default=default_labels_file(), help="标签 JSONL 文件（可不存在）")
    p.add_argument("--history", default=default_history_dir(), help="history-v2 目录（补齐 session 元数据）")
    p.add_argument("--out", default=os.path.join(here, "out"), help="输出目录")
    p.add_argument("--score-version", default=DEFAULT_SCORE_VERSION,
                   help=f"只保留该评分版本的块；传 all 表示不过滤（默认 {DEFAULT_SCORE_VERSION}）")
    p.add_argument("--include-pilot", action="store_true", help="纳入 Pilot 用途块（默认排除）")
    p.add_argument("--include-prior", action="store_true", help="纳入 priorValidation 历史复核块（默认排除）")
    p.add_argument("--exclude-carried", action="store_true",
                   help="排除续测携带块（默认：归回来源 session 并去重后纳入）")
    p.add_argument("--require-samples", action="store_true", help="排除没有逐帧样本、算不出运动学特征的块")
    p.add_argument("--self-test", action="store_true", help="用内置合成数据在临时目录跑通")
    return p.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.self_test:
        return self_test()

    versions = None if str(args.score_version).strip().lower() == "all" \
        else {v.strip() for v in str(args.score_version).split(",") if v.strip()}

    if not os.path.isdir(args.traces):
        print(f"错误：trace 目录不存在：{args.traces}", file=sys.stderr)
        return 1

    result = run_pipeline(args.traces, args.labels, args.out, args.history, versions,
                          args.include_pilot, args.include_prior, args.exclude_carried,
                          args.require_samples)
    man = result["manifest"]
    print(f"导出完成：{os.path.abspath(os.path.join(args.out, 'candidates.jsonl'))}")
    print(f"块 {man['BlocksExported']} · session {man['Sessions']} · 候选 {man['Candidates']} "
          f"· 偏好对 {man['PreferencePairs']}（可用 {man['PreferencePairsUsable']}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
