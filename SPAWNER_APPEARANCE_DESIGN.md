# 一模型一 Appearance Type：可配置 Spawner 扩展方案（方案 A）

> 只读代码 + 资源调研产出，**未修改工程任何文件、未导入任何 unitypackage**。
> 调研日期：2026-07-03
> 配套参考：`PEDESTRIAN_SPAWNER_DESIGN.md`（spawner 原始设计）、`PERSONALITY_BEHAVIOR_DESIGN_V2.md`（personality 状态机，已落地）、`ANIMATION_INTEGRATION_ANALYSIS.md`（Animator Controller 结构、Humanoid rig 结论）。
> **重要前提澄清**：上述三份文档里提出的 `PedestrianSpawner`/`PedestrianModulator`/`AppearanceAvatar`/`ConfigurableSpawner`/`IVelocityModulator` **均已实际写入代码**（本次逐文件核实，见 §0），不再是"未落地的设计"。本文档是在**已实现的现状代码**基础上做增量扩展，不是从零设计。
> 不确定处标注"需确认"。

---

## 目录

0. [现状核实：已落地的代码与本次任务的真实起点](#0-现状核实)
1. [`~/Downloads` 实际角色清单](#1-downloads-实际角色清单)
2. [新角色接入需求：rig / Avatar / Controller 兼容性](#2-新角色接入需求)
3. [新 `SpawnGroupConfig` 结构设计](#3-新-spawngroupconfig-结构设计)
4. [appearance type 组织方式（最简方案）](#4-appearance-type-组织方式)
5. [新角色 Unity 导入步骤清单](#5-新角色-unity-导入步骤清单)
6. [建议纳入的 Rocketbox 普通成年人名单](#6-建议纳入的-rocketbox-普通成年人名单)
7. [生成逻辑改动方案](#7-生成逻辑改动方案)
8. [改动面汇总](#8-改动面汇总)
9. [风险与需确认清单](#9-风险与需确认清单)

---

## 0. 现状核实

用 `find`/`wc -l` 逐文件核实，以下文件**已经存在于仓库**（不是待办）：

| 文件 | 行数 | 状态 |
|---|---|---|
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | 292 | 已加 `ModulateVelocity()` 钩子、`TriggerAnimation()`、`IsRotationSuppressed()` 支持 |
| `Assets/Scripts/SEAN/Scenario/Agents/IVelocityModulator.cs` | 31 | 已实现（比设计文档多了 `TryGetFacingOverride`/`IsRotationSuppressed`，是后续迭代加的） |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | 365 | 已实现 Scared/Curious(三阶段状态机)/Surprised(含冻结+转身朝向+`OnAnimatorMove`)/Indifferent，比 V2 文档更完整 |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs` | 124 | 已实现，但**只支持 `TransformList` 一种 spawn 模式**（`SpawnGroupConfig.spawnPoints`），**没有** Point/Area 模式，也**没有** appearance 字段——见下 |
| `Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs` | 45 | 已实现，但只有"Simple"一种外观，`avatars[]` 数组硬编码指向 Rocketbox `Female_Adult_01/02` |
| `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/ConfigurableSpawner.cs` | 69 | 已实现，接入现有 `SetPedestrianBehavior` 机制 |
| `Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab` | — | 已存在，`AppearanceAvatar` 组件 + `Female_Adult_01`/`02` + 共享 `BaseSFControllerNormalized.controller` |

**对用户任务描述的两处修正**（用户描述里默认这些"已有"，实测有出入）：

1. **spawn 点不是"已有 Point/Area/TransformList"三种，实际只有 TransformList 一种**（`PedestrianSpawner.cs:18`，`List<Transform> spawnPoints`，随机取一个点 + `Util.Navmesh.RandomHit(point.position, 0f, 2f)` 撒开）。Point/Area 只在最早的设计文档里提过，从未写进代码。本文档的改动**不新增 Point/Area**（超出本次范围，需要的话应该是独立任务），只在现有 TransformList 基础上加 appearance/speed/终点。
2. **`walkSpeedMultiplier` 字段已经存在于 `PedestrianModulator.cs:100`**（`Scale()` 方法在所有 personality 分支末尾统一乘这个系数），**但目前没有任何生成代码给它赋非默认值**——`PedestrianSpawner.SpawnAgent()`（`PedestrianSpawner.cs:113-117`）只设置了 `modulator.personality`，从未碰 `walkSpeedMultiplier`，且 **`Indifferent` 分组现在完全不挂 `PedestrianModulator` 组件**（`PedestrianSpawner.cs:113`：`if (group.personality != Indifferent) { AddComponent<PedestrianModulator>(); ... }`）——这意味着**如果不改这一段，"素人+正常速度"的 Indifferent 分组将永远拿不到 appearance 带来的走速差异**，这是本次必须改的一处行为（见 §7）。

---

## 1. `~/Downloads` 实际角色清单

**关键澄清：不是裸 fbx，是 8 个 `.unitypackage`**（`~/Downloads/*.unitypackage`，注意是复数 `Downloads`，`~/Download` 不存在）。已用 `tar tzf` + 解出每个包内 `pathname` 文件核实每个包的真实内容（只读列出，未 `tar xzf` 到工程目录，未导入 Unity）。

| 包文件 | 大小 | 目标落地路径（包内自带） | 角色本体 fbx | 附带道具/动画控制器 |
|---|---|---|---|---|
| `cyclist.unitypackage` | 5.7 MB | `Assets/Resources/Prefabs/Community-informed Model/Cyclist/` | `Sports_Female_02.fbx`（**复用现有 Rocketbox 角色**，见 §2） | 自行车模型（`Sepeda Facific Invert.fbx` + ~20 个 `.mat`）、`Bike Controller.controller`、`Cycling Animation/CyclistController.controller`（座姿蹬踏循环动画）、`Cyclist.prefab`（已装好） |
| `dog_walker.unitypackage` | 228 MB（**远大于其余包，含大量共享动画资源重复打包**） | `Assets/Resources/Prefabs/Community-informed Model/Dog Walker/` | `Ch22_nonPBR@Holding Walk.fbx`（Mixamo 风格命名，新模型） | 狗模型 `cur.fbx`、`AttachPropToHand.cs`/`DynamicLeash.cs`（**工程里目前不存在这两个脚本**，是包自带的新脚本）、`Dog_Walker.prefab`（已装好）。包里还打包了一份 `HumanoidWalk.fbx`/`HumanoidIdle.fbx`/`LegMask.mask`/`WalkBack.fbx` 等——这些路径在现有工程 `Assets/ExternalAssets/StandardAssets/...`/`Assets/Resources/Animation/...` 下**已经存在**，导入时 Unity 会提示"已存在同路径资源"，内容是否完全一致本次未逐字节比对，**需确认**导入时选择保留工程现有版本还是覆盖 |
| `female_child.unitypackage` | 44 MB | `Assets/Resources/Prefabs/Community-informed Model/Female Child/` | `kid2.fbx`（Reallusion Character Creator 命名规范 `CC_Base_*`，新模型） | `Female_Child.prefab`（已装好），同样打包了一份已存在的共享 Animation 资源（同上顾虑） |
| `male_child.unitypackage` | 50 MB | `Assets/Resources/Prefabs/Community-informed Model/Male Child/` | `KIDS-01.fbx`（新模型） | `Male_Child.prefab`（已装好），同样打包已存在的共享 Animation 资源 |
| `phone_user.unitypackage` | 42 MB | `Assets/Resources/Prefabs/Community-informed Model/Phone User/` | `Female_Adult_05 1.fbx`（**复用现有 Rocketbox 角色** `Female_Adult_05`） | 手机模型（`Apple iphone 12 pro max.fbx`）、`Walking While Texting 2.fbx`（低头看手机走路动画）、`PhoneController.controller` + `PhoneMask.mask`（上半身遮罩，说明这是一个**叠加层动画**，见 §2）、`Phone_User.prefab`（已装好） |
| `scooter_user.unitypackage` | 25 MB | `Assets/Resources/Prefabs/Community-informed Model/Scooter User/` | `casual_male.fbx`（新模型，**唯一 Generic rig，见 §2**） | 滑板车模型（`default.fbx`）、`Scooter_User.prefab`（已装好） |
| `wheelchair_users.unitypackage` | 196 MB | `Assets/Resources/Prefabs/Rocketbox/wheelchair-male/`、`.../wheelchairuser-female/`、`Assets/Resources/Prefabs/Rocketbox/Wheelchair_Female.prefab` | 男款 `Wheelchair.fbx`（Generic rig，见 §2）、女款 `Wheelchair (1).fbx`（Humanoid） | **女款有装好的 `Wheelchair_Female.prefab`；男款只有原始 fbx/材质/`Wheelchair.controller`/`model.dae`，没有装好的顶层 prefab**——需要手动组装（见 §5） |
| `white_cane_user.unitypackage` | 36 MB | `Assets/Resources/Prefabs/Community-informed Model/White Cane User/` | `Male_Adult_12.fbx`（**复用现有 Rocketbox 角色** `Male_Adult_12`） | 盲杖模型（`uploads_files_3305714_cane.fbx`）、眼镜（`fbxGlasses.fbx`）、`HoldingController.controller`（同 Phone User 的叠加层套路）、`White_Cane_User.prefab`（已装好） |

### 与用户预期名单的差异（需和 Howard 确认补发）

用户任务描述里预期的名单是：`Cane_User, Cyclist, Dog_Walker, Female_Child, Male_Child, Phone_User, Scooter_User, Walker_User, White_Cane_User, Wheelchair_Male, Wheelchair_Female`（11 个）。

实际收到 8 个包，覆盖了其中 9 个（`Wheelchair_Male`/`Wheelchair_Female` 算两个，来自同一个包）。**缺失两个，`~/Downloads` 里完全没有对应文件**：

- **`Cane_User`**（普通拐杖，区别于视障用的白手杖）—— 缺失
- **`Walker_User`**（助行架/rollator）—— 缺失

这两个需要找 Howard 补发，本次设计不覆盖（没有素材可分析）。

---

## 2. 新角色接入需求：rig / Avatar / Controller 兼容性

### 2.1 关键架构事实（决定一切兼容性判断的基础）

`ANIMATION_INTEGRATION_ANALYSIS.md` 已经逐行核实并本次复核：`Agents.Base.cs` 驱动行人移动的机制是——

- `Awake`/`Start` 里 `animator.applyRootMotion = true`（`Base.cs:32,64`，**硬编码，全仓库无处修改**），且 **`Move()`（`Base.cs:201-270`）里没有任何 `transform.Translate`/直接写 `position` 的代码**——行人在世界里的**位移 100% 来自 Animator Root Motion**，NavMesh 路径只用来算转向角度。
- `AppearanceAvatar.cs`/`RandomAvatar.cs` 在 `Instantiate` 角色后，都会**整体替换** `animator.runtimeAnimatorController = animationController`（共享的 `Assets/IVI/Controllers/BaseSFControllerNormalized.controller`）——**角色自带的 Animator Controller 会被覆盖，不生效**。
- 这个共享 Controller 唯一驱动位移的是一个 2D Blend Tree（`Forward`/`Strafe` 参数），9 个子动作全部核实为 **Humanoid rig**（Rocketbox 官方 + Unity Standard Assets Demo + Mixamo 混用，已验证跨来源 retarget 可行）。

**结论（本次调研最重要的一条）**：任何新角色要"能走路"，必须满足**两个独立条件**：
1. 角色自己的 fbx 导入设置里 **Animation Type = Humanoid**，且骨骼层级能被 Unity 自动映射（或手动 Configure 映射）成功——这样共享 Controller 的 Humanoid clip 才能 retarget 到这个新骨架上。
2. 角色的移动方式本身是"站立行走"——如果角色是"坐在载具上"（自行车/滑板车/轮椅），共享 Controller 的行走 Blend Tree**语义上就不适用**（骑车/坐轮椅不该有"走路"的 Forward/Strafe 摆腿动作），即使 rig 是 Humanoid 也没用,见下方 Group C。

### 2.2 逐包 rig 类型核实（读取包内 fbx 的 `.meta`，`animationType` 字段：`3`=Humanoid，`2`=Generic）

| 包 | 角色本体 fbx | `animationType` | 结论 |
|---|---|---|---|
| Cyclist | `Sports_Female_02.fbx` | **3 (Humanoid)** | ✅ 且这就是工程里已有的 `Rocketbox/Sports_Female_02.prefab` 同一个模型（已用 `find` 核实存在），本身早就是验证过能用的 Rocketbox Humanoid 角色 |
| Female Child | `kid2.fbx` | **3 (Humanoid)** | ✅ CC (Character Creator) 命名骨骼常见能正确映射 Humanoid，但**需在 Editor 里实际打开 Configure 界面确认无缺失骨骼报错**（本次只读了 `.meta` 数值，没有在 Unity 里跑一遍） |
| Male Child | `KIDS-01.fbx` | **3 (Humanoid)** | ✅ 同上，需 Editor 内二次确认 |
| Phone User | `Female_Adult_05 1.fbx` | **3 (Humanoid)** | ✅ 就是工程里已有的 `Rocketbox/Female_Adult_05.prefab`，同一模型，零风险 |
| White Cane User | `Male_Adult_12.fbx` | **3 (Humanoid)** | ✅ 就是工程里已有的 `Rocketbox/Male_Adult_12.prefab`，同一模型，零风险 |
| Dog Walker | `Ch22_nonPBR@Holding Walk.fbx` | **3 (Humanoid)** | ✅ Mixamo 风格命名（`Ch22`），需 Editor 内二次确认骨骼映射 |
| **Scooter User** | `casual_male.fbx` | **2 (Generic)！** | ⚠️ **本批唯一非 Humanoid 的角色本体**，导入后**默认无法**被共享 Controller 驱动，需要手动在 Import Settings 里把 Animation Type 改成 Humanoid 并确认自动映射成功（见 §5），如果这个骨架命名不规范导致自动映射失败，需要手动逐骨骼 Configure，工作量不确定，**需确认** |
| Wheelchair Female | `Wheelchair (1).fbx` | **3 (Humanoid)** | ✅（但见下方 Group C，Humanoid 不代表能直接走路） |
| **Wheelchair Male** | `Wheelchair.fbx` | **2 (Generic)！** | ⚠️ 本体是 Generic，且**没有配好的顶层 prefab**（§1），是本批接入成本最高的一个 |

（道具类 mesh——自行车、滑板车、狗——的 `.meta` 也顺带核实过，全部是 `animationType: 2 (Generic)`，这是**正常且符合预期**的，道具不需要 Humanoid。）

### 2.3 三类角色的接入难度分层

**Group A ——复用现有 Rocketbox 角色，零 rig 风险，只是加了道具**（接入成本最低）：

- Phone User → `Female_Adult_05`
- White Cane User → `Male_Adult_12`
- Cyclist → `Sports_Female_02`

这三个包本体骨架就是工程里已经在用、已验证兼容的 Rocketbox 角色，只是套了道具（手机/盲杖+眼镜/自行车）。**第一阶段最简做法**：不接入包里自带的"低头看手机走路"`Walking While Texting 2.fbx`/`HoldingController.controller` 这类**上半身叠加层动画**（`PhoneMask.mask`/`LegMask.mask` 命名说明这是"下半身走共享 Controller、上半身叠加另一层"的分层动画方案，Mecanim 里要接入需要给 Animator 新增一个 Layer + Avatar Mask，这是比"一模型一 appearance"更大的改动，超出本次范围）——直接**把手机/盲杖模型用 `AttachPropToHand.cs`（Dog Walker 包带的通用脚本，可以复用给 Phone/Cane）挂在手上当静态道具**，角色照常走共享 Locomotion Blend Tree，视觉上"这个人一直举着手机/拿着拐杖走路"，不做"低头看屏幕"的专属上半身动作。**需确认**：这个简化是否满足当前实验对这几种 appearance 的视觉要求，如果必须要"低头看手机"的姿态，需要额外做 Animator Layer + Mask 的工作（阶段二）。

**Group B ——全新模型，需要在 Editor 里验证 Humanoid 绑定**（接入成本中等）：

- Female Child (`kid2.fbx`)
- Male Child (`KIDS-01.fbx`)
- Dog Walker (`Ch22...fbx`)

`.meta` 数值上已经是 Humanoid，但"数值是 Humanoid"不等于"骨骼映射一定成功"——需要在 Unity Editor 里打开 Rig 设置点 `Configure...`，确认没有"Hips/Spine/…missing"之类报错。孩童角色额外要注意：Humanoid retarget 是按肌肉空间归一化的，**成人的走路动画套在儿童比例骨架上大概率不会崩，但步幅/姿态可能显得不自然**（腿短但迈成人步幅的视觉观感），**需实测**，如果不自然可能需要给 Female_Child/Male_Child 单独配一条更小步幅的行走动画（属于阶段二）。

**Group C ——"坐/骑乘"类角色，与现有走路系统架构性不兼容**（接入成本最高，建议本次先不接入或只做静态展示）：

- Cyclist（骑自行车，座姿蹬踏）
- Scooter User（站姿/骑滑板车）
- Wheelchair Male / Wheelchair Female（轮椅，推轮动作）

这四个的共同问题：**它们的"移动"语义根本不是"走路"**——骑车/坐轮椅不应该有 Forward/Strafe 摆腿的行走 Blend Tree，应该是"蹬踏/推轮循环动画原地播放 + 角色整体位移"。但 §2.1 已经确认**当前架构里位移 100% 来自 Animator Root Motion**，没有任何代码路径支持"动画原地循环 + 脚本直接平移 `transform`"这种模式。也就是说：

- 如果给这四个角色套共享的走路 Controller：动作会是"坐着/骑着车却在做走路摆腿"，视觉完全错误。
- 如果换成它们各自打包的 `CyclistController.controller`/`Wheelchair.controller` 等专属 Controller（`AppearanceMapping.overrideAnimatorController` 这个预留字段就是干这个的），这些专属 Controller 的�ним通常是"原地循环"（不带位移的 Root Motion，或者根本没有按 Forward/Strafe 语义设计），套上去以后角色会**原地做蹬踏/推轮动作但不移动**——因为 `Base.cs` 没有任何机制在 Root Motion 之外驱动位移。

**结论/建议**：这四个"载具类" appearance **不适合直接套进本次"一模型一 appearance type"的最简方案**。建议本次设计范围只覆盖 Group A + Group B 这 6 个（Phone User / White Cane User / Cyclist-仅换皮不用骑行动画 / Female Child / Male Child / Dog Walker）—— **注意 Cyclist 想要"骑车"效果的话也归入 Group C 的限制**，如果只是想让"Sports_Female_02 这个模型多一个 appearance 选项"而不追求骑车动作，可以把 Cyclist 降级处理成"就是 Sports_Female_02，走路，不出现自行车"，等价于 Group A 里"不接叠加动画"的简化处理，这样它就没有 Group C 的问题了；**如果确实需要看到骑车动作**，Group C 四个都需要 `Base.cs` 层面新增一种"非 Root-Motion 驱动位移"的移动模式，这是比本次任务大得多的架构改动，建议单独立项，本文档只记录问题，不在 §7 的改动方案里覆盖。**需确认**：用户对 Cyclist/Scooter/Wheelchair 这几个 appearance 的预期是"能看到骑车/坐轮椅但可以先不做动作对不上的问题"，还是"必须有正确的骑行/推轮动画"——这直接决定这四个是否进本期范围。

---

## 3. 新 `SpawnGroupConfig` 结构设计

现状（`PedestrianSpawner.cs:13-19`）：

```csharp
[System.Serializable]
public class SpawnGroupConfig
{
    public string label;
    public PedestrianModulator.PersonalityType personality = PedestrianModulator.PersonalityType.Indifferent;
    public int count;
    public List<Transform> spawnPoints;
}
```

**新结构**（新增 3 个字段，其余不变；不引入 Point/Area，见 §0 的修正说明）：

```csharp
[System.Serializable]
public class SpawnGroupConfig
{
    public string label;
    public PedestrianModulator.PersonalityType personality = PedestrianModulator.PersonalityType.Indifferent;
    public int count;
    public List<Transform> spawnPoints;

    // --- 新增 ---
    public GameObject appearancePrefab;        // 见 §4，拖一个"appearance 容器 prefab"
    public float walkSpeedMultiplier = 1.0f;   // 独立于 personality 的走速倍率
    public List<Transform> destinationPoints;  // 可选；为空/未填 = 保留现有随机游走
}
```

- `appearancePrefab`：每组一个，替代原来 `PedestrianSpawner` 类级别唯一的 `agentPrefab` 字段（见 §4，这是本次改动的核心）。
- `walkSpeedMultiplier`：直接对应 `PedestrianModulator.walkSpeedMultiplier` 字段（已存在，见 §0），spawn 时赋值过去。
- `destinationPoints`：为空 = 当前行为（`PedestrianSpawner.Update()` 里 `CloseEnough()` 就 `InitDest(Util.Navmesh.RandomPose().position)`，纯随机游走）；非空 = 见 §7 的目标点逻辑。

---

## 4. appearance type 组织方式（最简方案）

用户给的两个选项：「一个 `[Serializable]` 映射表（appearance 名 → prefab）」vs「每个 SpawnGroup 直接一个 GameObject prefab 字段」。

**推荐：后者（直接字段），理由是它能做到本次改动量最小、且不需要新写一行 `AppearanceAvatar.cs` 的代码**：

现有 `AppearanceAvatar.cs`（`Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs`）的 `Awake()` 逻辑是：`avatars[Random.Range(0, avatars.Length)]` 随机挑一个再 `Instantiate`。**"一模型一 appearance type"意味着每个 appearance 的候选数组长度就是 1**——`avatars.Length == 1` 时 `Random.Range(0, 1)` 恒等于 `0`，这个既有类的行为**不需要改一行代码**就能满足需求，只要给每个 appearance 类型准备一个"容器 prefab"：

- 复制现有 `Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab`（一个只挂 `AppearanceAvatar` 组件的空 GameObject）
- 改名（如 `MaleChild_AppearanceAgent.prefab`），把 `avatars` 数组改成只有 1 个元素，指向对应角色 prefab（如 `Male_Child.prefab`）
- `animationController` 字段留空指向共享的 `BaseSFControllerNormalized.controller`（Group A/B 都这样），或指向专属 Controller（Group C，如果之后要做）

这样每个 appearance type 在磁盘上就是一个几十字节的小 prefab 文件，`SpawnGroupConfig.appearancePrefab` 直接拖这个小 prefab 进去。**零代码改动**（`AppearanceAvatar.cs` 不用碰），只是"资源工作"（在 Editor 里创建 N 个小 prefab），且完全符合用户"拖 prefab、不做可视化 Editor"的要求。

`PedestrianSpawner.cs` 需要改的只是：把 `Instantiate(agentPrefab, ...)` 里的 `agentPrefab`（原来是类级别唯一字段）换成 `group.appearancePrefab`（见 §7 完整 diff）。

**被排除的备选方案（映射表）**：`AppearanceMapping[]`（`{appearanceId 字符串, avatarPrefab}` 数组 + `SpawnGroupConfig` 存字符串/枚举去查表）多了一层间接——好处是"appearance 列表在一个地方看全"，坏处是需要新写查表代码、需要保证枚举/字符串和数组下标不会对不上、而且每加一个新 appearance 类型要么加枚举值要么手打字符串（容易打错無编译期检查）。既然是"一模型一类型"、数量本来就不多（本次 6-9 个），直接拖 prefab 字段没有这些维护成本，**这是最简方案**。

---

## 5. 新角色 Unity 导入步骤清单

以下步骤需要在 Unity Editor 里手动做（本次只读分析不代做）。**每个新角色都要走一遍**：

1. **导入 `.unitypackage`**：`Assets → Import Package → Custom Package...`，选中对应文件。导入对话框会列出包内全部资源，**Dog Walker/Female Child/Male Child 三个包里有和工程现有路径重叠的共享 Animation 资源**（见 §1 表格备注），导入时如果 Unity 弹出"资源已存在"提示，建议**取消勾选那些重�一致别的共享动画文件**，只导入角色自己专属的部分（fbx、贴图、prefab、专属 controller），避免覆盖工程里可能已经被其它角色引用、版本一致性未知的共享资源。**需确认**：具体重叠文件逐个比对内容是否字节级相同，本次未做二进制 diff。

2. **确认/设置 Rig 为 Humanoid**（选中导入后的角色 fbx → Inspector → Rig 标签页）：
   - Group A（Phone User/White Cane User/Cyclist 本体）：本来就是工程已有的 Rocketbox fbx，**这一步理论上不需要做**（如果导入包时没有覆盖工程原有的 `Female_Adult_05.fbx`/`Male_Adult_12.fbx`/`Sports_Female_02.fbx`，用现有的就行，压根不用管包里带的那份）。
   - Group B（Female Child/Male Child/Dog Walker）：`Animation Type` 已经是 `Humanoid`（包内 `.meta` 自带），**打开 Configure 界面确认无红色报错**，尤其确认 Hips/Spine/手臂/腿的映射都正确对上了模型骨骼。
   - Scooter User (`casual_male.fbx`)：**当前是 Generic，必须手动改成 Humanoid**，改完点 `Apply` 后 Unity 会尝试自动映射骨骼，如果失败会有报错提示，需要手动在 `Configure` 界面里逐骨骼拖拽对应关系。
   - Wheelchair Male (`Wheelchair.fbx`)：同样当前是 Generic，是否应该走 Humanoid 取决于是否要做成 Group C 的骑乘特殊处理（如果只是先当"轮椅+人"整体道具、坐着不单独驱动腿部动画，也许根本不需要 Humanoid，直接当静态/自带专属 Controller 处理更合适，**需和用户确认这个角色本次要不要接入"走路"语义**）。

3. **Avatar 定义**：`Avatar Definition` 选 `Create From This Model`（Group A 不需要，用现有 Rocketbox prefab 自带的 Avatar 即可）。

4. **组装 appearance 容器 prefab**（§4 步骤）：
   - 确认角色 prefab（如 `Male_Child.prefab`）根物体或子物体上有 `SkinnedMeshRenderer`（`Base.cs:52` 硬依赖，没有会直接 NullReferenceException 崩)和 `Animator`（`Base.cs:63`，同样硬依赖）。
   - 复制 `SimpleAppearanceAgent.prefab` → 改名 → 打开 `AppearanceAvatar` 组件 → `avatars` 数组填 1 个元素指向这个角色 prefab → `animationController` 留空指向共享 Controller（Group A/B）。
   - 静态道具（手机/盲杖/眼镜）：用包自带的 `AttachPropToHand.cs`（Dog Walker 包带的，可以直接复制到 `Assets/IVI/Scripts/` 给其它 appearance 复用）挂在角色的手部骨骼上，参数指定要挂载的道具 prefab（手机/盲杖 fbx 各自做成一个小道具 prefab）。

5. **场景侧接线**（每个要用这批新 appearance 的场景，如 Lab/Outdoor/Warehouse 各自的 `PedestrianControl/ConfigurableSpawnerRoot` 下的 `PedestrianSpawner` 组件）：
   - Inspector 里 `spawnGroups` 列表，新增/编辑某一行，`appearancePrefab` 拖对应第 4 步做好的容器 prefab。

6. **实机验证**：Play 模式下确认新角色能正常站立行走（不塌陷、动画不抽搐、朝向正确），尤其 Group B 的孩童角色注意步幅观感（§2.3）。

---

## 6. 建议纳入的 Rocketbox 普通成年人名单

**用 `find`/`ls` 核实了工程现有全部 90 个 Rocketbox prefab 的文件名**（`Assets/Resources/Prefabs/Rocketbox/*.prefab`），命名规律是 `职业_性别_编号`，`Female_Adult_*`/`Male_Adult_*` 是唯一没有职业服装标签的"普通人"系列（17 个女性 + 21 个男性）。

已被占用的（避免重复选到）：
- `Female_Adult_01`/`02` —— `Random.prefab`（`RandomAvatar.cs`）和现有 `SimpleAppearanceAgent.prefab` 已经在用
- `Female_Adult_05` —— 本次 Phone User 复用
- `Male_Adult_12` —— 本次 White Cane User 复用
- `Sports_Female_02` —— 本次 Cyclist 复用（如果 Cyclist 采纳，见 §2.3）

**建议新增纳入的 4 个（2 男 2 女，均未被占用，均是无职业服装标签的"普通人"）**：

| appearance 名建议 | Rocketbox prefab |
|---|---|
| `SimpleMale1` | `Male_Adult_01` |
| `SimpleMale2` | `Male_Adult_02` |
| `SimpleFemale1` | `Female_Adult_03` |
| `SimpleFemale2` | `Female_Adult_04` |

选 `01`/`02`/`03`/`04` 只是取编号最小、最"居中/无特征"的几个，纯粹为了避开已占用编号，没有对这几个模型的外观做逐一视觉核对（**需确认**：建议在 Editor 里预览一遍这 4 个模型的服装/体型，确认符合"普通成年人"的观感预期，如果某一个视觉上有明显职业化特征（本次只按文件名판断，未逐一看模型），换成同类里的其它编号即可，不影响本设计结构）。

---

## 7. 生成逻辑改动方案

### 7.1 `PedestrianSpawner.cs` 改动点（3 处）

**(a) 类级别 `agentPrefab` 字段废弃，appearance 从 `group.appearancePrefab` 取**（`PedestrianSpawner.cs:38`,`104`）：

```csharp
// 现状（PedestrianSpawner.cs:104）：
var container = Instantiate(agentPrefab, Vector3.zero, Quaternion.identity);

// 改为：
var container = Instantiate(group.appearancePrefab, Vector3.zero, Quaternion.identity);
```

`public GameObject agentPrefab;`（类级别字段）可以删除，或保留作为"某组没填 `appearancePrefab` 时的兜底默认值"（`group.appearancePrefab != null ? group.appearancePrefab : agentPrefab`）——**建议保留做兜底**，这样如果某个已经配置好的场景暂时没给某组填新字段，行为不会突然报错，只是继续用老的默认外观，属于向后兼容的最小代价（**需确认**是否需要这个兜底，纯新建的组不需要）。

**(b) `SpawnAgent()` 里补 `walkSpeedMultiplier`，且去掉"Indifferent 不挂组件"的优化**（`PedestrianSpawner.cs:113-117`）：

```csharp
// 现状：
if (group.personality != PedestrianModulator.PersonalityType.Indifferent)
{
    var modulator = agent.gameObject.AddComponent<PedestrianModulator>();
    modulator.personality = group.personality;
}

// 改为（Indifferent 也要挂组件，否则 appearance 的走速差异对 Indifferent 分组不生效）：
var modulator = agent.gameObject.AddComponent<PedestrianModulator>();
modulator.personality = group.personality;
modulator.walkSpeedMultiplier = group.walkSpeedMultiplier;
```

原来"`Indifferent` 不挂组件"是一个性能优化（避免每帧一次 `GetComponent<IVelocityModulator>()` 的极小开销，`Base.cs:69` 已经把这个查找缓存在 `Start()` 里只做一次，所以其实这个优化点现在已经没有意义了——`modulator` 缓存本来就是"有就有，没有就是 null"，多一个空跑 `default: return Scale(...)` 分支的组件，性能代价可忽略（`PERSONALITY_BEHAVIOR_DESIGN_V2.md` §4 已经论证过几十人规模无感知）。**这是本次唯一"改变现有行为"的地方**（Indifferent 组现在会多一个组件），但不改变 Indifferent 组的速度表现（`walkSpeedMultiplier` 默认 1.0 时 `Scale()` 恒等），只是让 appearance 的走速差异在 Indifferent 下也能生效——符合用户"speedMultiplier 独立于 personality"的要求。

**(c) `Update()` 加终点逻辑，且需要一个 agent → group 的反查表**（`PedestrianSpawner.cs:45-61`）：

```csharp
// 新增字段：
private Dictionary<IVI.INavigable, SpawnGroupConfig> agentGroups = new Dictionary<IVI.INavigable, SpawnGroupConfig>();
// 每个 agent 的目的地游标（当 destinationPoints.Count > 1 时按顺序循环）
private Dictionary<IVI.INavigable, int> destinationCursor = new Dictionary<IVI.INavigable, int>();

void Update()
{
    foreach (var agent in agents)
    {
        var modulator = agent.gameObject.GetComponent<PedestrianModulator>();
        if (modulator != null && modulator.IsControllingDestination)
        {
            continue; // Curious Approach/Follow 接管中，不冲突（见下方说明）
        }
        if (!agent.CloseEnough()) { continue; }

        agentGroups.TryGetValue(agent, out var group);
        if (group == null || group.destinationPoints == null || group.destinationPoints.Count == 0)
        {
            agent.InitDest(Util.Navmesh.RandomPose().position); // 现状：随机游走
            continue;
        }

        // 填了终点：按顺序循环走完 destinationPoints 列表（1 个点 = 走到就停，见 §7.2 说明）
        int cursor = destinationCursor.TryGetValue(agent, out var c) ? c : 0;
        agent.InitDest(group.destinationPoints[cursor % group.destinationPoints.Count].position);
        destinationCursor[agent] = cursor + 1;
    }
}
```

`SpawnAgent()` 里 `agents.Add(agent)` 之后补一行 `agentGroups[agent] = group;`，`Clear()` 里补 `agentGroups.Clear(); destinationCursor.Clear();`。

**与 Curious 状态机的既有冲突检查已确认安全**：`PERSONALITY_BEHAVIOR_DESIGN_V2.md` §2.6 的 `IsControllingDestination` 检查在本次改动前的判断逻辑里已经排在最前面（先看 `IsControllingDestination` 再看 `CloseEnough()`），本次新增的"终点/随机游走"分支是在**这个检查通过之后**才执行的分支选择，不会和 Curious 的 `Approach`/`Follow` 抢 `destPos`——机器人靠近时 Curious 照常接管（哪怕这个 agent 本来在走固定终点路线），机器人远离后 Curious 松手，agent 会用**当前 `destinationCursor`**继续走原来该走的下一个终点，不会因为被 Curious 打断过而重置进度。

### 7.2 需确认：到达终点后的行为语义

用户任务描述只说"不填走随机，填了走向终点"，没说清"到了之后干嘛"。本设计给了一个默认行为，**需确认是否符合预期**：

- `destinationPoints` 只有 1 个点：agent 走到后，下一次 `CloseEnough()` 还是会命中"有 destinationPoints"分支，`cursor % 1 == 0` 恒成立，等于**每次 `CloseEnough()` 都重新 `InitDest()` 同一个点**——实际效果是"走到目的地附近悬停/小范围游荡"（因为 `CloseEnough()` 阈值是 1m，到了以后如果被社会力推得稍微远一点、再次进入判定就会重新规划到同一点，近似"原地等待"）。如果想要的是"走到就彻底不再动"，需要额外加一个"已到达"标志位跳过后续 `InitDest`,这是个小改动，**需确认**是否要这个语义。
- `destinationPoints` 多个点：按数组顺序循环访问（到 A→到 B→回到 A→...），模拟"通勤/巡逻"路线。如果想要"随机顺序"而不是固定循环，把 `cursor % Count` 换成 `Random.Range(0, Count)` 即可，**需确认**想要哪种。

### 7.3 `AppearanceAvatar.cs` / `Base.cs` / `PedestrianModulator.cs`：不改

- `AppearanceAvatar.cs`：如 §4 所述，`avatars.Length == 1` 时现有随机挑选逻辑自然退化成固定选择，不需要改代码。
- `PedestrianModulator.cs`：`walkSpeedMultiplier` 字段和 `Scale()` 方法已经存在（§0），只是之前没人赋值，本次不改这个文件。
- `Base.cs`：`SkinnedMeshRenderer`/`Animator` 硬依赖不变，新角色只要满足这两个组件存在即可，不需要改这个文件。

---

## 8. 改动面汇总

| 文件 | 状态 | 说明 |
|---|---|---|
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs` | **修改** | `SpawnGroupConfig` 加 3 个字段（`appearancePrefab`/`walkSpeedMultiplier`/`destinationPoints`）；`SpawnAgent()` 改用 `group.appearancePrefab`，补 `walkSpeedMultiplier` 赋值，去掉 Indifferent 不挂组件的优化；`Update()` 加终点/随机游走的分支选择 + `agentGroups`/`destinationCursor` 两个反查表 |
| `Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs` | **不改** | `avatars.Length==1` 天然满足需求 |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | **不改** | `walkSpeedMultiplier`/`Scale()` 已存在 |
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | **不改** | 硬依赖不变 |
| N 个新 "appearance 容器" prefab（如 `MaleChild_AppearanceAgent.prefab`） | **新增**（资源，非代码） | 每个新 appearance type 一个，`AppearanceAvatar` 组件 + 单元素 `avatars[]` |
| 新角色本体 prefab（`Female_Child.prefab` 等，来自 unitypackage） | **新增**（导入） | 按 §5 步骤导入并确认 Humanoid rig |
| `Assets/IVI/Scripts/AttachPropToHand.cs`/`DynamicLeash.cs` | **新增**（来自 Dog Walker 包） | 工程目前不存在，需要从包里带入；`AttachPropToHand.cs` 可复用给 Phone User/White Cane User 挂手机/盲杖（见 §2.3） |
| 各场景 `ConfigurableSpawnerRoot` 下的 `PedestrianSpawner` Inspector 配置 | **修改**（数据） | 现有 `spawnGroups` 列表逐行补 `appearancePrefab` |

**明确排除在本次范围外**（见 §2.3 Group C）：Cyclist 的骑行动作、Scooter User、Wheelchair Male/Female 的坐姿/推轮动作——这四个如果要"看起来在骑车/坐轮椅动"，需要 `Base.cs` 层面新增非 Root-Motion 驱动位移的移动模式，属于更大的架构改动，建议单独立项讨论。

---

## 9. 风险与需确认清单

- **需确认**：`~/Downloads` 里缺失 `Cane_User`/`Walker_User` 两个角色，需要找 Howard 补发（§1）。
- **需确认**：Dog Walker/Female Child/Male Child 三个包里打包了一份和工程现有路径重叠的共享 Animation 资源，本次未做字节级 diff，导入时建议不勾选这些重叠文件，只导入角色专属部分（§1、§5 步骤 1）。
- **需确认**：Scooter User (`casual_male.fbx`) 和 Wheelchair Male (`Wheelchair.fbx`) 当前是 Generic rig，需要手动改 Humanoid 并确认骨骼自动映射成功，如果骨骼命名不规范可能需要手动逐骨骼 Configure，工作量不确定（§2.2、§5 步骤 2）。
- **需确认**：Wheelchair Male 没有配好的顶层 prefab（只有原始 fbx/材质/controller/dae），是本批接入成本最高的一个，需要用户或 Howard 手动组装（§1）。
- **需确认**：Group C（Cyclist 骑行/Scooter/Wheelchair 坐姿+推轮）是否本次就要接入"正确的骑乘/推轮动画"——如果要，需要额外的 `Base.cs` 架构改动（非 Root-Motion 驱动位移），超出本次"一模型一 appearance type" spawner 扩展的范围，建议单独立项；如果只是想让这几个模型"能走路出现在场景里、暂不追求骑乘动作正确"，可以按 Group A 的简化方式处理（§2.3）。
- **需确认**：Group A 的 Phone User/White Cane User 是否需要"低头看手机/持杖"的专属上半身动画（包里自带的 `PhoneMask.mask`/`HoldingController.controller` 说明原作者设计的是 Animator 分层叠加方案），还是本次先用"角色照常走路 + 手上挂静态道具"的简化方案即可（§2.3）。
- **需确认**：Female_Child/Male_Child 套用成年人走路动画后的步幅/姿态观感是否自然，需要实机播放后判断是否需要专属的儿童行走动画（阶段二，§2.3）。
- **需确认**：到达 `destinationPoints` 终点后的行为语义——单点是"到达后原地小范围游荡"还是"彻底停住不再动"；多点是"固定顺序循环"还是"随机挑选"（§7.2）。
- **需确认**：`Male_Adult_01/02`/`Female_Adult_03/04` 这 4 个建议的"普通成年人"候选，本次只按文件名规律筛选，未在 Editor 里逐一打开看模型服装是否真的"无职业特征"，建议使用前预览一遍（§6）。
- **需确认**：`agentPrefab` 类级别字段是否要保留做"某组未填 `appearancePrefab` 时的兜底默认值"，还是直接删除强制每组必填（§7.1(a)）。
