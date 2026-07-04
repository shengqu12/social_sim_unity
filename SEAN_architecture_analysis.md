# SEAN (social_sim_unity) 架构分析

> 分析对象：`~/Desktop/research/social_navigation/social_sim_unity`（Unity 端），
> 配套参考：`~/sim_ws/src/social_sim_ros`（ROS Noetic 端）
> 本文档为只读代码分析产出，未修改工程任何文件。
> 分析日期：2026-06-30

## 目录

1. [顶层结构](#1-顶层结构)
2. [核心脚本目录 Assets/Scripts/SEAN/](#2-核心脚本目录-assetsscriptssean)
3. [行人行为系统（重点）](#3-行人行为系统重点)
4. [机器人控制链路（重点）](#4-机器人控制链路重点)
5. [场景与启动](#5-场景与启动)
6. [已知问题记录](#6-已知问题记录)

---

## 1. 顶层结构

### 1.1 `Assets/` 主要目录一览

| 目录 | 作用 |
|---|---|
| `Assets/Scripts/SEAN/` | **SEAN 框架核心 C# 代码**，本工程的业务逻辑主体，详见第 2 节 |
| `Assets/Scripts/{Agents,Cameras,Communication,Game,Humans,Robots,Sensors}/` | SEAN 命名空间之外的外围脚本：相机控制、遥测/演示用的额外 pub/sub（`Communication/`）、UI/游戏流程（`Game/`）、机器人小工具脚本等。不属于本次重点，但 `Communication/` 中有若干独立的 ROS pub/sub（如 `HeadPoseSubscriber`、`RosConnectorPortFromEnv`），排查连接问题时可能相关 |
| `Assets/Scripts/SocNavBench/`、`Assets/Scripts/SocNavBenchSean/` | 与 SocNavBench 基准测试对接的脚本，独立子系统，未展开 |
| `Assets/IVI/` | Yale Interactive Machines 实验室自研的**行人图导航 + 社会力（Social Force）仿真库**（命名空间 `IVI`）。`Scripts/SEAN/Scenario/Agents/Base.cs`（行人物理移动基类）与 `PedestrianBehavior/GraphNav.cs` 都依赖它，是行人系统的底层引擎，详见第 3 节 |
| `Assets/Resources/SEAN/` | **运行时可加载的 SEAN 预制体（prefab）库**，与 `SEAN.cs` 中按名字查找的子物体一一对应（`Controllers`、`PedestrianBehaviors`、`Robots`、`Players`、`Tasks`、`TF`、`Metrics`、`Input`、`Clock`、`Display`、`StartAndGoal`、`Util`），详见 §1.2 |
| `Assets/Robots/` | 机器人 URDF 源文件与部分整机 prefab（`Fetch.prefab`、`Jackal.prefab`、`turtlebot3_burger.prefab`、`warthog.prefab`，以及 `URDF/` 下 a1/Fetch/Jackal/Kuri/P3DX/Turtlebot3/Warthog 的 URDF+网格），详见第 4 节 |
| `Assets/Scenes/SEAN/` | 三个可玩场景：`Lab.unity`、`Outdoor.unity`、`Warehouse.unity`，详见第 5 节 |
| `Assets/Environments/` | 场景美术资产（`Textures`、`Models`、`Materials`、`Prefabs`、`Images`、`Skyboxes`），含 `Environments/SocNavBench` 子目录 |
| `Assets/ExternalAssets/` | 第三方导入资产：`RosSharp`（ROS# 库）、`Microsoft-Rocketbox`（行人角色模型库）、`UMA`（Unity 多用途角色）、`kmeans-clustering-unity`（`Tasks/BusyABNav.cs` 用于人群聚类）、`VolumetricLines`、`CityScape`、`Streets`、`AllSkyFree` 等 |
| `Assets/RosMessages/` | ROS-TCP-Connector 自动生成的 C# 消息类，按 ROS package 分文件夹（`Geometry`、`Nav`、`Sensor`、`Rosgraph`、`SocialSimRos`〈项目自定义消息，如 `MTrialInfo`/`MSceneInfo`〉等） |
| `Assets/RosSharpMessages/` | 另一套（较旧的）ROS# 消息定义，与 `RosMessages/` 并存，需进一步确认当前是否仍在使用 |
| `Assets/StreamingAssets/` | 运行时数据文件（如回放用的轨迹 CSV 等） |
| `Assets/UnivTextures/`、`Assets/VolumetricLines/` | 纹理资产、体积光线（light-saber 风格路径可视化）渲染资产包 |

### 1.2 `Assets/Resources/SEAN/` 关键 prefab

这是 `SEAN.cs` 在 `Awake()` 中按子物体名字查找并挂载的**运行时配置库**，每个 prefab 对应 SEAN 根物体下的一个功能分组：

- `SEAN.prefab` — 顶层 prefab，场景中拖入即完成整套框架装配
- `Controllers.prefab` — 挂载 `VelocityController` 等 `ControlSubscriber`
- `PedestrianBehaviors.prefab`（+ `PedestrianBehaviors/GraphNav.prefab`、`Random.prefab`、`HandcraftedSocialSituations.prefab`）— 行人行为选择器，详见第 3 节
- `Robots.prefab`（+ `Robots/Kuri.prefab`、`P3DX.prefab`、`Unitree A1.prefab`；第 4 个子物体 `Jackal` 直接引用 `Assets/Robots/Jackal.prefab`）— 可选机器人集合
- `Tasks.prefab` — 各 `Tasks.Base` 子类实例（`RobotTasks`）
- `Players.prefab`、`Player/Player.prefab` — 玩家角色
- `StartAndGoal.prefab` — 起点/终点标记物体
- `TF.prefab` — 各 TF/Odometry publisher
- `Metrics.prefab`、`Input.prefab`、`Clock.prefab`、`Display.prefab`、`Util.prefab`
- `Sensors/`（`OverheadCamera.prefab`、`RaycastLaserScanner.prefab`、`ROSCameraDepth.prefab`、`ThirdPersonCameraParent.prefab`）
- `Visualizations/`（`AttentionValCanvas.prefab`、`GlobalPlanVisualizer.prefab`）
- `LabStudy/block{1,2,3}` — 用户研究场景中的实体道具贴图/材质
- `RuleBasedSituationClassifier.prefab`、`ML.prefab`

---

## 2. 核心脚本目录 `Assets/Scripts/SEAN/`

`SEAN` 命名空间下所有 ROS 交互均为**去中心化注册**：每个组件在自己的 `Start()` 里独立调用 `ROSConnection.instance.Subscribe<...>(...)` 或 `ros.Send(...)`，不存在统一的 topic 注册表。`SEANRosInterface.cs` 看起来像"中枢接口"，实际只订阅了一个 topic（见下）。ROS-TCP-Connector 端口号由 `SEAN.cs` 的 `-ros-tcp-port` 命令行参数设置到 `ROSConnection.instance.RosPort`（仅非 Editor 构建生效）。

### 2.1 顶层文件

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `SEAN.cs` | 无（全局单例/装配器） | — | 整个框架的中枢单例，负责发现并持有各功能子树（见第 5 节） |
| `SEANRosInterface.cs` | **Subscriber** | `/social_sim/control/task/new`（`MBool`） | 收到 `true` 且非评测模式时调用 `sean.robotTask.StartNewTask()`，用于外部触发新一轮 task |

### 2.2 Control/（详见第 4 节）

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Control/ControlSubscriber.cs` | **Subscriber**（抽象基类） | `Topic = "/mobile_base_controller/cmd_vel"`（`MTwist`，可在 Inspector 覆盖） | 定义 `CmdVelMessage` 抽象方法；`Update()` 中还支持本地手柄/键盘直接旁路 ROS 输入 |
| `Control/VelocityController.cs` | 继承自 ControlSubscriber，同一 topic | 同上 | 实际驱动 Kuri/P3DX/Jackal/Unitree A1 的运动学控制器，`FixedUpdate` 直接设置 `Rigidbody.velocity/angularVelocity` |
| `Control/MotorController.cs` | **Subscriber**（独立组件） | 每轮子一个 topic（Inspector 指定，`MFloat64`，对应 `diff_drive_controller` 输出的轮速指令） | 基于 `WheelCollider` 的 PID 力矩驱动；**目前只有 `Assets/Robots/warthog.prefab` 使用**，未接入当前可选的 4 台机器人 |
| `Control/A1PlaybackController.cs` | 无（非 ROS 驱动） | — | 从本地 CSV (`a1mocap.csv`) 回放四足机器人腿部动作，与 `cmd_vel` 无关 |

### 2.3 TF/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `TF/BaseTransformPublisher.cs` | **Publisher**（共享基类） | 由子类指定 | 提供 `PublishIfNew`，统一走 ROSClock 打时间戳、去重后 `ros.Send` |
| `TF/WorldTransformPublishers.cs` | **Publisher** | `/map_to_odom`、`/map_to_base_link`（`MPoseStamped`） | 每帧发布机器人在 map 系下的位姿；`map_to_odom` 首帧锁定后不再更新 |
| `TF/RelativeTransformPublisher.cs` | **Publisher** | `/base_link_to_<FrameID>`（`MPoseStamped`，`FrameID` 每实例可配） | 通用相对变换发布器，供各传感器/坐标系复用 |
| `TF/OdometryPublisher.cs` | **Publisher** | `topicName` 按实例配置；**目前仅 `Kuri.prefab` 配置为 `/robot_odom`** | 约 2Hz（`publishMessageFrequency=0.5s`）发布 `nav_msgs/Odometry`，通过位姿差分计算线速度/角速度；P3DX/Jackal/Unitree A1 均未挂载此组件 |

### 2.4 Sensors/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Sensors/LaserScanner.cs` | 抽象基类 | — | 定义 `Scan()`/`ScanPeriod()`/`InitializeMessage()` 接口 |
| `Sensors/RaycastLaserScanner.cs` | 实现类 | — | 用 `Physics.Raycast` 环形扫描实现 2D 激光雷达 |
| `Sensors/LaserScanPublisher.cs` | **Publisher** | `Topic = "/laser"`（默认，可覆盖），`MLaserScan` | `[RequireComponent(LaserScanner)]`，按 `scanPeriod` 周期调用 `Scan()` 并发布 |

### 2.5 Scenario/PedestrianBehavior/（详见第 3 节）

基类 `Base.cs` + 子类 `GraphNav.cs`、`Random.cs`、`Handcrafted.cs`、`LabStudy.cs`、`Playback.cs`、`None.cs`。均非直接 ROS pub/sub，而是聚合 `groups`/`agents` 供 `Scenario/GroupPublisher.cs`、`Scenario/Agents/PositionPublisher.cs`、`Scenario/Classifier/*` 等发布器读取。

### 2.6 Tasks/

`Tasks/Base.cs` 是抽象基类，同时也是 **Publisher**：`Topic = "/move_base_simple/goal"`（`MPoseStamped`），每 `publishInterval`（10s）或新 task 开始时发布目标点。核心抽象方法 `protected abstract bool NewTask()`（子类实现"如何设置起点/终点"），可重写 `CheckNewTask()`（任务完成判定逻辑）。

| 子类 | 场景名 | 起点/终点逻辑 |
|---|---|---|
| `RandomABNav.cs` | `RandomAB` | NavMesh 上随机取点 |
| `BusyABNav.cs` | `CrowdedAB` | 对行人位置做 k-means 聚类，起点/终点分别贴近/远离最大人群簇 |
| `JoinGroup.cs` | `JoinGroup` | 终点=某个 `TrackedGroup` 的空位 |
| `LeaveGroup.cs` | `LeaveGroup` | 起点=群组内部，终点=NavMesh 随机点（需 `GraphNav` 场景） |
| `Handcrafted.cs` | 跟随 `SocialSituation` | 转发给 `PedestrianBehavior.Handcrafted.NewScenario()` |
| `CustomStartGoal.cs` | — | 直接引用设计师放置的起点/终点 `GameObject` |
| `LabStudy.cs` | `LabStudy` | 多路径点（A–F），按 `taskID` 选路线，用距离判定替代超时判定 |

### 2.7 Metrics/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Metrics/Metrics.cs` | 无（聚合器） | — | 统计碰撞/侵入个人-亲密空间次数、机器人轨迹、路径长度等；订阅 `robotTask.onNewTask` 在每个 trial 开始时 `Reset()` |
| `Metrics/CountCollisions.cs` | 无（物理触发器） | — | 在机器人上挂 3 层同心触发碰撞体（个人空间/亲密空间/硬碰撞），分类事件并记录责任归属 |
| `Metrics/MetricsPublisher.cs` | **Publisher** | `/social_sim/metrics`（`MTrialInfo`） | 每帧发布当前 trial 的完整统计信息（轨迹、碰撞计数等） |

### 2.8 Input/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Input/InputPublisher.cs` | **Publisher** | `/social_sim/cmd_vel`（`MTwist`）、`/social_sim/trigger`（`MBool`） | 本地键盘/手柄遥控，逐帧发布；**注意与机器人真实运动 topic `/mobile_base_controller/cmd_vel` 不同**，是否有 ROS 侧节点做转发需进一步确认；Unity 内部另有 `ControlSubscriber.Update()` 的本地旁路逻辑直接消费 `sean.input.CmdVel`（见 §4） |

### 2.9 ROSClock/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `ROSClock/ROSClockPublisher.cs` | **Publisher**（单例） | `"clock"`（`MClock`） | 仿真时钟权威来源，`FixedUpdate` 中变化即发布；同时向全局提供 `UpdateMHeader()` 供其它组件打时间戳 |

### 2.10 Mapping/

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Mapping/MapCreator.cs` | **Publisher**（离线工具，非常规运行时组件） | `/short_map/compressed`、`/tall_map/compressed`（`MCompressedImage`） | 通过 `Physics.OverlapBox` 递归四分空间栅格化生成俯视占据地图，代码注释明确写着"生产环境请禁用此脚本" |

### 2.11 Scenario/ 其余组件

| 文件 | 角色 | Topic | 说明 |
|---|---|---|---|
| `Scenario/GroupPublisher.cs` | **Publisher** | `/social_sim/group_positions`（`MPoseArray`） | 发布所有行人群组中心点 |
| `Scenario/Publisher.cs` | **Publisher** | `/social_sim/scene_info`（`MSceneInfo`） | 发布场景名/机器人起终点/人数/群组数等 |
| `Scenario/Agents/PositionPublisher.cs` | **Publisher** | `/social_sim/agent_positions`（`MPoseArray`） | 发布所有行人位置 |
| `Scenario/Classifier/SituationClassifier.cs` + `SituationRuleBased.cs` | **Publisher**（抽象+规则实现） | `/social_sim/situations/rule_based/{empty,down_path,cross_path,join_group,leave_group}`（`MFloat32`） | 基于角度/距离规则判断当前社交情境 |
| `Scenario/Trajectory/*.cs` | 无 | — | `TrackedAgent`（可追踪标记）、`TrackedTrajectory`（轨迹采样+速度估计）、`TrackedGroup`（基于 O-space 理论的群组检测）、`LinearTrajectory`（最小二乘拟合速度方向） |
| `Display/PlanVisualizer.cs` | **Subscriber** | Inspector 配置（`MPath`，推测为 `move_base` 全局/局部规划路径），需进一步确认具体实例化的 topic 字符串 | 将 ROS 路径渲染为体积光线 |

---

## 3. 行人行为系统（重点）

### 3.1 两层抽象：场景级"行为选择器" vs. 单体"移动实现"

SEAN 的行人系统实际由**两条独立的类层次**组成，容易混淆：

```
SEAN.Scenario.PedestrianBehavior.Base   ← "场景级"：决定当前用哪种行人玩法/激活哪组环境物体
        │  (抽象属性 groups / agents，仅用于向 Metrics/Classifier/GroupPublisher 汇报)
        │
SEAN.Scenario.Agents.Base : IVI.INavigable : Trajectory.TrackedAgent   ← "个体级"：单个行人如何走路
        │  (抽象方法 UpdateVelocity()，即速度模型)
        ├── IVI.SFAgent            社会力模型（Social Force Model），当前实际使用的行走引擎
        ├── SEAN.ORCA.Agent        ORCA 避障模型，UpdateVelocity() 抛 NotImplementedException（占位未实现）
        └── Scenario.Agents.Playback.Agent   回放真实数据集轨迹，UpdateVelocity() 直接返回外部设定速度
```

第三层是**目标分配/生成管理器**：

```
SEAN.Scenario.Agents.BaseAgentManager（单例基类）
    ├── RandomABNavAgentManager   随机 A→B 导航（Random 场景）
    ├── Agents.Handcrafted        预设社交情境（JoinGroup/LeaveGroup/DownPath/CrossPath）
    └── IVI.NavManager            图导航 + 群组占用均衡（GraphNav 场景）
```

### 3.2 `PedestrianBehavior.Base`（`Assets/Scripts/SEAN/Scenario/PedestrianBehavior/Base.cs`）

```csharp
public abstract class Base : MonoBehaviour
{
    protected GameObject pedestrianControl;   // = GameObject.Find("/Environment/PedestrianControl")
    public abstract Trajectory.TrackedGroup[] groups { get; }
    public abstract Trajectory.TrackedAgent[] agents { get; }
    public virtual string scenario_name { get; }
    public void SetScenarioName(string name);
}
```

- `Start()` 会查找 **`/Environment/PedestrianControl`**（注意：这是"每个场景"下的一个 GameObject，与下面提到的 `/SEAN/PedestrianBehaviors` 不是同一个东西）。
- 子类通常在自己的 `Start()` 里，在 `pedestrianControl.transform` 的子物体中按名字查找一个"环境专属"子树（例如 `GraphNav` 找名为 `"Graph"` 的子物体），`SetActive(true)` 后把实际的生成/寻路工作委托给对应的 Agent Manager 或 `IVI.NavManager`。
- `groups`/`agents` 两个抽象属性只是把当前活跃行人/群组列表"暴露"出去，供 `Metrics`、`SituationClassifier`、`GroupPublisher`、`Agents/PositionPublisher` 等消费，**本身不参与走路逻辑**。

现有 6 个子类：

| 子类 | 文件 | scenario_name | 在 `/Environment/PedestrianControl` 下查找的子物体 | 委托的 Manager |
|---|---|---|---|---|
| `GraphNav` | `PedestrianBehavior/GraphNav.cs` | `Graph_<name>` | `"Graph"` | `IVI.NavManager`（`graph.GetComponent<IVI.NavManager>()`） |
| `Random` | `PedestrianBehavior/Random.cs` | `Random` | `"Random"` | `Agents.RandomABNavAgentManager`（经 `BaseAgentManager.instance` 单例转型） |
| `Handcrafted` | `PedestrianBehavior/Handcrafted.cs` | `Handcrafted_<situation>` | `"HandcraftedSocialSituations"` → `"Handcrafted"` | `Agents.Handcrafted`（`NewScenario(situation, env, spawnLocations)`） |
| `Playback` | `PedestrianBehavior/Playback.cs` | `Playback` | `"Playback"` | `Agents.Playback.LoadAllAvatar`（CSV 轨迹回放） |
| `LabStudy` | `PedestrianBehavior/LabStudy.cs` | `LabStudy` | （自身子物体 `positions`） | 无（用户研究专用，`agents` 恒为单个 `TrackedAgent`） |
| `None` | `PedestrianBehavior/None.cs` | — | 无 | 无（空场景，`groups`/`agents` 恒返回空数组） |

### 3.3 行人如何被 spawn 及分配 A→B 目标

三条典型路径（对应上表委托的 Manager）：

**(a) Random 场景** — `Agents/RandomABNavAgentManager.cs`
- `Restart()`：清空现有行人，循环 `numberOfAgents`（默认 65）次调用 `SpawnAgent()`，从 `agentPrefab`（内含 `IVI.SFAgent`）实例化，`transform.position` 设为 `Util.Navmesh.RandomPose()`，随后立即 `agent.InitDest(pos)` 分配一个随机 NavMesh 目标。
- `Update()`：每帧检查每个 agent 的 `CloseEnough()`（默认阈值 1.0m），到达后立刻 `InitDest()` 一个新随机点 —— 典型的"随机游走"式 A→B→C→...。

**(b) GraphNav 场景** — `IVI/Scripts/Navigation/NavManager.cs`
- 场景内预先摆放 `NavNode`/`GroupNavNode`（图节点）与 `NavEdge`（图边，带 `Constraint` 流向约束）。
- `Run()` 协程：在每个节点按 `spawnCount` 生成行人（`GroupNavNode` 则调用 `AddMember` 摆到群组槽位），构建邻接矩阵，再对每个 agent 调用 `UpdateAgentGoal()`。
- `UpdateAgentGoal()`：核心是**占用均衡 + Dijkstra**——比较各节点当前占用 (`nodeOccupancy`) 与期望占用 (`nodeDesired`，来自 `GroupNavNode.groupSize`)，随机挑一个目标节点（群组节点优先补位），用 Dijkstra（边权 = 距离 × `edgeDesired` 流向系数）求出下一跳节点，再调用 `agent.InitDest(nextNode, offsetPos)`。
- 单个 agent 到达目标（`INavigable.Coroutine()` 检测 `CloseEnough()`）后，会在群组节点停留 `GetTime()` 秒（模拟"聊天"），再重新调用 `NavManager.inst.UpdateAgentGoal(this)` 领取下一个目标 —— 形成持续的图上随机游走。

**(c) Handcrafted 场景** — `Agents/Handcrafted.cs`
- `NewScenario(situation, ...)` 根据 4 种 `SocialSituation`（`JoinGroup`/`LeaveGroup`/`DownPath`/`CrossPath`）在预置的 `SpawnLocations` 处生成行人或群组；`DownPath`/`CrossPath` 用固定环形路径点列表循环分配目标（`agentGoals` 字典），`JoinGroup`/`LeaveGroup` 通过 `IVI.GroupDataLoader` 加载预设群组构型并 `SpawnGroup()`。

### 3.4 单个行人如何移动（`Agents.Base` → `UpdateVelocity()`）

- `Agents.Base`（`Scenario/Agents/Base.cs`）在 `Start()` 里挂载 `NavMeshAgent`（仅用于半径参数，实际 **禁用** `nma.enabled=false`，不使用其寻路）、`Rigidbody`、`CapsuleCollider`、`Animator`。
- `Update()`：`velocity = UpdateVelocity()`（抽象方法，子类实现具体速度模型）→ `Move()`：用 `nearestGoalPoint`（NavMesh 路径下一个拐点）计算朝向角，限幅角速度后 `transform.RotateAround`，再把速度换算为局部坐标驱动 `Animator` 的 `Forward`/`Strafe` 参数做行走动画。
- `ComputePath(dest)`：调用 Unity `NavMesh.CalculatePath()` 计算路径拐点（存于 `nmPath`），由 `IVI.INavigable.Coroutine()` 按 `plannerFPS`（默认 5Hz）周期性调用 `PlanNavigation()` → `ComputePath()` 保持重规划。
- 目前唯一投入使用的速度模型是 **`IVI.SFAgent`**（社会力模型，`IVI/Scripts/SFAgent.cs`）：`UpdateVelocity()` = 目标引力（`CalculateGoalForce`）+ 行人间斥力（`CalculateAgentForce`，含对机器人特殊处理的 `robotRepulsion` 阻尼）+ 墙体斥力（`CalculateWallForce`，基于 `BoxCollider` 障碍物），力学参数集中在 `Scripts/Agents/Parameters.cs`（`A`/`B`/`T`/`MAX_VEL` 等社会力模型经典参数）。
- `SEAN.ORCA.Agent`（`Scripts/SEAN/ORCA/Agent.cs`）是 **未实现的占位类**（`UpdateVelocity()` 直接 `throw NotImplementedException`），如果 `SEAN.AgentController` 被设为 `LowLevelControl.ORCA` 会在运行时崩溃 —— 见第 6 节。
- 具体用哪种模型由 `Agents/RandomAvatar.cs`（挂在每个行人 avatar 上）决定：读取 `SEAN.instance.AgentController`（`LowLevelControl` 枚举，`SF`/`ORCA`），据此给行人子物体动态 `AddComponent<IVI.SFAgent>()` 或 `AddComponent<ORCA.Agent>()`。

### 3.5 `Resources/SEAN/PedestrianBehaviors.prefab` 的作用

这是 `PedestrianBehavior.Base` 系子类的**注册容器**，随 `SEAN.prefab` 一起放入每个场景根节点下（成为 `/SEAN/PedestrianBehaviors`）。用 `grep m_Name` 检查其内容，子物体命名与上表一一对应：`Playback`、`Random`、`GraphNav`、`None`、`LabStudy`、`HandcraftedSocialSituation`。`SEAN.cs` 的 `pedestrianBehaviors` 属性遍历这些子物体、要求每个都挂有 `PedestrianBehavior.Base` 派生组件；`-scenario <name>` 命令行参数或 Editor 下拉框（`SEANEditor.cs`）就是通过匹配这里的 **GameObject 名字** 来 `SetActive()` 切换当前使用哪个行为。

注意：这个 prefab 只包含"行为选择器脚本"本身，**不包含**具体环境的行人生成点/图节点数据——那部分数据挂在每个场景（Lab/Outdoor/Warehouse）各自的 `/Environment/PedestrianControl` 下（三个场景经确认均有此 GameObject）。这是新增行为时最容易漏掉的一点，见 §3.6。

### 3.6 如何新增一个自定义行人行为类（操作步骤）

以新增一个假设的 `MyBehavior` 场景级行为为例：

1. **写场景级选择器脚本**：在 `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/` 下新建 `MyBehavior.cs`，继承 `SEAN.Scenario.PedestrianBehavior.Base`：
   ```csharp
   namespace SEAN.Scenario.PedestrianBehavior
   {
       public class MyBehavior : Base
       {
           MyAgentManager agentManager;
           GameObject myRoot;
           public override string scenario_name => "MyBehavior";

           public void Start()
           {
               base.Start(); // 会设置 pedestrianControl = /Environment/PedestrianControl
               foreach (Transform t in pedestrianControl.transform)
                   if (t.name == "MyBehaviorRoot") { myRoot = t.gameObject; break; }
               myRoot.SetActive(true);
               agentManager = (MyAgentManager)Agents.BaseAgentManager.instance;
               // 触发 spawn / 目标分配
           }
           public override Trajectory.TrackedGroup[] groups => agentManager.groups.ToArray();
           public override Trajectory.TrackedAgent[] agents => agentManager.agents.ToArray();
       }
   }
   ```
   必须实现的抽象成员：`groups`、`agents`（供 Metrics/Classifier/Publisher 读取）；可选覆盖 `scenario_name`。

2. **（如需自定义目标分配逻辑）写一个 Manager**：在 `Assets/Scripts/SEAN/Scenario/Agents/` 下新建类继承 `BaseAgentManager`（单例基类，`Awake()` 自动注册 `instance`），实现你的 spawn / A→B 目标分配策略（可参考 `RandomABNavAgentManager.cs` 的简单随机游走，或 `IVI.NavManager` 的图占用均衡）。

3. **（如需自定义速度模型）写一个 Agent 类**：若默认的社会力模型（`IVI.SFAgent`）不满足需求，可在 `Assets/Scripts/SEAN/Scenario/Agents/`（或任意命名空间）新建类继承 `SEAN.Scenario.Agents.Base`，只需实现：
   ```csharp
   protected override Vector3 UpdateVelocity() { /* 返回本帧期望速度向量（世界坐标，y=0） */ }
   ```
   基类已处理好 `NavMeshAgent`/`Rigidbody`/`Animator`/朝向旋转，通常不需要碰 `Move()`。

4. **在 `Resources/SEAN/PedestrianBehaviors.prefab` 中注册**：打开该 prefab，新增一个子 GameObject，命名为 `"MyBehavior"`（必须与 `scenario_name`/`-scenario` 参数匹配的名字一致，且该名字要能通过 `SetPedestrianBehavior(name)` 里的 `behavior.name == name` 精确匹配），挂上第 1 步写的 `MyBehavior` 组件。

5. **在每个要支持该行为的场景（`Lab.unity`/`Outdoor.unity`/`Warehouse.unity`）的 `/Environment/PedestrianControl` 下新增对应子树**：命名需与第 1 步脚本里 `t.name == "MyBehaviorRoot"` 查找的名字一致，内部放置生成点、图节点等场景相关数据。**这是最容易被忽略的一步** —— 场景级选择器脚本能被激活，但如果场景里没有对应数据子树，`Start()` 会直接抛异常或行人不会生成。

6. **验证**：Play 模式下用 Unity Editor 的 SEAN Inspector（`SeanEditor.cs` 提供的 "Pedestrian Control" 下拉框）选中新行为，或用命令行 `-scenario MyBehavior` 启动。

---

## 4. 机器人控制链路（重点）

### 4.1 完整调用链：ROS → Unity 轮子转动

```
ROS 端 move_base / teleop
    │  发布 geometry_msgs/Twist 到 /cmd_vel
    ▼
diff_drive_controller（ros_control，见各机器人 launch 文件 remap）
    │  重映射 /cmd_vel → /mobile_base_controller/cmd_vel
    ▼
ROS-TCP-Connector（端口 10000，SEAN.cs 的 -ros-tcp-port 配置）
    ▼
Unity: Control/ControlSubscriber.cs（抽象基类）
    │  ros.Subscribe<MTwist>("/mobile_base_controller/cmd_vel", CmdVelMessage)
    ▼
Unity: Control/VelocityController.cs（唯一实际接入的实现类）
    │  CmdVelMessage() 缓存 targetLinVelocity/targetAngVelocity
    │  FixedUpdate() 直接： rb.velocity = rb.transform.forward * targetLinVelocity
    │                       rb.angularVelocity = (0, -targetAngVelocity, 0)
    ▼
sean.robot.base_link 的 Rigidbody（运动学式直接赋速度，非力/力矩驱动）
```

- `Control/ControlSubscriber.cs`：抽象基类，默认 `Topic = "/mobile_base_controller/cmd_vel"`，`Start()` 订阅 `MTwist`；`Update()` 中还有一条**本地旁路**：若 `sean.ControlledAgent == Robot` 且 `sean.input.LocalInput` 且按住 `L1`，直接调用 `CmdVelMessage(sean.input.CmdVel)`，绕过 ROS（键盘/手柄遥操作调试用）。抽象方法 `CmdVelMessage(MTwist)` 交由子类实现真正的执行动作。
- `Control/VelocityController.cs`：**唯一被 4 台可选机器人（Kuri/P3DX/Jackal/Unitree A1）实际使用的控制器**。场景中只有一个实例（`Resources/SEAN/Controllers.prefab` 下的 `VelocityControl` 子物体），`Start()` 时通过 `sean.robot.base_link.GetComponent<Rigidbody>()` 动态绑定到"当前激活的机器人"，因此切换机器人不需要重新配置这个控制器。运动学式直接赋值 `Rigidbody.velocity/angularVelocity`（**不是**力/力矩驱动），带一个简单的看门狗：超过 `maxTimeDeltaSec`（默认 0.25s）没收到新 `cmd_vel` 就自动清零速度。文件里保留了一段注释掉的 PID 力/力矩驱动代码，当前未启用。
- `Control/MotorController.cs`：走另一套架构——订阅**每个轮子单独一个 topic**（`Std.MFloat64`，与 ROS 侧 `differential_drive_sim_controller.cpp` 通过 `wheel_left_joint_cmd`/`wheel_right_joint_cmd` 发布的轮速指令对应），驱动真实的 `WheelCollider`（PID 力矩），是"物理级"的驱动方式。**但目前只有 `Assets/Robots/warthog.prefab` 挂了这个组件，而 warthog 并未被接入 `Resources/SEAN/Robots.prefab` 的可选机器人列表**——相当于该套物理驱动链路目前是"孤立"的，见第 6 节。
- `Control/A1PlaybackController.cs`：与 `cmd_vel` 完全无关，从本地写死路径的 CSV（`Application.dataPath + "/Resources/a1mocap.csv"`）回放动作捕捉数据，逐帧设置 `ArticulationBody` 腿部关节 `xDrive.target`。**挂在 `Unitree A1.prefab` 上，意味着选择 "Unitree A1" 作为机器人时，它并不会响应 ROS 的导航速度指令，只会循环播放固定的走路动画** —— 是一个已知限制，见第 6 节。

### 4.2 机器人位姿回传 ROS（TF / Odometry）

| 文件 | Topic | 频率/触发方式 |
|---|---|---|
| `TF/WorldTransformPublishers.cs` | `/map_to_odom`（`map_to_odom` 首帧后锁定不变）、`/map_to_base_link` | 每 `Update()` 帧 |
| `TF/RelativeTransformPublisher.cs` | `/base_link_to_<FrameID>`（`FrameID` 每实例配置，用于各传感器/相机坐标系） | 每 `Update()` 帧 |
| `TF/OdometryPublisher.cs` | `topicName`（**仅 `Kuri.prefab` 配置为 `/robot_odom`**；P3DX/Jackal/Unitree A1 均未挂载） | 约 2Hz（`publishMessageFrequency=0.5s`，`FixedUpdate` 计时触发），差分位姿得到线速度/角速度，转换为 ROS FLU 坐标系后发布 `nav_msgs/Odometry` |

`Scenario/Robot.cs` 是机器人的门面类（facade），持有 `base_link`（真正的物理/碰撞体所在物体）、三个相机（`camera_first`/`camera_third`/`camera_overhead`，缺一个都会在 `Start()` 抛异常）和 `TrackedTrajectory`；对外的 `Robot.transform`/`.position`/`.rotation` 全部代理到 `base_link.transform`。

### 4.3 机器人模型来源

- `Assets/Robots/URDF/` 存放各机器人的原始 URDF + 网格：`a1`（717 行）、`Fetch`（626 行）、`Kuri`（实为 `gizmo_description`，575 行）、`Turtlebot3`（287 行，标准差速驱动 `continuous` 轮关节）、`Warthog`（475 行）。**`Jackal/urdf.urdf` 文件内容仅 7 字节的文本 `"default"`，是损坏/占位文件，并非有效 URDF**（见第 6 节）；`P3DX` 没有 URDF，只有手工准备的 `.obj`/`.fbx`/`.mtl` 网格文件。
- Unity 侧使用 `com.unity.robotics.urdf-importer`（v0.5.2，见 `Packages/manifest.json`）将 URDF 导入为 Unity prefab；但实际验证发现，**当前可选的 4 台机器人（Kuri/P3DX/Jackal/Unitree A1）均未使用 `ArticulationBody` 关节驱动来实现移动**（`ArticulationBody` 仅用于 `A1PlaybackController` 的腿部动画回放），行走全部通过 `VelocityController` 直接设置整机 `Rigidbody` 速度实现——即"轮子/关节是否真实转动"这件事在物理仿真层面并未体现，只有外观网格跟着整体刚体位移。真正做 `WheelCollider` 物理驱动的只有游离于当前机器人列表之外的 `warthog.prefab`。
- 最终可供 `SEAN.robot` 选择的整机 prefab 是 `Resources/SEAN/Robots.prefab` 的 4 个子物体：`Kuri.prefab`、`P3DX.prefab`、`Unitree A1.prefab`（均在 `Resources/SEAN/Robots/`）+ 直接引用 `Assets/Robots/Jackal.prefab` 的第 4 个子物体。

### 4.4 机器人如何被选中/装配

- **不是命令行参数**（`SEAN.cs` 的 `ParseCommandLineArgs()` 中没有 `-robot` 之类的选项，也没有在自定义 Inspector `SeanEditor.cs` 中提供机器人切换 UI）。
- `SEAN.cs` 的 `robot` 属性：遍历 `/SEAN/Robots`（对应 `Resources/SEAN/Robots.prefab`）下的所有子物体，要求**恰好一个**处于 `activeSelf == true`，否则直接抛异常。也就是说，**切换机器人 = 在 Unity Editor 里手动勾选/取消勾选 `Robots` 下对应子物体的 Active 状态**（当前没有找到运行时/命令行切换入口，需进一步确认是否有遗漏的 UI 或场景变体）。

### 4.5 如何替换或修改机器人模型（注意事项）

**Unity 端：**

1. 准备新机器人的 URDF（或直接手工搭建 prefab，如 P3DX 的做法），用 URDF-Importer 导入生成基础 prefab，确认根物体命名习惯与其它机器人一致（`base_link` 等）。
2. 挂载 `Scenario/Robot.cs`，填好 `base_link`、`camera_first`、`camera_third`、`camera_overhead`（**三个相机缺一不可，否则 `Start()` 直接抛异常**）。
3. 确认 `base_link`（或其所在物体）上有 `Rigidbody`——`VelocityController.Start()` 会用 `GetComponent<Rigidbody>()` 去拿，拿不到会在后续 `FixedUpdate` 空引用崩溃（当前代码里只判了 `rb == null` 才跳过，`Start()` 阶段本身没有防护）。
4. 若要用物理级轮速驱动（而非默认的运动学直接赋速度），需要参考 `warthog.prefab` 的做法：挂 `WheelCollider` + `Control/MotorController.cs`（每个轮子一个实例，配置对应的 `Topic`），而不是走 `VelocityController`。
5. 把新 prefab 作为子物体加入 `Resources/SEAN/Robots.prefab`（或单独存放后在该 prefab 里新增一个子物体引用它），初始 `Active` 状态设为 false，需要用时手动勾活（同时把其它机器人子物体设为 false，保证"恰好一个 active"的约束）。
6. 如需里程计反馈，参考 `Kuri.prefab` 补一个 `TF/OdometryPublisher.cs`，设置 `topicName`（须与 ROS 侧 move_base 配置的里程计 topic 对应）。

**ROS 端（`~/sim_ws/src/social_sim_ros`）：**

1. 为新机器人添加/调整 `launch/differential_drive_<robot>.launch`、`config/differential_drive_<robot>.yaml`（`diff_drive_controller` 参数：轮距、轮半径、速度/加速度限幅等），并在其中把 `move_base` 发布的 `/cmd_vel` `remap` 到 `mobile_base_controller/cmd_vel`（与 Unity 端 `ControlSubscriber.Topic` 默认值保持一致，除非你在 Unity 端每个实例改了 `Topic`）。
2. 在 `params/<robot>/` 下补齐 `move_base_params.yaml`、`costmap_common_params.yaml`、`base_local_planner_params.yaml` 等 move_base 导航参数，机器人的物理尺寸（半径/轮距）需要和 Unity 端 `Robot.radius`、碰撞体尺寸保持一致，否则规划器和实际仿真物理会不匹配。
3. 若使用 `Control/MotorController.cs` 物理驱动路径，需要保留/参考 `src/differential_drive_sim_controller.cpp`（一个 `hardware_interface::RobotHW` 实现，通过 `controller_manager` + `diff_drive_controller` 把 `cmd_vel` 转成左右轮速度指令，发布到 `wheel_left_joint_cmd`/`wheel_right_joint_cmd`，同时订阅 `wheel_left_joint_pos`/`wheel_right_joint_pos` 回读轮子位置形成闭环）；若走默认的 `VelocityController` 运动学路径，则不需要这一层，`cmd_vel` 可以直接（经 remap）打到 Unity。
4. Two 套控制链路目前在仓库里**同时存在但互不联动**（见第 6 节），改机器人前需要先确认打算走哪一条，避免误以为 `MotorController`/`differential_drive_sim_controller.cpp` 对当前 4 台机器人有效。

---

## 5. 场景与启动

### 5.1 场景列表（`Assets/Scenes/SEAN/`）

| 场景 | 说明 |
|---|---|
| `Lab.unity` | 室内实验室场景，含预烘焙光照贴图（Lightmap ×2）、NavMesh、ReflectionProbe |
| `Warehouse.unity` | 仓库场景，光照贴图更多（×9），推测规模更大 |
| `Outdoor.unity` | 室外场景，文件行数明显更多（~1600 行 vs. Lab 的几十行框架），场景内容更复杂 |

三个场景经检查均包含一个名为 `PedestrianControl` 的 GameObject（对应 §3 提到的 `/Environment/PedestrianControl`），是行人行为系统读取生成点/图节点数据的场景专属容器；同时应通过嵌套 prefab 引用了 `Resources/SEAN/SEAN.prefab`（未逐一展开验证内部 prefab 引用关系，需进一步确认细节，但从 `SEAN.cs` 强依赖 `/Environment` 与 `/SEAN` 下固定命名子物体的写法看，这是必然的装配方式）。

### 5.2 `SEAN.cs` 命令行参数

`ParseCommandLineArgs()`（`Awake()` 中调用）支持：

| 参数 | 作用 |
|---|---|
| `-ros-tcp-port <int>` | 设置 `RosConnectionPort`，非 Editor 构建下赋给 `ROSConnection.instance.RosPort` |
| `-evaluation-mode` | 置 `EvaluationMode = true`（会影响 `SEANRosInterface` 是否响应新任务触发等逻辑） |
| `-scenario <name>` | 调用 `SetPedestrianBehavior(name)`，按名字激活 `/SEAN/PedestrianBehaviors` 下对应子物体 |
| `-task <name>` | 调用 `SetTask(name)`，按名字激活 `/SEAN/RobotTasks` 下对应子物体 |
| `-task-social-situation <SocialSituation>` | 设置 `taskSocialSituation.socialSituation`（仅对 Handcrafted task 有意义） |
| `-taskID <int>` | 仅 `-task LabStudy` 时可用，否则抛异常 |
| `-completion-distance <float>` | 任务完成判定距离阈值 |
| `-max-num-tasks <int>` | 最大任务数 |
| `-task-timeout-seconds <float>` | 单任务超时时间 |

代码注释里明确写着 `// TODO: configure other command line options`，说明该参数解析并非完整/最终形态。**没有 `-robot` 参数**（机器人选择方式见 §4.4）。

### 5.3 单例初始化与装配顺序

`SEAN.cs` 的 `[ExecuteAlways] Awake()`：

1. 单例赋值（`_instance = this`，重复实例会被 `Destroy`）。
2. `GameObject.Find("/Environment")` 必须存在，否则直接抛异常终止；取其 `Environment.Environment` 组件。
3. 遍历自身（`/SEAN`）子物体，按固定名字（`PedestrianBehaviors`/`RobotTasks`/`Robots`/`Players`/`Controllers`/`Input`/`Metrics`/`StartAndGoal`）缓存引用——**这些子物体名字是硬编码约定，改名会导致运行时找不到对应功能而抛异常**。
4. 根据 `ControlledAgent` 是否为 `Player`，决定 `player` 物体是否 `SetActive`。
5. 调用 `ParseCommandLineArgs()`（此时会真正激活 `-scenario`/`-task` 指定的子物体）。
6. 非 Editor 构建下设置 ROS 端口。

Play 时的实际先后顺序（结合 Unity 生命周期与各脚本 `Start()` 依赖关系推断）：`SEAN.Awake()`（同步装配子物体引用）→ 各功能组件各自的 `Start()`（`ControlSubscriber`/`TF`/`Metrics`/`PedestrianBehavior.*` 等，此时才真正 `ROSConnection.instance.Subscribe/Send`，即 **ROS 连接注册发生在每个组件自己的 `Start()`，而不是有一个统一的"连接建立后再装配"的等待点**）→ `IVI.NavManager`/各 AgentManager 的 `Start()`/协程开始 spawn 行人 → 每帧 `Update()`/`FixedUpdate()` 驱动运动、发布话题。由于 Unity 对同帧内多个 `Start()` 调用顺序无强保证（除非用 `[DefaultExecutionOrder]`），**如果新增脚本在 `Start()` 里依赖 `SEAN.instance` 之外的其它组件已完成初始化，需要额外确认执行顺序或改用懒加载**（本仓库中大量属性用了 `if (_x != null) return _x;` 懒加载模式来规避这个问题，例如 `SEAN.robot`/`SEAN.controller`/`SEAN.pedestrianBehavior`）。

---

## 6. 已知问题记录

以下均为代码/资源层面观察到的现象，标注"需进一步确认"处表示未能从静态代码完全验证，建议实际运行验证。

### 6.1 行人系统

- **`SEAN.ORCA.Agent.UpdateVelocity()` 未实现**（`Assets/Scripts/SEAN/ORCA/Agent.cs:9`，直接 `throw new NotImplementedException()`）。若 `SEAN.AgentController` 被设为 `Scenario.Agents.LowLevelControl.ORCA`，任何行人在第一次 `Update()` 时就会崩溃。当前默认/实际使用的是 `SF`（社会力模型）。
- `Scenario/PedestrianBehavior/Playback.cs` 与 `Scenario/Agents/Playback/LoadAllAvatar.cs` 中多处 `// TODO`：`groups` 属性恒返回空数组（TODO 标记未实现群组回放）；坐标系存在 `// TODO: right -> Left handed conversion` 未完成的转换问题，可能导致回放数据方向不准确。
- `Scenario/PedestrianBehavior/LabStudy.cs` 的 `AgentGoal()` 方法体基本是空的（大段代码被注释掉 + `// TODO`），推测该 task 的目标点驱动逻辑尚未完工或已被其它机制取代。
- `Scenario/Agents/Playback/LoadAllAvatar.cs` 类头部注释 `// TODO: rename PlaybackAgentManager`，命名不规范但功能可用。

### 6.2 机器人控制链路

- **两套控制链路并存但互不联动**：
  - 链路 A（当前实际生效）：ROS `diff_drive_controller` → `/mobile_base_controller/cmd_vel`（`Twist`）→ Unity `VelocityController` → 直接赋值 `Rigidbody.velocity`（运动学，无物理轮驱动）。用于 Kuri/P3DX/Jackal/Unitree A1。
  - 链路 B（代码存在但未接入当前机器人）：ROS `differential_drive_sim_controller.cpp`（`hardware_interface::RobotHW` + `diff_drive_controller`）→ 每轮 `wheel_*_joint_cmd`（`Float64`）→ Unity `Control/MotorController.cs` → `WheelCollider` 物理力矩驱动。仅 `Assets/Robots/warthog.prefab` 使用，而 warthog **未被列入** `Resources/SEAN/Robots.prefab` 的可选机器人。
  - 修改/替换机器人时容易误用链路 B 的配置（ROS 侧 launch/yaml 仍保留完整），需先确认 Unity 端该机器人到底挂的是 `VelocityController` 还是 `MotorController`。
- **`Unitree A1` 机器人不响应 ROS 速度指令**：其挂载的是 `Control/A1PlaybackController.cs`，只回放本地写死的 mocap CSV（`Application.dataPath + "/Resources/a1mocap.csv"`），与 `cmd_vel`/ROS 完全无关。如果预期它能像其它机器人一样被 `move_base` 导航驱动，这是一个功能缺口而非 bug。
- **`Assets/Robots/URDF/Jackal/urdf.urdf` 文件损坏/占位**：内容仅 7 字节文本 `"default"`，不是合法 URDF。但 `Jackal.prefab` 是独立手工搭建的（未经过该 URDF 走 URDF-Importer 流程），运行时不受影响；若之后想用官方 Jackal URDF 重新导入，需先修复/替换此文件。
- **里程计发布不一致**：`TF/OdometryPublisher.cs` 目前只挂在 `Kuri.prefab` 上（发布 `/robot_odom`），P3DX/Jackal/Unitree A1 均未配置——如果 ROS 侧导航栈（`move_base`/AMCL 等）依赖里程计 topic，切换到这几台机器人时可能导致导航失效或需要额外配置，需进一步确认这几台机器人在 ROS 侧是否用了替代的定位来源（如直接用 TF ground-truth）。
- `Control/VelocityController.cs` 中保留了完整的 PID 力/力矩驱动实现（`Pid()` 方法及一段被注释掉的 `AddRelativeForce`/`AddRelativeTorque` 逻辑），但当前 `FixedUpdate()` 走的是更简单粗暴的直接赋速度分支——PID 分支是死代码，需进一步确认是否为未来计划启用的备用实现。
- `Assets/Robots/URDF/P3DX/` 没有 `.urdf` 文件，只有裸网格（`.obj`/`.fbx`/`.mtl`），与其余机器人走 URDF-Importer 流程不一致，是手工搭建的遗留产物。

### 6.3 Linux 兼容性

- 在 `Assets/Scripts/SEAN/`、`Assets/IVI/`、`Assets/Robots/` 范围内**未发现任何 Assimp、libdl 或原生 `.so` 插件的直接代码引用**（`grep -rli assimp` 全仓库只命中一个无关的 `.dae` 文件名字符串）。
- 工程使用 `com.unity.robotics.urdf-importer`（v0.5.2，见 `Packages/manifest.json`）做 URDF → Unity prefab 的**编辑器期**导入；该包内部的网格加载器实现细节不在本仓库代码范围内，是否在 Linux Editor 下有 Assimp/libdl 相关的已知问题**需进一步确认**（建议直接在 Linux 上跑一次 URDF 重新导入流程验证，或查阅该包的 GitHub issue）。由于仓库里的机器人网格看起来均已提前导入为 Unity 原生资产（`.prefab`/`.mat`/网格数据已落盘），正常运行时理论上不会再触发这条导入路径，只有"重新导入/新增机器人"时才可能遇到。
- 未发现 git 历史中有专门的 Linux 修复提交（`git log` 未搜到相关关键字）；README 中除标准的 Unity/dotnet 工具链说明外没有 Linux 专属注意事项。

### 6.4 其它杂项

- `Assets/RosMessages/` 与 `Assets/RosSharpMessages/` 两套 ROS 消息定义并存，实际代码中的 `using RosMessageTypes.*` 均来自前者；后者是否仍被引用**需进一步确认**（可能是历史遗留，尚未清理）。
- `Assets/Scripts/SEAN/Mapping/MapCreator.cs` 明确是"离线建图工具"，代码注释写"生产环境请禁用此脚本"，不应作为常规运行时组件理解。
- `SEAN.cs` 的命令行参数解析代码上方留有 `// TODO: configure other command line options` 注释，说明作者本身认为这部分尚不完整。
