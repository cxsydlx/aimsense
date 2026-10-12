# AimSense 离线机器学习数据管线（`ml/`）

这是 AimSense（人物瞄准灵敏度校准器）的离线数据管线：把 WPF 应用写出的 **trace JSONL** 与
人工标签合成为可训练的特征表，并用**纯 numpy** 训练一个候选档位级的模型，供 C# 侧做
**影子模式推理**（只输出一行预测供对比，绝不参与计分、搜索或推荐）。

> 本目录只包含 Python 脚本与文档，**不修改任何 `.cs` / `.xaml`**，也**不写入**
> `%LOCALAPPDATA%\AimSense` 下的任何文件——唯一的例外是 `train.py` 在满足严格条件时
> 把训练好的模型复制到 `%LOCALAPPDATA%\AimSense\model\preference-v1.json`（见下文
> [发布策略](#发布策略)），且该行为可以被 `--no-publish` 关闭。

---

## 1. 数据来源

| 来源 | 路径 | 说明 |
| --- | --- | --- |
| trace | `%LOCALAPPDATA%\AimSense\traces\*.jsonl` | 每次会话一个文件，JSONL，**只读** |
| 标签 | `%LOCALAPPDATA%\AimSense\labels\labels-v1.jsonl` | append-only JSONL，**可能尚不存在**（0 标签也能跑通） |
| 历史 | `%LOCALAPPDATA%\AimSense\history-v2\*.json` | 会话报告摘要；用于在 trace 缺少 `session` 记录时补齐 Game/Dpi |

### trace 里的记录类型（`Kind` 字段）

| `Kind` | 数量级（当前真实数据） | 用途 |
| --- | --- | --- |
| `session` | 每文件 1 条 | Dpi / Fov / Game / Seed / **Low / High** / Modes / Display |
| `blockStart` | 每计分块 1 条 | Request、Difficulty、Script、Sensitivity、eDPI |
| `sampleBatch` | 每块 1 条 | `Events`（begin/observe/shot/invalidate）+ `Samples`（逐帧） |
| `block` | 每块 1 条 | **`Result`：块级成绩与诊断量，训练的主表** |
| `priorValidation` | 少量 | 历史复核块，`ExcludedFromCurrentReview=true`，默认排除 |
| `interrupted` | 少量 | 没有 `Result` 的中断块，无法使用 |
| `frame` / `phase` / `report` / `displayVerified` / `reviewConsent` / `resumeConsent` / `resumeDifficulty` | 大量 | 性能与流程元数据，与训练无关 |

逐帧样本 `Samples[]` 的字段是：
`Tick, Time, Phase, DX, DY, CameraYaw, CameraPitch, HeadX/Y/Z, RootX/Y/Z, Crouch, FacingYaw, GaitPhase`。
（**不是** `Yaw/TargetYaw/Radius`——那些在 `Events` 里。原始描述在这里与实际不符。）

### 标签类型（`Kind` 字段）

| `Kind` | 关键字段 | 本管线的用途 |
| --- | --- | --- |
| `feel` | `SessionId, Cm360, Feel(0–10), ComparedToCurrent, BaselineCm360, ScoreVersion` | **主训练标签**（`Feel >= 7` 二分类） |
| `preference` | `SessionId, BetterCm360, WorseCm360, Basis` | 成对偏好（Bradley-Terry pairwise logistic） |
| `session` | `SessionId, CenterCm, Candidates[]` | **`centerRatio` 的分母来源**（C# 的 `_centerCm` 只落在这里） |
| `match` | `Game, Cm360, Rating, HeadshotRate, ...` | 真实对局成绩，外部标签，仅作补充列 |
| `adoption` / `retest` | 采用档位 / 复测差值 | 目前未进入模型，仅导出计数 |

### 标签匹配口径

* `feel` 匹配候选：同 `SessionId` + 同 `cm/360`（0.5% 相对容差，与 `LabelAnalysis.CmTolerance` 一致），
  且标签自带 `ScoreVersion` 时必须与候选同版本。
* 同一候选有多条 `feel` 时取 `Created` 最新的一条，并计数上报。
* `preference` 的更好/更差两侧都必须能在同一 session 里找到候选，否则该对标记为不可用。

---

## 2. 输出文件

两个脚本的输出都在 `ml/out/`（可用 `--out` 改）：

| 文件 | 内容 |
| --- | --- |
| `out/blocks.jsonl` / `.csv` | **块级特征表**：每块一行。含 `Result` 全部字段 + 会话元数据 + 从逐帧样本聚合出的运动学特征 |
| `out/candidates.jsonl` / `.csv` | **候选档位级特征表**：每 `session × cm/360 × ScoreVersion` 一行，含 26 个契约特征与可用标签 |
| `out/preference_pairs.jsonl` / `.csv` | **偏好对表**：每条 `preference` 标签一行，标注两侧候选是否齐全可用 |
| `out/manifest.json` | 本次导出的配置、规模、排除原因、契约特征缺失率；`train.py` 会读它 |
| `out/export_report.txt` | 人类可读的中文导出报告 |
| `out/model/preference-v1.json` | 训练出的模型（C# 影子模式读取的格式） |
| `out/model/preference-v1.report.txt` | 人类可读的中文训练报告 |
| `out/model/preference-pairwise-v1.json` | `--target preference` 的成对模型（单独文件名，避免覆盖上面那个） |
| `out/model/preference-v1-<game>.json` | 标签跨游戏时**按游戏分别训练**出的模型 |

CSV 使用 `utf-8-sig`，Excel 直接打开中文表头不乱码。JSONL 每行一个 JSON 对象。

---

## 3. 字段含义

### 3.1 块级表（`blocks`）

标识与来源：`BlockKey, BlockId, SessionId, TraceSessionId, CarriedResume, SourceFile`

块定义：`Mode`(0=Click,1=Track,2=MovingClick), `ModeName`, `Purpose`(0=Pilot,1=Search,2=Validation),
`PurposeName`, `Cm360`, `ScriptId`, `DurationSeconds`, `ExpectedShots`

成绩（来自 `Result`）：`Hits, Misses, Timeouts, TrackingShare, MeanAngularErrorRadii, MeanShotSeconds,
Overshoots, Corrections, MeanOvershootRadii, MeanTurnResponseSeconds, MeanRecoverySeconds,
TurnSamples, RecoverySamples, Valid, InvalidReasons, Score, Loss, FailureLoss, TimeLoss,
OffTargetLoss, AngularErrorLoss, ScoreVersion`

会话元数据：`Game, Dpi, Fov, Sensitivity, EDpi, Seed, MotionVersion, Started, SchemaVersion,
TrackingFamily, SessionLow, SessionHigh, GameYawPerCount, BlockFov, Distance, PathScale, BlockYaw`

运动学（从逐帧样本流式聚合，**不保留原始帧**）：

| 列 | 含义 |
| --- | --- |
| `KinSamples` | 参与聚合的样本数 |
| `KinScoringShare` | 处于 `Phase == "Scoring"` 的样本占比 |
| `KinMeanFrameDtMs` / `KinP95FrameDtMs` | 平均 / 95 分位帧间隔（毫秒） |
| `KinMeanAbsDx` / `KinMeanAbsDy` / `KinMaxAbsDx` | 平均 / 最大 `|DX|`、平均 `|DY|`（鼠标计数） |
| `KinDirectionReversals` / `KinDirectionReversalRate` | DX 符号翻转次数 / 每秒翻转次数 |
| `KinPathLength` | `Σ hypot(DX, DY)`（鼠标计数） |
| `KinAngularPathRad` | `Σ |wrap(ΔCameraYaw)| + |ΔCameraPitch|`（弧度） |
| `KinMeanSpeed` / `KinP95Speed` | 平均 / 95 分位速度（计数/秒） |
| `KinSpeedKurtosis` | 速度分布超额峰度（正态为 0，越大越"爆发集中"） |
| `KinDurationSeconds` | 样本首尾时间跨度（秒） |

### 3.2 候选级表（`candidates`）— **26 个契约特征**

**这 26 个列名与顺序必须与 C# 侧完全一致**，否则影子模式推理取不到值、整列会被按均值填充：

```
blocks, validBlocks, meanScore, maxScore, minScore, scoreStd,
clickScore, trackScore, movingClickScore,
hitRate, timeoutRate, missRate, trackingShare, meanAngularError, meanShotSeconds,
overshootsPerBlock, correctionsPerBlock, meanOvershootRadii, meanTurnResponse, meanRecovery,
cm360, logCm360, centerRatio, dpi, sensitivity, edpi
```

该口径在 `AimSense\MainWindow.xaml.cs -> CandidateFeatures()` 里逐条复刻：

| 特征 | 口径 |
| --- | --- |
| `blocks` | 该 cm 上的**全部**块数（**含无效块、不含 Pilot**）——刻意如此，见下文"与直觉相反的两点" |
| `validBlocks` | 其中 `Valid == true` 的块数 |
| `meanScore` / `maxScore` / `minScore` / `scoreStd` | 有效块 `Score` 的均值 / 最大 / 最小 / **样本**标准差（单块时为 0） |
| `clickScore` / `trackScore` / `movingClickScore` | 该模式**有效块**的平均分；**该模式没有有效块时整列留空（缺失）** |
| `hitRate` / `missRate` / `timeoutRate` | 非 Track 模式有效块的 `Σhits / Σ(hits+misses+timeouts)` 等；无点击类块时缺失 |
| `meanShotSeconds` | 非 Track 有效块中 `Hits > 0` 的 `MeanShotSeconds` 均值（秒） |
| `trackingShare` / `meanAngularError` | Track 有效块的平均贴靶比例 / 平均角误差（单位：头部角半径） |
| `overshootsPerBlock` / `correctionsPerBlock` / `meanOvershootRadii` | 有效块的诊断量均值（`meanOvershootRadii` 单位为头部角半径） |
| `meanTurnResponse` / `meanRecovery` | 有该诊断量的有效块均值，**单位为秒** |
| `cm360` / `logCm360` | 档位与其自然对数 |
| `centerRatio` | `cm360 / CenterCm`。`CenterCm` 是会话开始时的当前档位，**只存在于 `session` 标签快照**里；没有该标签时回退为 `sqrt(Low*High)`（Pilot 档位中心）作为代理，二者可能有轻微偏差 |
| `dpi` / `sensitivity` / `edpi` | `eDPI = DPI × sensitivity`，`sensitivity = 360×2.54/(DPI×cm360×YawDegreesPerCount)`；缺少游戏系数时三项一起缺失 |

非契约的辅助列（**不得进入模型**）：

* `CandidateKey`（`session|ScoreVersion|cm360`）、`Game`、`ScoreVersion`、`Started`、`TrackingFamily`、
  `Scripts`、`Modes`、`TraceSessionId`、`VirtualCandidate`
* `CenterCm`、`CenterCmSource`、`SessionLow/High`、`InvalidBlocks`、`PilotBlocks`、`SearchBlocks`、
  `ValidationBlocks`、`CarriedBlocks`
* `x_*` 前缀：`x_WeightedScore`、`x_ModeSpread`、`x_Kin*`。C# 侧**提供不了**这些，
  所以模型里绝不能出现它们
* 标签列：`LabelFeel`、`LabelFeelBinary`、`LabelComparedToCurrent`、`LabelBaselineCm360`、
  `LabelScoreVersion`、`LabelId`、`LabelCreated`、`PreferenceAsBetter/Worse`、
  `MatchLabelCount`、`MatchMeanRating`

### 3.3 模型 JSON

```json
{
  "ModelVersion": "preference-logreg-v1",
  "LabelSchemaVersion": 1,
  "FeatureVersion": "candidate-features-1",
  "ScoreVersion": "angular-block-loss-v3",
  "Game": "CS2",
  "Target": "Feel>=7",
  "Features": ["...26 个契约特征名..."],
  "Mean": ["..."], "Scale": ["..."], "Weights": ["..."],
  "Bias": 0.0, "TrainedAt": "ISO8601",
  "Samples": 0, "Groups": 0, "Auc": 0.0,
  "Notes": "..."
}
```

* `Features` / `Mean` / `Scale` / `Weights` **长度严格一致且顺序对应**（脚本内有断言）。
* 预测：`p = sigmoid(Bias + Σ wᵢ·(xᵢ - Meanᵢ)/Scaleᵢ)`，缺失特征回填 `Meanᵢ`
  （C# `OfflineModel.Predict` 就是这么算的，缺失数量会在报告里显示）。
* **三项闸门**：`OfflineModel.Load` 校验 `FeatureVersion`，
  `OfflineModel.CanPredict(scoreVersion)` 校验 `ScoreVersion`，
  `Game` 字段是游戏闸门（`"ALL"` 或缺省 = 不做游戏闸门）。
* `"ALL"` 只在显式 `--game ALL` 时才会写出，并在 `Notes` 里写明"跨游戏训练，预测需谨慎"。

---

## 4. 如何运行

环境：Windows，`python`（本机为 Python 3.14）。**只需要 numpy**；本机 `python` 没有 pandas，
所以两个脚本都不依赖 pandas（CSV 用标准库 `csv` 写，`utf-8-sig` 编码）。不需要联网装包。

```powershell
cd C:\Users\19171\Desktop\Ofiles\develop\AimSense\ml

# 1) 导出（对真实 trace，约 300 MB，实测 ~3 秒，流式解析）
python export_dataset.py

# 2) 训练（真实数据；没有标签时会正常退出并说明原因，退出码 0）
python train.py

# 3) 管线自证：合成数据跑通全流程，不碰真实数据
python export_dataset.py --self-test
python train.py --self-test
```

### `export_dataset.py` 开关

| 开关 | 默认 | 作用 |
| --- | --- | --- |
| `--traces DIR` | `%LOCALAPPDATA%\AimSense\traces` | trace 目录（只读） |
| `--labels FILE` | `%LOCALAPPDATA%\AimSense\labels\labels-v1.jsonl` | 标签文件，可不存在 |
| `--history DIR` | `%LOCALAPPDATA%\AimSense\history-v2` | 补齐 session 元数据 |
| `--out DIR` | `ml/out` | 输出目录 |
| `--score-version V` | `angular-block-loss-v3` | 只保留该评分版本；`all` = 不过滤。**不同版本默认不混算** |
| `--include-pilot` | 关 | 纳入 Pilot 块。**会破坏与 C# 推理的口径一致性**，仅供离线探索 |
| `--include-prior` | 关 | 纳入 `priorValidation` 历史复核块 |
| `--exclude-carried` | 关 | 丢弃续测携带块（默认是归回来源 session 并去重后纳入） |
| `--require-samples` | 关 | 丢弃没有逐帧样本的块 |
| `--drop-invalid` | 关 | 丢弃无效块（默认保留，因为 C# 的 `blocks` 计数包含它们） |
| `--self-test` | — | 用内置合成数据在临时目录跑通并做断言 |

### `train.py` 开关

| 开关 | 默认 | 作用 |
| --- | --- | --- |
| `--target feel\|preference` | `feel` | `Feel >= 7` 二分类 / 成对偏好 |
| `--game auto\|ALL\|<Id>` | `auto` | `auto` = 按标签涉及的游戏决定；跨游戏时**按游戏分别训练** |
| `--folds N` | 5 | 分组交叉验证折数 |
| `--seed N` | 20260101 | 折分与合成数据随机种子 |
| `--l2` / `--lr` / `--epochs` | 1.0 / 0.5 / 3000 | 手写梯度下降超参 |
| `--min-samples` / `--min-groups` | 200 / 10 | "数据足够"的判定阈值 |
| `--model-file` / `--report-file` | — | 自定义输出路径 |
| `--no-publish` | — | 禁止把模型复制到 `%LOCALAPPDATA%\AimSense\model\` |
| `--self-test` | — | 合成数据端到端跑通（含跨游戏、数据不足、零标签分支） |

### 发布策略

只有**同时满足**全部条件，`train.py` 才会把模型复制到 C# 读取路径
`%LOCALAPPDATA%\AimSense\model\preference-v1.json`（只复制这一个文件，并自动建目录）：

1. `--target feel`（C# 影子模式只消费 `Feel>=7` 模型）；
2. 用的是**真实标签**（`--self-test` / 合成数据永远拒绝发布）；
3. 样本量 ≥ `--min-samples` 且 session 数 ≥ `--min-groups`（即**不是**"数据不足"状态）；
4. 本次是单个模型（跨游戏分别训练出的 `preference-v1-<game>.json` 不发布——
   C# 侧目前只有单一读取路径，没有按游戏选择模型的逻辑）。

任何一条不满足，脚本都会在报告里明确写出**未复制**及原因。占位模型（标签数为 0、`Weights` 全为 0）
永远不会被复制过去。

---

## 5. 诚实的局限

**这些局限是这份管线的核心结论，请先读它们再看任何指标。**

1. **当前标签量必然很小。** 用户刚开始采集，标签文件很可能还不存在。此时
   `train.py` 会输出 `Samples: 0`、`Weights` 全 `0` 的**占位模型**，并在报告与 `Notes` 里写明
   "数据不足，仅管线验证，不可用于结论"。这不是失败，是诚实的空结果。

2. **样本量 / 分组数的硬门槛。** 样本 < 200 或 session < 10 时，报告里会打上
   `数据不足，仅管线验证，不可用于结论` 的横幅，并且**不会**出现任何"模型有效"的说法。
   折间的 AUC 只是"代码能跑通"的自证。

3. **主观手感标签有噪声。** `Feel` 是 0–10 的自评整数，同一档位重测可能给出不同分数；
   用 `>= 7` 一刀切会把这种噪声变成标签翻转，样本越少影响越大。
   另外手感评价有时间漂移：训练适应度会随日期变化，早期和晚期的标签不可直接比较。

4. **评分自指泄漏风险（很重要）。** `Score` / `Loss` 是轨迹特征（贴靶比例、角度误差、
   过冲、恢复时间……）的**确定性函数**，与候选特征同源同批。
   用 `Score` 相关特征去预测 `Feel`，模型很可能只是在复述评分公式，而不是在解释"手感"。
   **绝不能用 `Score` 当标签**——那是自证，不是学习。
   真正有信息量的目标是**外部标签**：`preference`（成对偏好）、`match`（真实对局成绩）、
   `adoption` / `retest`（纵向留存）。本管线已把 `preference` 实现为独立目标。

5. **混杂因素。** `cm/360` 与 eDPI、游戏、DPI、显示条件、训练适应度都相关。
   只有**同一 session 内交错测试**的档位之间才有可比的对照关系；
   跨 session 横向比较会把"档位差异"和"当天状态差异"混在一起。

6. **过拟合。** 26 个契约特征，而样本可能只有几十条。逻辑回归在这个维度下必然过拟合，
   折间 AUC 波动会非常大，单折的高 AUC 不能当作发现。

7. **分组交叉验证只能去掉一部分泄漏。** 已按 `session` 分组（同一 session 的候选绝不跨折），
   但所有 session 仍来自同一个用户、同一台机器、相近的时期，不是独立同分布。

8. **必须先攒够数据再谈模型。** 现实的门槛是：**数百条有标签候选 + ≥ 10 个独立 session**。
   在那之前，本目录的价值是"管线可复现、口径可对齐"，而不是"模型可用"。

9. **`centerRatio` 有轻微训练/服务偏差。** 推理时 C# 用的是内存里的真实 `_centerCm`；
   训练时该值只可能来自 `session` 标签快照，缺失时退化为 `sqrt(Low*High)` 代理。
   报告里会统计三种来源各有多少个 session。

10. **影子模式不会改变任何推荐。** C# 侧的预测只写进报告末尾一行，供对比与积累，
    不参与计分、搜索和推荐。这是刻意的：模型没被验证之前，不能影响产品行为。

---

## 6. 与原始描述不一致的地方（实测发现）

调查真实数据（23 个 trace / 约 300 MB / 124 个 block 记录）时发现以下几处与最初假设不符，
脚本已按**实际情况**处理：

1. **存在两种完全不同的 trace 格式。** 2026-10-02 的两个大文件（合计约 171 MB）用的是
   **旧格式**：顶层字段是 `Event` 而不是 `Kind`，值为 `session` / `round` / `difficulty` / `target`，
   逐帧样本字段是 `TimeMs / X / Y / TargetX / TargetY / Radius / RawDX / RawDY / Raw`。
   共 555 条记录，与 v2 格式**没有任何共同字段**，本管线整体跳过并把数量写进报告。
   要使用它们必须另写转换器，不能假定同口径。

2. **`ScoreVersion` 在真实数据里不止一个。** 124 个块中有 120 个 `angular-block-loss-v3`、
   4 个 `angular-block-loss-v2`。默认只保留 v3，`--score-version all` 可关闭过滤
   （但那时必须靠 `ScoreVersion` 分组，不能混算）。

3. **续测会话会重复发送来源会话的块。** 2 个会话的 `session.ResumedFrom` 指向
   `20261012-125722-1ce0c522...`，该来源 trace **已不在 traces 目录里**；
   它的 50 个块被两个续测会话各携带一遍（共 100 条 `Carried=true` 且 `Samples` 为空）。
   本管线把携带块**归回来源 session** 并按 `(来源session, BlockId)` 去重（保留最早出现的一份），
   因此 124 条 block 记录实际只有 **74 个唯一样本**。报告里会给出重复计数的示例。

4. **逐帧样本字段与描述不符。** `Samples[]` 里没有 `Yaw/TargetYaw/Radius/OnHead`，
   只有 `Tick/Time/Phase/DX/DY/CameraYaw/CameraPitch/Head*/Root*/Crouch/FacingYaw/GaitPhase`。
   角度目标信息在同一个 `sampleBatch` 的 `Events[]`（`observe` 事件）里。
   运动学特征因此基于 `DX/DY/Time/CameraYaw/CameraPitch`，而不是角度误差。

5. **绝大多数块没有逐帧样本。** 124 个 `sampleBatch` 中只有 24 个带样本（合计 82,740 帧），
   其余 100 个是上面第 3 点的携带块。所以块级表的运动学列大量为空，这是数据本身的样子。

6. **`CenterCm` 不在 trace 里。** 会话的开始档位（C# 的 `_centerCm`）只写进
   `session` **标签**（`SessionSummaryLabel.CenterCm`），trace 的 `session` 记录里没有这个字段。
   `centerRatio` 因此按"标签快照 → `sqrt(Low*High)` 代理 → 缺失"的优先级取分母。

7. **`block` 的 `Result` 里包含计算属性。** 序列化把 `Score`、`Loss`、`Request` 这几个
   只读属性也写进了 JSON（除 `Block`/`Breakdown`/`Valid`/`Diagnostics` 等存储字段之外）。
   本管线优先使用这些冗余字段，但也能从 `Block.Request` / `Breakdown` 自行计算。

8. **`report` 记录的新旧字段名不同。** 早期记录的正文在 `Report`，较新的在 `Text`
   （两者都可能是字符串）。本管线不依赖它，只把 `report` 计入"无关记录"。

9. **有 1 行 JSON 是坏的**（`20261012-172529-...jsonl` 第 9 行的 `frame` 记录被截断）。
   解析器跳过坏行并计数，不影响其它数据。

10. **`interrupted` 与 `priorValidation` 不是计分块。** 前者没有 `Result`（11 条），
    后者是"历史复核"且带 `ExcludedFromCurrentReview=true`（6 条）。默认都排除，计数写进报告。

---

## 7. 已知的、刻意的取舍

* **无效块保留在块级表里。** 因为 C# `CandidateFeatures()` 的 `blocks` 计数包含无效块
  （它们被提交进了搜索，只是不计分）。丢掉它们会让训练侧与服务侧的 `blocks` 口径不一致，
  比"看起来更干净"更糟。`--drop-invalid` 可以强制剔除，但会破坏口径一致性。
* **`x_*` 列不进模型。** 它们含运动学等 C# 提供不了的信息，只能用于离线探索。
  一旦进模型，影子推理就会因为整列缺失而被均值填充，等于用一个退化模型。
* **默认排除 Pilot 块。** C# 的 `AllResults()` 只含 Search 与 Validation 块，
  Pilot 不在其中；要保持训练/服务一致就必须同样排除。
* **`--target preference` 写不同文件名。** 成对模型与 `Feel>=7` 模型的目标不同，
  用同一个文件会互相覆盖；因此成对模型固定写到 `preference-pairwise-v1.json`
  （可用 `--model-file` 改），而永不覆盖 C# 读取的 `preference-v1.json`。
