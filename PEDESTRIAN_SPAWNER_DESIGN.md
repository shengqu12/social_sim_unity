# 可配置行人 Spawner：现状分析 + 设计方案

> 只读代码分析产出，**未修改工程任何文件**。
> 分析日期：2026-07-02
> 配套参考：`SEAN_architecture_analysis.md`（整体架构）、`PEDESTRIAN_SCENARIO_DESIGN.md`（scenario 选择机制 + 已给出的 modulation hook 建议）
> 全文分两部分：**第 1 章「现状分析」**（客观代码事实，均已用 `grep`/`Read` 逐行核实）与 **第 2 章「设计方案」**（未落地，供讨论）。不确定处标注"需确认"。

---

## 目录

1. [现状分析](#1-现状分析)
   1. [现有 spawn 链路：Random 场景全链路](#11-现有-spawn-链路random-场景全链路)
   2. [GraphNav 场景对比：spawn 位置来自场景手摆数据](#12-graphnav-场景对比spawn-位置来自场景手摆数据)
   3. [个体移动层：`Agents.Base.Update()` 与调制插入点](#13-个体移动层agentsbaseupdate-与调制插入点)
   4. [机器人位置获取方式](#14-机器人位置获取方式)
   5. [spawn 位置现状汇总](#15-spawn-位置现状汇总)
   6. [关键约束：下游系统只认"当前唯一 active scenario"](#16-关键约束下游系统只认当前唯一-active-scenario)
2. [设计方案](#2-设计方案)
   1. [总体架构](#21-总体架构)
   2. [`PedestrianSpawner` 组件设计](#22-pedestrianspawner-组件设计)
   3. [`PersonalityModulator` 设计](#23-personalitymodulator-设计)
   4. [appearance 走速差异如何生效（与 personality 共用同一插入点）](#24-appearance-走速差异如何生效与-personality-共用同一插入点)
   5. [与现有 RandomAvatar / scenario 机制共存方案](#25-与现有-randomavatar--scenario-机制共存方案)
   6. [appearance 模型接口预留：Howard 角色资源到位后怎么接](#26-appearance-模型接口预留howard-角色资源到位后怎么接)
   7. [personality × appearance 正交组合怎么落地](#27-personality--appearance-正交组合怎么落地)
3. [改动面汇总（哪些是新文件，哪些必须碰现有文件）](#3-改动面汇总)
4. [风险与需确认清单](#4-风险与需确认清单)

---

## 1. 现状分析

### 1.1 现有 spawn 链路：Random 场景全链路

以当前实际跑起来的 `Random` scenario 为例，完整链路（均已读代码核实）：

```
SEAN.SetPedestrianBehavior("Random")
  → /SEAN/PedestrianBehaviors/Random 这个 GameObject SetActive(true)
    → PedestrianBehavior.Random.Start()（继承 PedestrianBehavior.Base）
      → 在场景的 /Environment/PedestrianControl 下找名为 "Random" 的子物体，SetActive(true)
        （这个子物体本身就是 Resources/SEAN/PedestrianBehaviors/Random.prefab 的一个实例，
         挂着 Agents.RandomABNavAgentManager 组件 —— 见下方 Random.prefab 内容）
      → agentManager = (RandomABNavAgentManager)Agents.BaseAgentManager.instance
      → agentManager.Restart()
```

`Agents/RandomABNavAgentManager.cs`（`Assets/Scripts/SEAN/Scenario/Agents/RandomABNavAgentManager.cs`）：

- `public int numberOfAgents = 65;` —— **数量是 Inspector 上的一个整数字段**，`Random.prefab` 里序列化值为 `65`。
- `public GameObject agentPrefab;` —— `Random.prefab` 里这个字段引用的是 `Assets/Resources/Prefabs/RocketboxRandomAnimatedAgent.prefab`（已用 GUID 交叉核实：`Random.prefab` 里 `agentPrefab: {fileID: 1779652455186312154, guid: e849054b62b8b8fd7981bfe9785f2a5f}`，该 GUID 对应 `RocketboxRandomAnimatedAgent.prefab.meta`）。
- `Restart()`：`Clear()` 销毁旧 agent，然后循环 `numberOfAgents` 次调用 `SpawnAgent("Agent_"+i, Util.Navmesh.RandomPose())`。
- `SpawnAgent(name, pose)`：`Instantiate(agentPrefab, Vector3.zero, Quaternion.identity)` → 取子物体上的 `IVI.INavigable`（即挂了 `SFAgent` 的那个子物体，见下）→ 设置 `position`/`rotation` = 传入的 `pose` → 挂到 `Agents` 容器物体下 → `agent.InitDest(Util.Navmesh.RandomPose().position)` 分配第一个随机目标。
- `Update()`：每帧遍历所有 agent，`agent.CloseEnough()`（`IVI.INavigable.CloseEnough()`，与目标点水平距离 ≤ `Parameters.CLOSE_ENOUGH_MIN_DIST`=1.0m）为真就立刻 `InitDest()` 一个新随机点——持续的随机游走。

**`RocketboxRandomAnimatedAgent.prefab` 本身的结构**（已读取完整 YAML）：单个 GameObject，只挂了一个组件 —— `Agents/RandomAvatar.cs`（脚本 GUID `417508598fbc14cf495db457d81cf0b1` 已交叉核对）。字段：

```yaml
animationController: {guid: d3b7ebf8605e64140b49960db196f694}   # 一个共享的 RuntimeAnimatorController
avatars:
  - Female_Adult_01.prefab
  - Female_Adult_02.prefab
controller: 0   # LowLevelControl.SF
isPlayer: 0
```

`RandomAvatar.cs`（`Assets/Scripts/SEAN/Scenario/Agents/RandomAvatar.cs`）的 `Awake()` 逻辑：

1. 维护一个**静态**（跨所有 `RandomAvatar` 实例共享）的 `avatarsList`：首次或用完时，从 `avatars` 数组拷贝一份；每次 `Awake()` 从中不放回随机抽一个当 `avatarPrefab`，抽完从 `avatarsList` 移除——保证在一轮里各 agent 不重复挑同一个模型，用完再重新洗一轮。
2. `Instantiate(avatarPrefab, transform.position, transform.rotation)` 生成真正的角色模型子物体（如 `Female_Adult_01` 的实例），设置它的 `Animator.runtimeAnimatorController = animationController`。
3. `if (SEAN.instance) controller = SEAN.instance.AgentController;`（`LowLevelControl` 枚举，`SF`/`ORCA`，全局唯一配置，非按 agent 区分）。
4. 非 player 时：`controller==SF` → `avatarObject.AddComponent<IVI.SFAgent>()`；`controller==ORCA` → `AddComponent<ORCA.Agent>()`（注意 `ORCA.Agent.UpdateVelocity()` 是未实现占位，会抛异常，当前实际只用 SF）。
5. `avatarObject.transform.parent = transform;`——真正带 `SFAgent`/`INavigable` 的物体是 `RocketboxRandomAnimatedAgent` 的**子物体**，这也是为什么 `RandomABNavAgentManager.SpawnAgent()` 里要用 `GetComponentInChildren<IVI.INavigable>()` 而不是 `GetComponent<>()`。

**Rocketbox 角色资源现状**：`Assets/Resources/Prefabs/Rocketbox/` 下现成约 90+ 个角色 prefab（`Female_Adult_01~17`、`Male_Adult_01~21`，以及 `Business_*`/`Medical_*`/`Police_*`/`Military_*`/`Fire_*`/`Sports_*` 等职业装扮）。当前 `RocketboxRandomAnimatedAgent.prefab` 只引用了其中 2 个（`Female_Adult_01`/`02`），其余全部未被任何现有 prefab 引用，是现成可用的"外观占位"素材库。**未发现明确标注为"elderly"（老年）或"child"（儿童）体型/骨架的角色**——需确认这批 Rocketbox 资源里是否存在体型差异化的模型（本次只做了文件名扫描，未逐一在 Editor 里查看网格）。

### 1.2 GraphNav 场景对比：spawn 位置来自场景手摆数据

`IVI.NavManager`（`Assets/IVI/Scripts/Navigation/NavManager.cs`）的 `Run()` 协程：

```csharp
allNavNodes = GameObject.FindObjectsOfType<NavNode>();
foreach (var node in allNavNodes) {
    for (int i = 0; i < node.spawnCount; i++) {
        var pos = node.transform.position + <node.radius 范围内随机偏移>;
        var sfRandom = Instantiate(agentPrefab, pos, Quaternion.identity);
        ...
    }
}
```

`NavNode`（`Assets/IVI/Scripts/Navigation/NavNode.cs`）是一个挂在场景 GameObject 上的组件，`public float radius; public int spawnCount = 0;`——**这就是"现成的 spawn 点/区域定义"**：策划/研究者在 Editor 里手摆若干个带 `NavNode` 组件的空物体，每个指定"这个点生成几个人、半径多大范围内随机撒开"。

**已用脚本核实 Lab 场景的实际情况**：`Lab.unity` 的 `/Environment/PedestrianControl` 下有两个子物体（均为嵌套 prefab 实例，已用 GUID 反查确认）：

| 子物体名 | 来源 prefab |
|---|---|
| `Graph` | `Assets/IVI/Prefabs/LabGraph.prefab` |
| `Random` | `Assets/Resources/SEAN/PedestrianBehaviors/Random.prefab`（即上一节的 `RandomABNavAgentManager` 配置） |

`LabGraph.prefab` 内部已核实包含至少 3 个手摆节点（`Node 2`/`Node 3`/`Group Node 3`），每个都有具体的 `m_LocalPosition`（如 `{-2.698, 0, 1.844}`）和 `spawnCount: 1`。即 **Lab 场景本身就已经在用"预先摆放的 Transform + 每点生成数量"这套模式**，只是目前专属于 `GraphNav`，且节点还带有图导航（Dijkstra 占用均衡）相关的字段，不是单纯的 spawn 点。

### 1.3 个体移动层：`Agents.Base.Update()` 与调制插入点

`Assets/Scripts/SEAN/Scenario/Agents/Base.cs`：

```csharp
void Update()
{
    velocity = UpdateVelocity();   // Base.cs:70，抽象方法，SFAgent 在此算出社会力速度
    Move();                        // 消费 velocity 驱动旋转/动画
}
```

`public Vector3 velocity { get; protected set; }`——**setter 是 `protected`**，这意味着一个"外挂"的、不继承 `Agents.Base` 的普通 `MonoBehaviour`（比如独立的 `PersonalityModulator` 组件）**无法从外部直接改写 `velocity` 字段**，即便它能 `GetComponent<IVI.SFAgent>()` 拿到该组件的引用。这是本次分析新确认的一个关键约束（`PEDESTRIAN_SCENARIO_DESIGN.md` §2.3 提出的"独立组件 + `GetComponent`"方案在这一点上需要补充说明，见 §2.3）。

`IVI.SFAgent`（`Assets/IVI/Scripts/SFAgent.cs`）是当前唯一实际使用的 `Agents.Base` 子类，只 `override UpdateVelocity()`，未重写 `Update()`——确认在 `Update()` 里 `UpdateVelocity()` 之后插入调制逻辑，能覆盖所有走 `SFAgent` 的行人。

### 1.4 机器人位置获取方式

`IVI.SFAgent.Start()` 已经这样做（代码原文）：

```csharp
if (SEAN.SEAN.instance != null)
{
    var robot = SEAN.SEAN.instance.robot.gameObject;
    ...
    neighbors.Add(robot);
}
```

`SEAN.SEAN.instance.robot` 属性（`Assets/Scripts/SEAN/SEAN.cs:376`）：遍历 `/SEAN/Robots` 下的子物体，要求恰好一个 `activeSelf`，返回其 `Scenario.Robot` 组件（找不到/多于一个会直接 `throw`）。

`Scenario.Robot`（`Assets/Scripts/SEAN/Scenario/Robot.cs`）用 `new` 关键字覆盖了 `MonoBehaviour.transform`：

```csharp
public new Transform transform { get { return base_link.transform; } }
public Vector3 position { get { return transform.position; } }
```

即**任何代码只要能拿到 `SEAN.instance`，就可以用 `SEAN.instance.robot.position`（或 `.transform.position`）拿到机器人在世界坐标系下的实时位置**，不需要额外的 Find/Tag 查找。`SFAgent` 现在只是把机器人当作一个"邻居"参与社会力计算（`CalculateAgentForce()` 里对 `robot` 分支用 `robotRepulsion`——一个 `[0.5, 1.0]` 区间内**每个 agent 各自随机**的阻尼系数，只影响斥力大小，不产生方向性的"主动远离/靠近"行为）——这与用户想要的"scared 主动远离/curious 主动靠近"是两回事，现有机制不能直接复用，需要在调制层新增方向性逻辑。

`Parameters.cs`（`Assets/Scripts/Agents/Parameters.cs`）是 `public struct` 里全是 `public const float`（如 `MAX_VEL = 0.6f`、`DESIRED_SPEED = 0.6f`）——**编译期常量，无法按 agent 实例区分**，这是设计 appearance 走速差异时的一个硬约束（见 §2.4）。

### 1.5 spawn 位置现状汇总

| Scenario | Manager | spawn 位置来源 | 数量来源 |
|---|---|---|---|
| `Random` | `Agents.RandomABNavAgentManager` | `Util.Navmesh.RandomPose()`——NavMesh 三角剖分上纯随机采样（`Assets/Scripts/SEAN/Util/Navmesh.cs`） | Inspector 字段 `numberOfAgents`（Random.prefab 上配置为 65） |
| `GraphNav` | `IVI.NavManager` | 场景内手摆的 `NavNode`/`GroupNavNode`（各场景各自一份 prefab，如 Lab 用 `LabGraph.prefab`），`node.transform.position` ± `node.radius` 范围随机偏移 | 每个 `NavNode.spawnCount`（逐点配置，手动叠加） |
| `Handcrafted` | `Agents.Handcrafted` | 预置 `SpawnLocations`（未在本次展开逐行验证，`SEAN_architecture_analysis.md` §3.3(c) 已提及）| 固定于预设情境逻辑 |

**结论**：现有代码里已经有两种成熟模式可以复用/参考——`Random` 的"NavMesh 随机点 + 全局数量"，以及 `GraphNav` 的"场景手摆 Transform 列表 + 逐点数量"。用户想要的"Inspector 配置多组 {appearance, personality, 数量, spawn 区域/点}"本质上是这两种模式的**参数化、可分组版本**，不需要发明新的底层 spawn 机制（`Instantiate` + `INavigable.InitDest()`），只需要新的配置结构和一层分组循环。

### 1.6 关键约束：下游系统只认"当前唯一 active scenario"

已用 `grep` 核实，以下所有消费"行人列表"的下游系统，无一例外全部读取 `SEAN.instance.pedestrianBehavior.agents`（或 `.groups`）——即 §1.4/`PEDESTRIAN_SCENARIO_DESIGN.md` 已分析过的、**当前唯一 active 的那个 `PedestrianBehavior.Base` 子类**暴露出来的列表：

| 文件 | 用法 |
|---|---|
| `Scenario/Agents/PositionPublisher.cs:35` | `foreach (... person in sean.pedestrianBehavior.agents)` → 发布 `/social_sim/agent_positions` |
| `Metrics/Metrics.cs:37,195` | `sean.pedestrianBehavior.agents.Length` / 遍历统计碰撞等指标 |
| `Scenario/GroupPublisher.cs:32` | `sean.pedestrianBehavior.groups` → 发布 `/social_sim/group_positions` |
| `Scenario/Classifier/SituationRuleBased.cs:68` | `sean.pedestrianBehavior.groups` → 情境分类 |

**这是本次分析中对整体设计影响最大的一条事实**：如果新的 spawn 机制生成的行人不属于"当前 active scenario"暴露的 `agents`/`groups`，那么这些行人虽然能在 Unity 里正常走动、正常受 `SFAgent` 社会力影响，但**不会被发布到 ROS（位置/群组 topic），不会被计入碰撞/个人空间指标，也不会被情境分类器感知**——对着重"机器人-行人交互"实验的这个项目而言，这几乎是硬性要求，直接决定了 §2.5 的共存方案只能走"新增一个 scenario"这条路，而不是做一个完全游离在 scenario 机制之外的独立 spawner。

---

## 2. 设计方案

> 以下均为**建议方案，代码未修改**。核心原则：复用 §1 已确认的两条现有链路（`Instantiate` + `AddComponent<SFAgent>` + `INavigable.InitDest()`），只新增配置层和调制层，不改动 `SFAgent`/`RandomAvatar`/`RandomABNavAgentManager` 已有行为。

### 2.1 总体架构

```
新增 scenario："ConfigurableSpawner"（与 GraphNav/Random/Handcrafted/Playback/LabStudy/None 平级）
  │
  ├─ PedestrianBehavior.ConfigurableSpawner : PedestrianBehavior.Base   [新文件]
  │     · 挂在 /SEAN/PedestrianBehaviors 下，注册进 Resources/SEAN/PedestrianBehaviors.prefab
  │     · Start(): 在 /Environment/PedestrianControl 下找 "ConfigurableSpawnerRoot" 子物体 SetActive(true)
  │     · groups/agents 转发给下面的 Manager
  │
  ├─ PedestrianSpawner : Agents.BaseAgentManager                        [新文件]
  │     · 挂在场景 /Environment/PedestrianControl/ConfigurableSpawnerRoot 下（每个场景各自一份，
  │       与 Lab 的 LabGraph.prefab / Random.prefab 挂法一致）
  │     · Inspector: List<SpawnGroupConfig> spawnGroups   ← 用户要的"可增删列表"
  │     · Restart()/Awake(): 遍历 spawnGroups，每条按 count 循环 Instantiate + 挂 SFAgent + 挂调制器 + InitDest
  │     · Update(): 沿用 RandomABNavAgentManager 的模式——CloseEnough() 就重新分配下一个目标
  │
  └─ 每个被 spawn 出来的 agent GameObject 上：
        IVI.SFAgent            （既有类，不改）
        PedestrianModulator     [新文件，非 Agents.Base 子类，独立 MonoBehaviour]
        （appearance 对应的角色模型作为子物体，沿用 RandomAvatar.cs 的 Instantiate 方式）
```

### 2.2 `PedestrianSpawner` 组件设计

**挂载位置**：场景专属，`/Environment/PedestrianControl/ConfigurableSpawnerRoot`（每个要用这个功能的场景——Lab/Outdoor/Warehouse——各自放一份，与现有 `Graph`/`Random`/`HandcraftedSocialSituations` 子物体同级）。理由：严格遵循 §1.2 已确认的"场景级数据 vs. scenario 选择器"分离约定（`SEAN_architecture_analysis.md` §3.6 给出的标准新增步骤），不新造一套装配逻辑。

**数据结构**（Inspector 可编辑列表，用 `[System.Serializable]` class + `List<>`，这是 Unity 默认 Inspector 渲染"可增删列表"的标准做法，`RandomABNavAgentManager` 里 `numberOfAgents`/`agentPrefab` 这类扁平字段已经证明简单字段能被 Inspector 正确渲染，`List<[Serializable] class>` 同理会渲染成带 `+`/`-` 按钮的可展开数组）：

```csharp
public enum AppearanceType { Simple, Elderly, Child, Distracted, TBD }
public enum PersonalityType { Scared, Curious, Surprised, Indifferent }
public enum SpawnAreaMode { Point, Area, TransformList }

[System.Serializable]
public class SpawnGroupConfig
{
    public string label;                    // 仅供 Inspector 辨识，不参与逻辑
    public AppearanceType appearance;
    public PersonalityType personality;
    public int count;

    public SpawnAreaMode areaMode;
    // Point/Area 用：
    public Vector3 areaCenter;
    public Vector3 areaSize;                // Area 模式下 Box 半径范围内随机采样，Point 模式下忽略
    // TransformList 用：
    public List<Transform> spawnPoints;     // 手动拖场景里的空物体作为候选点，逐个或随机取
}

public class PedestrianSpawner : Agents.BaseAgentManager
{
    public List<SpawnGroupConfig> spawnGroups;
    public AppearanceMapping[] appearanceMappings;   // 见 §2.6
    ...
}
```

**spawn 位置的三种模式**（对应用户"坐标点 / 区域 / Transform 列表"的要求）：

- `Point`：直接用 `areaCenter`（一个 Inspector 可拖的 `Vector3`，或退化为拖一个单独的 `Transform`）。
- `Area`：以 `areaCenter` 为中心、`areaSize` 为半边长的一个 Box 内做水平面随机采样，可复用 `Util.Navmesh.RandomHit(nearPosition, distance, maxDistance)`（`Assets/Scripts/SEAN/Util/Navmesh.cs:72`，已有"以某点为圆心、半径内随机 + NavMesh 吸附"的现成实现，只需要把 `distance` 参数换算成 Area 的对角线范围）保证采样点落在 NavMesh 上。
- `TransformList`：直接复用 §1.2 已确认的 `GraphNav`/`LabGraph.prefab` 模式——一组手摆在场景里的空物体，`List<Transform>` 在 Inspector 里可拖拽引用，spawn 时按顺序或随机从列表取点。这是三种里**风险最低**的一种，因为完全复刻了 Lab 场景已经在用的手摆-引用套路。

**数量**：`SpawnGroupConfig.count`（每组一个 `int`），与 `RandomABNavAgentManager.numberOfAgents` 是同一思路，只是拆到了每个 `{appearance, personality, spawn区域}` 组合各自一份，而不是全场景一个总数。

**spawn 执行逻辑**（伪代码，未写实现细节，仅示意与现有链路的对应关系）：

```csharp
void Restart() {
    Clear();
    foreach (var group in spawnGroups) {
        for (int i = 0; i < group.count; i++) {
            Vector3 pos = SamplePosition(group);              // 按 areaMode 采样
            GameObject avatarRoot = Instantiate(agentContainerPrefab, pos, RandomRotation);
            // 沿用 RandomAvatar.cs 的做法：按 appearance 挑一个角色 prefab 实例化成子物体
            GameObject visual = SpawnAppearance(group.appearance, avatarRoot.transform);
            var sfAgent = visual.AddComponent<IVI.SFAgent>();
            var modulator = visual.AddComponent<PedestrianModulator>();
            modulator.personality = group.personality;
            modulator.walkSpeedMultiplier = LookupSpeedMultiplier(group.appearance);
            agents.Add(sfAgent);
            sfAgent.InitDest(Util.Navmesh.RandomPose().position);  // 首个目标，复用现有随机游走
        }
    }
}
```

`Update()` 沿用 `RandomABNavAgentManager.Update()` 的"到达就换下一个随机目标"模式即可，不需要另外设计导航层——用户的需求是"个性化怎么走"，不是"去哪走"，A→B 目标分配继续用最简单的随机游走或者复用 GraphNav 的图占用均衡都可以，属于可选增强，不是本次核心诉求。

### 2.3 `PersonalityModulator` 设计

**关键澄清（对 `PEDESTRIAN_SCENARIO_DESIGN.md` §2.3 建议的补充）**：该文档建议"新写一个独立 `MonoBehaviour`，通过 `GetComponent<IVI.SFAgent>()` 拿到 `SFAgent` 后……调制"，但 §1.3 已确认 `Agents.Base.velocity` 的 setter 是 `protected`——一个独立组件**拿不到写权限**去改这个字段。要让调制真正生效，只有两条路：

**路线 A（推荐，改动面最小）**：在 `Agents/Base.cs` 里新增一个 `protected virtual` 钩子（这是本设计**唯一必须触碰的既有核心文件**，且是纯增量、不改变默认行为）：

```csharp
// Base.cs，在 velocity = UpdateVelocity(); 这一行之后插入：
void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());   // 原来是 velocity = UpdateVelocity();
    Move();
}

protected virtual Vector3 ModulateVelocity(Vector3 socialForceVelocity)
{
    var modulator = GetComponent<IVelocityModulator>();   // 接口，见下
    return modulator != null ? modulator.Modulate(socialForceVelocity, this) : socialForceVelocity;
}
```

`IVelocityModulator` 是一个新增的小接口（新文件），`PedestrianModulator`（挂在与 `SFAgent` 同一个 GameObject 上的独立 `MonoBehaviour`）实现它：

```csharp
public interface IVelocityModulator
{
    Vector3 Modulate(Vector3 socialForceVelocity, Agents.Base self);
}
```

这样设计的好处：

- `Base.cs` 只改一行 + 新增一个 3 行的 `virtual` 方法，`SFAgent`/`ORCA.Agent`/`Playback.Agent` 全部不受影响（未挂 `IVelocityModulator` 组件的 agent，`GetComponent` 返回 `null`，行为与改动前完全一致）。
- `PedestrianModulator` 保持是**独立组件**，不需要为每种 personality 派生新的 `Agents.Base`/`SFAgent` 子类——用户要求的"4 种 personality × 5 种 appearance 任意组合"天然满足（组合方式是"同一个 GameObject 上挂哪个 modulator 实例、实例的字段设成什么值"，不是类型层面的组合爆炸）。
- 后续如果要接入更复杂的 belief-state（跨帧状态机），`PedestrianModulator` 自己在 `Update()`/协程里维护内部状态即可，`Modulate()` 只是它对外的"这一帧最终修正量"接口，不影响 `Agents.Base` 契约。

**路线 B（不推荐，仅记录为什么排除）**：把 `velocity` 的 setter 从 `protected` 改成 `public`，让 `PersonalityModulator` 在自己的 `LateUpdate()` 里直接读写 `SFAgent.velocity`。排除理由：`Move()` 在 `Base.Update()` 里紧跟着 `UpdateVelocity()` 同步执行，如果外部组件要在两者之间插入修改，必须精确控制 Unity 的脚本执行顺序（`[DefaultExecutionOrder]`），比路线 A 的显式方法调用更脆弱、更难调试，且需要放宽一个现有字段的访问级别（改变既有契约），不符合"最小改动"原则。

**四种 personality 的具体调制逻辑（设计层面，参数需实测调整，已标注需确认）**：

| Personality | 触发条件 | 调制行为 | 实现要点 |
|---|---|---|---|
| `Scared` | `distanceToRobot < scaredRadius` | 在 `socialForceVelocity` 上叠加一个方向为 `(self.position - robot.position).normalized`、幅度随距离减小而增大的分量 | 无状态，纯函数式，每帧根据 `SEAN.instance.robot.position` 现算；叠加后按 personality 自己的速度上限（可以略高于 `Parameters.MAX_VEL`，模拟"受惊加速"）夹紧 |
| `Curious` | `distanceToRobot` 在 `[curiousMinDist, curiousMaxDist]` 区间 | 叠加方向为 `(robot.position - self.position).normalized` 的分量，靠近机器人 | **需确认/需实测调参**：这个吸引分量必须明显小于 `SFAgent` 内部在近距离产生的排斥力（`CalculateAgentForce()` 里的 `Parameters.A * Exp(overlap/B)` 项），否则会和社会力互相拉扯抖动；`curiousMinDist` 起到"看够近就不再挤"的下限保护 |
| `Surprised` | 检测机器人**首次**进入 `surprisedRadius`（上升沿，需要跨帧记忆"上次是否已经在范围内"） | 触发后 `pauseDuration` 秒内把速度强制衰减到接近 0（可以是直接返回 `Vector3.zero` 或对 `socialForceVelocity` 做插值衰减更自然），之后正常恢复，并设一个冷却期避免同一次接近重复触发 | **有状态**：`PedestrianModulator` 内部维护 `isPaused`/`pauseTimer`/`hasTriggered` 等字段，`Modulate()` 是纯函数接口，状态推进可以放在同一组件的 `Update()`/`LateUpdate()` 里，或者干脆在 `Modulate()` 内部用 `Time.deltaTime` 自增计时器（不需要额外协程） |
| `Indifferent` | 无 | 原样返回 `socialForceVelocity`，即纯 SFAgent 行为 | 最简单：这类 agent **可以干脆不挂 `PedestrianModulator` 组件**，`Base.ModulateVelocity()` 的 `GetComponent` 返回 `null` 天然走恒等分支；如果为了 Inspector 上"看得到这个 agent 是 indifferent"的可读性想保留组件，也可以挂一个 `personality=Indifferent` 的空调制 |

`distanceToRobot`/`robot.position` 的取值方式：`SEAN.SEAN.instance.robot.position`（§1.4 已确认的访问路径），每帧在 `Modulate()` 里直接取，不需要缓存或订阅——这是一个廉价的属性访问（内部只是 `base_link.transform.position`），没有性能问题。

### 2.4 appearance 走速差异如何生效（与 personality 共用同一插入点）

§1.4 已确认 `Parameters.DESIRED_SPEED`/`Parameters.MAX_VEL` 是**编译期 `const`**，无法按 agent 实例覆盖，也就是说**不能通过"传参数进 SFAgent"来实现走速差异**，因为 `SFAgent.CalculateGoalForce()`/`UpdateVelocity()` 内部直接写死引用 `Parameters.XXX`。

可行方案：appearance 的"走速差异"和 personality 的"方向性调制"复用**同一个 `IVelocityModulator.Modulate()` 插入点**——`PedestrianModulator` 除了 `personality` 字段外再加一个 `walkSpeedMultiplier`（由 spawn 时按 appearance 类型查表设置），在 `Modulate()` 末尾对结果做一次 `result *= walkSpeedMultiplier` 缩放。

这样设计的好处是**不需要两个互相打架的组件**去抢同一个 hook——一个 agent 上只有一个 `PedestrianModulator` 实例，同时装着 personality 和 appearance 两组参数，`Modulate()` 内部顺序是"先算 personality 的方向性叠加分量 → 再整体乘以 appearance 的速度倍率"，顺序明确、不存在多组件叠加时"谁先谁后"的歧义。

**需确认**：`walkSpeedMultiplier` 的合理取值范围——如果乘出来的速度显著超出 `SFAgent` 内部力学参数（`Parameters.A`/`B`/`T` 等）原本假设的物理范围，可能出现转向/避障不自然的观感（这是 `PEDESTRIAN_SCENARIO_DESIGN.md` §3 已经点出的同一类风险）。建议实现时先用较保守的范围（如 0.7×~1.3×）实测调整，而不是任意取值。

### 2.5 与现有 RandomAvatar / scenario 机制共存方案

**结论：新增一个平级的 scenario（`PedestrianBehavior.ConfigurableSpawner`），而不是做游离于 scenario 机制之外的独立 spawner。**

理由已在 §1.6 详细论证：`Metrics`/`PositionPublisher`/`GroupPublisher`/`SituationClassifier` 全部只读"当前唯一 active scenario"暴露的 `agents`/`groups`。如果新 spawner 不挂进这套机制，生成的行人对 ROS 侧和指标系统是"隐形的"——这对機器人-行人交互实验是不可接受的。

具体接入方式，**完全复用 `SEAN_architecture_analysis.md` §3.6 已经写明的"新增自定义行人行为"标准步骤**，不需要发明新流程：

1. 新文件 `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/ConfigurableSpawner.cs`，继承 `PedestrianBehavior.Base`，`Start()` 里找场景下 `/Environment/PedestrianControl/ConfigurableSpawnerRoot` 并 `SetActive(true)`，`groups`/`agents` 转发给 `PedestrianSpawner`（§2.2）。
2. 新文件 `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs`，继承 `Agents.BaseAgentManager`（与 `RandomABNavAgentManager`/`IVI.NavManager` 同级，共享同一个单例槽位——这意味着 `ConfigurableSpawner` 场景激活时，`Random`/`GraphNav` 的 Manager 不会同时存活，这与现有 `Random` 和 `GraphNav` 互斥的行为完全一致，符合 §1.6 揭示的"scenario 之间本来就是互斥"的既有约束，不是本设计新引入的限制）。
3. 在 `Resources/SEAN/PedestrianBehaviors.prefab` 里新增一个子物体 `ConfigurableSpawner`，挂上第 1 步的脚本——这样它就能通过现有的 `SetPedestrianBehavior("ConfigurableSpawner")` / `-scenario ConfigurableSpawner` 命令行参数 / Editor 下拉框来激活，**不需要新写任何切换 UI**。
4. 在每个要用这个功能的场景（至少 Lab）的 `/Environment/PedestrianControl` 下新增 `ConfigurableSpawnerRoot` 子物体，挂上第 2 步的 `PedestrianSpawner` 组件（即 Inspector 配置列表的挂载点）。

**是否能和 `GraphNav`/`Random` 的背景人群同时跑（比如"图导航人流 + 几个手动配置的 scared/curious 行人"叠加）**：现有单例机制下**不能直接支持**——`BaseAgentManager.instance` 是全局唯一槽位，谁的 `Awake()` 后跑谁的 GameObject 被 `Destroy`。如果确实需要"背景人群 + 个性化行人"同时存在，需要在 `ConfigurableSpawner` 场景内部**自己再生成一批背景随机游走的行人**（即 `PedestrianSpawner` 的某个 `SpawnGroupConfig` 就配成 `personality=Indifferent` + 数量拉高 + `Point`/`Area` 模式覆盖整个可行走区域，效果等价于 `Random` 场景），而不是试图让两个 scenario 并存——这是最不破坏现有单例约束的做法。

**是否复用 `RandomAvatar.cs`**：不建议直接修改它（它目前被 `Random.prefab` 实际使用，改动有回归风险）。建议新写一个逻辑上是它"参数化版本"的类（如 `Agents/AppearanceAvatar.cs`），核心区别是：`RandomAvatar` 从**单一固定数组**里纯随机挑角色，新类需要"按 `AppearanceType` 从对应的候选数组里挑"——两者共享同一套"`Instantiate` 角色 prefab → 设 `Animator` → `AddComponent<SFAgent>`"的骨架，属于合理的平行实现而非重复造轮子。

### 2.6 appearance 模型接口预留：Howard 角色资源到位后怎么接

**现阶段占位方案**：`AppearanceMapping` 是一个数据驱动的映射表（Inspector 可配置），而不是代码里写死的 `switch`：

```csharp
[System.Serializable]
public class AppearanceMapping
{
    public AppearanceType type;
    public GameObject[] avatarPrefabs;              // 该 appearance 下可用的候选模型（可以放 1 个或多个做随机变体）
    public float walkSpeedMultiplier = 1.0f;         // 见 §2.4
    public RuntimeAnimatorController overrideAnimatorController;  // 留空则用默认的 animationController
}
```

`PedestrianSpawner.appearanceMappings` 是这个类型的数组，5 种 appearance 各配一行。**现阶段**（Howard 角色资源未到位）：

- `Simple`：直接引用现成的 `Female_Adult_01`/`Male_Adult_01` 等（`Assets/Resources/Prefabs/Rocketbox/`，§1.1 已确认有 90+ 个未被占用的现成角色）。
- `Elderly`/`Child`：**需确认**——当前 Rocketbox 库内未发现明确的老年/儿童体型模型（本次只做了文件名扫描），现阶段按用户需求"参数标签 + 走速差异"处理，即复用成年人模型、只是 `walkSpeedMultiplier` 调低（模拟老年人）或调整（儿童），不追求视觉区分。
- `Distracted`：同样复用成年人模型作为占位，"分心"这个语义现阶段更适合归入 personality 层（比如可以理解成 `Curious` 的一种变体：更少避让、更晚反应）而不是外观层——**需确认**：`Distracted` 是否应该实际上是 personality 的第 5 种，还是确实只是 appearance 层的"低头看手机"体态标签，这点用户需求描述里写在 appearance 里，本设计按字面先放在 appearance，但建议在实现前和用户确认语义边界，因为它会影响到底是接进 `AppearanceMapping.walkSpeedMultiplier` 还是接进 `PersonalityModulator`。
- 第 5 种 appearance（"待定"）：`AppearanceType` 枚举先占位一个 `TBD` 值，`AppearanceMapping` 数组对应一行先随便指个占位模型，等确定语义后只需要改枚举名字和这一行映射，不影响其余代码。

**将来 Howard 角色资源到位后的接入步骤**（不需要碰 `PedestrianSpawner`/`AppearanceAvatar` 任何一行代码）：

1. 新角色 prefab 只要满足 `Agents.Base.Start()` 里两处硬依赖的接口约定（已读代码确认，`Base.cs:51-64`）：根物体或子物体上有 `SkinnedMeshRenderer`（用于算 `agentHeight` 定碰撞体尺寸）+ `Animator`（`applyRootMotion`/`cullingMode` 会被代码设置，且需要能配上 `Forward`/`Strafe`/`Idling` 这几个现有 Animator 参数名，或提供自己的 `overrideAnimatorController` 走同名参数）——即和现有 `Rocketbox_*` 系列 prefab 一样的"外形接口"，直接就能塞进 `avatarPrefabs` 数组。
2. 打开 Inspector，把 `AppearanceMapping` 对应那一行的 `avatarPrefabs` 从占位 Rocketbox 模型换成 Howard 发来的新 prefab（可以多个一起放，spawn 时随机挑一个做变体），调整 `walkSpeedMultiplier`。
3. 如果新角色的骨骼/动画系统和现有的共享 `animationController` 不兼容（**需确认**，取决于 Howard 资源是否走 Humanoid rig），把新角色自己的 `RuntimeAnimatorController` 填进 `overrideAnimatorController` 字段，`AppearanceAvatar.cs`（实例化时）按"有 override 就用 override，没有就用默认”的简单判断处理，这一分支在最初写这个类时就应该预留好（哪怕现阶段所有行都留空、走默认），避免将来又要改代码。

### 2.7 personality × appearance 正交组合怎么落地

不需要任何额外机制——`SpawnGroupConfig` 本身就是 `{appearance, personality, count, spawn区域}` 的一条记录，Inspector 列表让用户**手动枚举想要的组合**（比如"3 个 scared+elderly 在 A 点，5 个 curious+child 在 B 区域，10 个 indifferent+simple 撒满全场"），天然支持任意子集组合，不需要在代码里生成 4×5 的笛卡尔积再逐一配置数量（那样对大多数不需要全组合的实验场景反而是过度设计）。

---

## 3. 改动面汇总

| 文件 | 状态 | 说明 |
|---|---|---|
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | **修改**（唯一必须碰的既有核心文件） | 1 行 `Update()` 改动 + 新增 1 个 3 行的 `protected virtual ModulateVelocity()` 方法，默认恒等，不影响任何现有子类行为 |
| `Assets/Scripts/SEAN/Scenario/Agents/IVelocityModulator.cs` | 新增 | 接口定义 |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | 新增 | personality + appearance 走速的统一调制组件 |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs` | 新增 | 继承 `BaseAgentManager`，Inspector 配置列表 + spawn 循环 |
| `Assets/Scripts/SEAN/Scenario/Agents/AppearanceAvatar.cs` | 新增 | `RandomAvatar.cs` 的"按 appearance 筛选"平行版本 |
| `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/ConfigurableSpawner.cs` | 新增 | 场景级 scenario 选择器，接入现有 `SetPedestrianBehavior` 机制 |
| `Assets/Resources/SEAN/PedestrianBehaviors.prefab` | **修改**（数据/prefab 层面，非代码） | 新增一个子物体注册 `ConfigurableSpawner` |
| 各场景 `/Environment/PedestrianControl` | **修改**（数据/prefab 层面） | 新增 `ConfigurableSpawnerRoot` 子物体，挂 `PedestrianSpawner` 并配置 Inspector 列表 |
| `Assets/Scripts/SEAN/Scenario/Agents/RandomAvatar.cs`、`RandomABNavAgentManager.cs`、`Assets/IVI/Scripts/SFAgent.cs` | **不改** | 完全复用现有行为，新机制通过组合（新组件挂载）接入，不修改原有类 |

---

## 4. 风险与需确认清单

- **需确认**：Rocketbox 现成 90+ 角色 prefab 里是否存在体型上明显区别于成年人的模型（老年/儿童），本次只做了文件名扫描（`Assets/Resources/Prefabs/Rocketbox/*.prefab` 文件名均是 `职业_性别_编号` 命名，未见 elderly/child 字样），未在 Editor 里逐一打开查看网格比例。
- **需确认**：`Distracted` 这个 appearance 类型的语义边界——究竟是纯外观标签（体态/低头看手机），还是应该实际上是第 5 种 personality。会影响它最终接进 `AppearanceMapping` 还是 `PersonalityModulator`，建议实现前与用户/Howard 再确认一次。
- **需确认**：Howard 未来发来的角色资源是否为标准 Humanoid rig，能否直接复用现有共享 `animationController`（`Forward`/`Strafe`/`Idling` 参数名），还是需要各自带一份 `overrideAnimatorController`——§2.6 已预留字段但未验证实际兼容性。
- **需确认/需实测调参**：`Curious` personality 的吸引力分量与 `SFAgent` 内部近距离排斥力（`Parameters.A`/`B`）之间的相对强度，需要实机调参避免抖动；`walkSpeedMultiplier` 的合理取值范围同样需要实测（建议先从 0.7×~1.3× 开始）。
- **需确认**：`PedestrianSpawner` 与 `GraphNav`/`Random` 共享 `BaseAgentManager.instance` 单例槽位，意味着 `ConfigurableSpawner` 激活时无法让 `GraphNav` 的图人流同时运行——§2.5 已给出"用 `Indifferent` 的大数量 `SpawnGroupConfig` 模拟背景人群"的绕行方案，但如果用户后续明确需要"图导航人流 + 个性化行人"物理上同时共存（而不是都用同一套随机游走逻辑模拟），需要更大的架构改动（比如把 `BaseAgentManager` 的单例约束放宽为可多实例共存），本设计未覆盖这种更复杂的需求，需要用户确认是否有这个诉求。
- **需确认**：`Agents.Base.Update()` 里 `ModulateVelocity()` 调用 `GetComponent<IVelocityModulator>()` 每帧执行一次的性能开销——对典型规模（几十个行人）应该可忽略，但如果未来场景人数显著增加（比如 GraphNav 65 人规模），建议在 `Start()` 里缓存该引用而不是每帧 `GetComponent`，这是实现阶段的优化细节，不影响本设计的整体结构。
