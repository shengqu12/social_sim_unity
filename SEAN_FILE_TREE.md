# SEAN (social_sim_unity) 工程文件树

> 只读结构扫描，生成日期：2026-07-02。基于实际读代码（全部 71 个 `Assets/Scripts/SEAN/**/*.cs`
> 逐文件核实）+ 目录扫描产出，不确定处标注"用途待确认"，不编造。
> Assets 下其余（非 SEAN 命名空间）脚本仅按文件夹粒度标注，未逐文件核实。

---

## 1. 根目录关键文件

```
social_sim_unity/
├── Assets/                         — Unity 工程主体，见第 2 节
├── Packages/                       — Unity Package Manager 依赖清单（manifest.json 等）
├── ProjectSettings/                — Unity 工程设置（物理/输入/渲染管线等），未展开
├── Documentation/, docs/, _site/   — DocFX 生成的 API 文档源与产物
├── .gitmodules                     — 声明 1 个 git submodule：
│                                     Assets/ExternalAssets/Microsoft-Rocketbox
│                                     (https://github.com/yale-img/Microsoft-Rocketbox.git)
├── README.md                       — 项目说明：SEAN = Social Environment for Autonomous
│                                     Navigation；关联仓库(sim_ws/social_sim_ros/文档)+ 文档生成命令
├── SEAN_architecture_analysis.md   — 全工程架构分析（2026-06-30）：顶层目录职责、
│                                     Scripts/SEAN 核心代码、行人行为系统、机器人控制链路、
│                                     场景与启动、已知问题记录
├── PEDESTRIAN_SCENARIO_DESIGN.md   — PedestrianBehavior 场景级选择机制分析(2026-07-01)：
│                                     "More than 1 Scenario is active" 警告溯源 + "单场景单
│                                     Scenario + Inspector 可切换"改造设计（现状分析+未落地方案）
├── PEDESTRIAN_SPAWNER_DESIGN.md    — 可配置行人 Spawner 现状分析 + 设计方案(2026-07-02)：
│                                     Random/GraphNav 现有 spawn 链路核实、PedestrianSpawner /
│                                     PersonalityModulator 设计、appearance 走速接口预留
├── PERSONALITY_BEHAVIOR_DESIGN_V2.md — Personality 行为重设计 V2(2026-07-02)：Curious
│                                     三阶段状态机(Wander→Approach→Follow) + Scared/Surprised/
│                                     Indifferent 设计，PedestrianModulator.cs 当前实现的
│                                     配套设计文档
├── SCENARIO_SWITCH_PATCH.md        — "Play 只演一种 Scenario"代码补丁草案(2026-07-01，未落地)：
│                                     用 prefab/场景 YAML 实际解析核实 PedestrianBehaviors.prefab
│                                     6 个子物体的真实激活状态
├── LICENSE                         — BSD 风格许可证
└── social_sim_unity (symlink)      — 指向自身的悬空符号链接，用途待确认（可能是误建）
```

---

## 2. `Assets/Scripts/SEAN/` 核心脚本（重点，逐文件核实）

命名空间 `SEAN.*`，是本工程的业务逻辑主体。顶层入口：

- **`SEAN.cs`** — 全局单例总管：`Awake()` 时定位 `/Environment`、按子物体名字缓存
  `PedestrianBehaviors`/`RobotTasks`/`Robots`/`Players`/`Controllers`/`Input`/`Metrics`/`StartAndGoal`
  等分组，解析命令行参数(`-scenario`/`-task`/`-ros-tcp-port`等)，提供 `pedestrianBehavior`/
  `robotTask`/`robot`/`player`/`controller` 等运行时单例访问器；`selectedScenario` 枚举
  (`ScenarioSelection`) 驱动 Play 模式下自动激活哪个 pedestrian scenario，`TryActivateSelectedScenario()`
  在所有子物体 `Awake()` 跑完后、任何一个多余 scenario 被激活前就收敛成唯一 active scenario
- **`SEANRosInterface.cs`** — 订阅 `/social_sim/control/task/new`，收到 true 时调用
  `sean.robotTask.StartNewTask()`（evaluation mode 下阻止）

### 2.1 `Control/` — ROS 速度指令 → 机器人/关节驱动

- `ControlSubscriber.cs` — 抽象基类，订阅 `cmd_vel` 话题(`MTwist`)，支持本地键盘/L1 触发绕过 ROS
- `VelocityController.cs` — 继承 ControlSubscriber，机器人移动核心：区分 Rigidbody 驱动（轮式，
  如 Kuri）与 ArticulationBody 驱动（腿式，如 Unitree A1）两条路径；对腿式机器人额外做姿态纠正
  (levelAngVel)、地面高度保持(GroundTargetHeight 射线检测)、任务重启后的站立缓冲(settleSeconds)
- `MotorController.cs` — 单个轮子的 WheelCollider PID 力矩控制，订阅单独的轮速话题
- `A1PlaybackController.cs` — 从 CSV 回放 Unitree A1 的 mocap 关节角度序列，驱动腿部
  ArticulationBody 的 PD 位置控制（含修复：原 driver 的 stiffness/damping=0 会导致瘫软）

### 2.2 `Display/` — GUI/可视化叠加层

- `DisableRosPanel.cs` — 关闭 ROSConnection 自带的 HUD 面板
- `FPSDisplay.cs` — 屏幕左下角显示 FPS 文本
- `PlanVisualizer.cs` — 订阅 `nav_msgs/Path`，用体积光线(VolumetricLine)渲染机器人全局规划路径
- `VolumetricLine/VolumetricLineStripBehavior.cs` — 第三方"体积光线"渲染算法(Hillaire/移植版)，
  被 PlanVisualizer 用来画路径的光带效果

### 2.3 `Editor/`

- `SeanEditor.cs` — `SEAN` 组件的自定义 Inspector：下拉选择 Pedestrian Scenario / Robot Task，
  显示当前任务的 completionDistance 等字段

### 2.4 `Environment/`

- `Environment.cs` — 挂在 `/Environment` 上，`environment` 取第一个子物体作为环境名，
  暴露 `topViewCamera` 访问器

### 2.5 `Input/` — 本地/ROS 输入

- `GUIInputVisualization.cs` — 屏幕右下角显示当前 L1/Horizontal/Vertical 输入值
- `InputPublisher.cs` — 读取键盘或手柄输入，发布 `cmd_vel`(`/social_sim/cmd_vel`) 与
  `trigger`(`/social_sim/trigger`) 话题；含手柄/键盘轴映射

### 2.6 `Mapping/`

- `MapCreator.cs` — 四叉树递归细分场景碰撞体生成占据栅格地图，编码为 JPEG 发布
  `/short_map/compressed` 与 `/tall_map/compressed`（开发期建图工具，"生产环境应关闭"）

### 2.7 `Metrics/` — 评测指标

- `CountCollisions.cs` — 挂在有 CapsuleCollider 的对象上，追加 PersonalDistance/IntimateDistance/
  碰撞三级触发体，`OnTriggerEnter` 判断机器人是否"有责"(速度方向朝向对方)并计入相应计数
- `Metrics.cs` — 汇总指标状态机：路径长度、到目标/最近行人最小距离、人-机碰撞与三级空间侵犯计数，
  监听 `onNewTask` 在每次新任务时 Reset()
- `MetricsPublisher.cs` — 把 `Metrics` 状态打包为 `MTrialInfo` 消息发布到 `/social_sim/metrics`
  （注释说明：每次发布必须 new 一个消息对象，因为 ROSConnection.Send() 是异步序列化，复用会竞态）

### 2.8 `ORCA/`

- `Agent.cs` — `Agents.Base` 的 ORCA 实现占位，`UpdateVelocity()` 抛 `NotImplementedException`
  （即 ORCA 低层控制目前未真正实现，只有 SF/SocialForce 生效）

### 2.9 `ROSClock/`

- `ROSClockPublisher.cs` — 单例，维护仿真时钟起点，`FixedUpdate` 发布 `/clock`(rosgraph_msgs/Clock)，
  并提供 `UpdateMHeader()` 给其它 publisher 统一打时间戳
- `GUIClockVisualization.cs` — 屏幕左下角显示时钟延迟(ms)与 FPS

### 2.10 `Scenario/Agents/` — 个体行人/机器人移动层（**行人相关重点在此**）

- `Base.cs` — 个体导航基类(实现 `IVI.INavigable`)：`Update()` 中
  `velocity = ModulateVelocity(UpdateVelocity())` 是核心钩子——先算社会力速度，再经
  `ModulateVelocity()` 二次调制；`ModulateVelocity()` 默认直通，若同一 GameObject 上挂了
  `IVelocityModulator` 组件（如 PedestrianModulator）则调用其 `Modulate()`
- `BaseAgentManager.cs` — Agent 管理器单例基类(`instance` 静态槽位)，多个具体 Manager
  (RandomABNavAgentManager/Handcrafted/PedestrianSpawner) 竞争同一单例槽
- `ControlledAgent.cs` — 枚举 `Robot`/`Player`，决定当前被控制的是机器人还是玩家角色
- `LowLevelControl.cs` — 枚举 `SF`(Social Force)/`ORCA`，决定行人挂载哪种底层移动组件
- `Handcrafted.cs` — 为"手工社交情景"(DownPath/CrossPath/JoinGroup/LeaveGroup)生成/管理行人+分组
- `RandomABNavAgentManager.cs` — `Random` scenario 的行人管理器：固定数量行人在导航网格上随机游走
- `RandomAvatar.cs` — 随机挑选 Rocketbox 外观 prefab 并实例化，按 `LowLevelControl` 挂载
  `SFAgent`/`ORCA.Agent`（不重复直到列表耗尽再重置），是 PedestrianSpawner 出现前的外观加载方式
- `SocialForce.cs` — 简单数据容器：合力向量 + 是否有行人在前方/正在接近的标志位
- `Publisher.cs` / `PositionPublisher.cs` — 把当前所有行人打包发布到
  `/social_sim/agents`(含 pose+twist) 与 `/social_sim/agent_positions`(纯 pose 数组)

**行人个性化系统（本次新增，重点标注）**：

- **`IVelocityModulator.cs`** — 极小接口：`Vector3 Modulate(Vector3 socialForceVelocity, Base self)`，
  任何独立 MonoBehaviour 实现它并挂到与 `Agents.Base` 子类同一 GameObject 上，即可在
  `Base.ModulateVelocity()` 中被自动调用一次
- **`PedestrianModulator.cs`** — 行人 personality 状态机实现，`IVelocityModulator` 的具体实现：
  - `PersonalityType` 枚举：`Scared`/`Curious`/`Surprised`/`Indifferent`
  - **Scared**：在 `scaredRadius` 内叠加远离机器人的"逃跑力"，按距离线性插值强度，限速 `scaredMaxSpeed`
  - **Curious**：内部三阶段状态机 `Wander→Approach→Follow`（`detectRadius`/`followDist` 加滞回
    `*ExitMargin` 防抖），Approach/Follow 阶段主动调用 `self.InitDest()` 追向/跟随机器人（跟随时
    用位置差分估计机器人速度，适配轮式/腿式两种机器人），`IsControllingDestination` 属性告知
    `PedestrianSpawner` 此时不要用随机游走覆盖其目标点
  - **Surprised**：`surpriseRadius` 内进入的上升沿触发 `freezeDuration` 秒定身 + `cooldownDuration`
    秒冷却（冷却从触发时刻起算，而非解冻时刻）
  - **通用**：`walkSpeedMultiplier` 供 appearance 差异化走速复用同一调制钩子(见
    PEDESTRIAN_SPAWNER_DESIGN.md §2.4)；所有状态推进都在 `Modulate()` 内部同步完成，
    刻意不用独立 `Update()`，以避免与 `Base.Update()`/`SFAgent` 的执行顺序竞态
  - Indifferent 不挂本组件（`Base.ModulateVelocity()` 的 `GetComponent<IVelocityModulator>()`
    为 null 时直通）
- **`PedestrianSpawner.cs`** — `BaseAgentManager` 的具体实现，"可配置行人生成器"：
  - `SpawnGroupConfig`：Inspector 可配置的一组行人(label/personality/count/spawnPoints 列表，
    仅支持 TransformList 出生点模式)
  - `Restart()` 遍历 `spawnGroups`，每组从其 `spawnPoints` 随机选点 + navmesh 采样生成对应数量的
    `agentPrefab` 实例；非 Indifferent 的组会 `AddComponent<PedestrianModulator>()` 并设置 personality
  - `Update()` 对每个 agent 做随机游走重定向，但跳过 `IsControllingDestination == true`
    的 Curious 行人（避免和其 Approach/Follow 目标点打架）
  - appearance 本轮固定为 "Simple"：`agentPrefab` 预期挂 `AppearanceAvatar` 组件
- **`AppearanceAvatar.cs`** — PedestrianSpawner 专用的外观实例化器，与 `RandomAvatar.cs` 骨架
  (Instantiate→设置 Animator→挂 SFAgent/ORCA.Agent)一致但独立成新类：从单个候选数组里每次均匀
  随机选一个外观（不做"不重复直到耗尽"记账），完整 AppearanceType(Elderly/Child/Distracted等)
  映射表本轮未实现（用途待确认 / 见 PEDESTRIAN_SPAWNER_DESIGN.md §2.6）
- `Playback/Agent.cs` — `Agents.Base` 的回放实现，`UpdateVelocity(Pose)` 由外部(LoadAllAvatar)喂入位姿差分速度
- `Playback/LoadAllAvatar.cs` — 从 CSV 轨迹数据集(`Source: DataExtraction`)逐帧生成/移动/隐藏行人 Agent，
  按 `fps` 用协程驱动
- `Playback/PlayerAgent.cs` — 挂在玩家控制角色上的 `Agents.Base` 实现，读取 `sean.input` 转为速度

### 2.11 `Scenario/Classifier/` — 社交情境规则分类器

- `SituationClassifier.cs` — 抽象基类，定义 5 种 `Situation`(empty/downPath/crossPath/
  leaveGroup/joinGroup)并发布到 `/social_sim/situations/<type>/<name>`
- `SituationRuleBased.cs` — 具体规则实现：按距离/朝向/速度阈值判断机器人当前是否处于
  "join group"/"leave group"/"cross path"/"down path"/"empty" 情境，周期性发布

### 2.12 `Scenario/PedestrianBehavior/` — 场景级 scenario 选择器（与 2.10 的个体移动层是两层不同抽象）

- `Base.cs` — 抽象基类：`Start()` 定位 `/Environment/PedestrianControl`；子类通过同名(非
  override)`Start()` 隐藏它并手动 `base.Start()`；暴露 `agents`/`groups` 抽象属性给下游
  (PositionPublisher/Metrics/GroupPublisher/SituationClassifier)统一读取
- `None.cs` — 空场景，`agents`/`groups` 恒为空数组
- `Random.cs` — 激活 `Agents.RandomABNavAgentManager` 单例并 `Restart()`
- `GraphNav.cs` — 激活场景手摆的 `IVI.NavManager`("Graph"子物体)，行人位置来自图节点而非随机生成
- `Handcrafted.cs` — 激活 `Agents.Handcrafted` 管理器，驱动 DownPath/CrossPath/JoinGroup/
  LeaveGroup 四种手工社交情景实例的随机挑选与生成
- `LabStudy.cs` — 面向实验室人类被试研究的场景，读 Inspector 拖入的位置点集合(`positions`)
- `Playback.cs` — 激活 `Agents.Playback.LoadAllAvatar`，从数据集回放行人轨迹
- **`ConfigurableSpawner.cs`** — 本次新增，`PedestrianSpawner` 的场景级入口：定位场景下
  `ConfigurableSpawnerRoot` 子物体并激活，`agentManager = (PedestrianSpawner)BaseAgentManager.instance`
  然后 `Restart()`；`agents`/`groups` 转发给 PositionPublisher/Metrics/GroupPublisher/
  情境分类器，使得该 spawner 生成的行人对下游系统"表现得和其它 scenario 的行人一样"

### 2.13 `Scenario/` 根目录

- `Player.cs` — 玩家控制角色包装类，`Start()` 时只启用第一人称相机
- `Publisher.cs` — 发布 `/social_sim/scene_info`(`MSceneInfo`)：当前 scenario 名/起止点/行人数/分组数/环境名
- `Robot.cs` — 机器人包装类：持有 `base_link`/三个相机(first/third/overhead)引用，`ResolveCameras()`
  在 prefab 未手工挂好相机时按名字关键词查找子物体或从共享 Resources 相机 prefab 实例化兜底
- `Situation.cs` — `Situation` 值对象(name/idx/val)，`SituationClassifier`/`SituationRuleBased` 用它承载 5 种情境的当前值
- `GroupPublisher.cs` — 发布所有 group 中心点到 `/social_sim/group_positions`
- `GUISituationVisualization.cs` — 屏幕文字显示 5 种情境的实时数值(E/C/D/J/L)

### 2.14 `Scenario/Trajectory/` — 轨迹跟踪与分组几何

- `ITrackedGroup.cs` — 接口：暴露 `group` 属性，`GraphNav` 用它从图节点里找到关联的 `TrackedGroup`
- `LinearTrajectory.cs` — 固定窗口位姿队列 + 最小二乘拟合，得到平滑的速度/方向向量(MathNet.Numerics)
- `TrackedAgent.cs` — 挂 `TrackedTrajectory` 组件的行人标记类
- `TrackedGroup.cs` — 群体(o-space)检测：`Physics.OverlapSphere` 找朝向群心+低速的成员，
  `GroupMemberLocationGenerator()` 用最大空隙法计算新成员该站哪个位置(Yang's method)
- `TrackedTrajectory.cs` — 单个物体的轨迹记录器，周期性(`TrajectoryDeltaSec`)采样位姿喂给
  `LinearTrajectory`，提供 `lookingAt()`/`movingTowards()`/`nearbyAgents()` 等几何查询

### 2.15 `Sensors/`

- `LaserScanner.cs` — 抽象基类：`Scan()`/`ScanPeriod()`/`InitializeMessage()`
- `RaycastLaserScanner.cs` — 具体实现：360° 射线检测生成 `sensor_msgs/LaserScan` 数据
- `LaserScanPublisher.cs` — 按 `ScanPeriod()` 周期发布激光扫描话题（来自 Siemens 的第三方代码，
  经 SEAN 团队修改整合）

### 2.16 `Tasks/` — 机器人导航任务(起点/终点生成逻辑)

- `Base.cs` — 抽象基类：管理 `robotStart`/`robotGoal`(与 playerStart/playerGoal)、任务完成判定
  (debounce+超时)、周期性发布 `/move_base_simple/goal`；`Update()` 每帧检查是否该触发新任务；
  对 ArticulationBody 机器人用 `TeleportRoot()` 重定位（普通 Transform 赋值对物理解算无效）
- `RandomABNav.cs` — 起点/终点均为 navmesh 上随机点
- `BusyABNav.cs` — 用 K-Means 对当前行人聚类，起点选在离最大人群簇较远处，终点在簇附近
- `JoinGroup.cs` / `LeaveGroup.cs` — 起终点之一取自某个 `TrackedGroup` 的可用成员位/群成员位
- `Handcrafted.cs` — 起终点取自当前 `PedestrianBehavior.Handcrafted` 场景实例给出的 start/goal
- `CustomStartGoal.cs` — 起终点直接是 Inspector 里拖入的两个固定 GameObject
- `LabStudy.cs` — 实验室研究专用：按 `taskID` 选择一条预设途经点轨迹(4 种)，逐点推进直到完成后退出程序

### 2.17 `TF/` — 坐标变换发布

- `BaseTransformPublisher.cs` — 抽象基类，`PublishIfNew()` 按时间戳去重后发布命名变换
- `RelativeTransformPublisher.cs` — 发布某坐标系相对机器人 base_link 的变换(带可调旋转修正)
- `WorldTransformPublishers.cs` — 发布 `map_to_odom`(仅初始化一次)与 `map_to_base_link`(每帧)
- `OdometryPublisher.cs` — 用位姿差分计算线速度/角速度，发布 `nav_msgs/Odometry`

### 2.18 `Util/` — 通用工具

- `CappedQueue.cs` — 定长队列，超出容量自动 Dequeue 最老元素(供 LinearTrajectory 用)
- `CSVReader.cs` — 轻量 CSV 解析器(支持带引号字段)，供回放数据加载用
- `Geometry.cs` — Unity↔ROS 消息的位姿/向量/四元数转换 + 地面平面距离计算
- `KalmanFilterVector3.cs` — 简单标量增益卡尔曼滤波器，对 Vector3 序列平滑
- `Navmesh.cs` — 在 NavMesh 三角剖分上采样随机点/随机位姿(供各 Manager 生成行人/机器人起终点用)
- `PoseStamped.cs` — 带时间戳的位姿数据结构(LinearTrajectory 用)
- `Time.cs` — 仿真时间→毫秒/ROS `MTime` 转换
- `Unity.cs` — 反射方式运行时拷贝组件(仅显式支持 Camera 类型)

---

## 3. 其它 `Assets/Scripts/` 目录（非 SEAN 命名空间，按文件夹粒度）

- `Agents/` — 早期/外围行人脚本：`Parameters.cs`(社会力模型物理常数表，`SEAN.Scenario.Agents.Base`
  依赖的力场参数)、`RocketboxHumanCSVAvatar.cs`(整个文件已注释掉，标记 `//TODO: remove`，已废弃)、
  `RocketboxPlaybackRandomAvatar.cs`(用途待确认，未逐行读)
- `Cameras/` — 各类相机控制器：`CameraFollowRobot.cs`/`CameraFollowPlayer.cs`/`CameraFollow.cs`
  (跟随目标)、`CameraFollowMouse.cs`/`ExtendedFlycam.cs`/`TiltRotateCamera.cs`(自由视角)、
  `CameraCollision.cs`(防穿墙)、`CameraController.cs`、`DepthImageSynthesis.cs`(深度图合成，
  来自 ImageSynthesis 第三方资产)、`DisableRobotKeyboardAndCamera.cs`
- `Communication/` — SEAN 命名空间之外的独立 ROS pub/sub："RGB/深度相机发布"
  (`BaseCameraPublisher.cs`/`RGBCameraPublisher.cs`)、`LaserScanSubscriber.cs`/
  `LaserScanVisualizer.cs`/`LaserScanWriter.cs`、`HeadPoseSubscriber.cs`、
  `RosConnectorPortFromEnv.cs`(从环境变量读取 ROS 端口)、`BoolPublisher.cs`/`StringPublisher.cs`/
  `SpawnArrayPublisher.cs`/`TrialStartSubscriber.cs` — 排查连接问题时可能相关
- `Game/` — UI/游戏流程：`GameDisplay.cs`(主/侧画中画相机布局)、`StudyGameLoader.cs`/
  `StudyGamePanLoader.cs`(人类被试研究加载器)、`AttentionBarGraph.cs`/`DisplaySocialSituation.cs`、
  `ExitGame.cs`/`RestartGame.cs`/`PerformanceOptions.cs`/`CopyToken.cs`
- `Humans/`、`SocNavBench/`、`SocNavBenchSean/` — 目录存在但当前无 `.cs` 文件（可能仅含
  meta/占位，或脚本已迁移；用途待确认）
- `Robots/` — 机器人专用小工具：`KuriKeyboardController.cs`(WheelCollider 键盘控制示例)、
  `LimitRotation.cs`、`LookAtTarget.cs`
- `Sensors/Scripts/` — 独立传感器仿真库：`LiDAR/Lidar.cs`(多层激光雷达扫描)、
  `Noise/BiasNoise.cs`/`BoxMullerNoise.cs`(传感器噪声模型)、
  `Transmission/HingeSimpleTransmission.cs`

---

## 4. `Assets/Scenes/` 场景文件

```
Assets/Scenes/
├── SEAN/                           — ★ 三个主场景（当前实际使用）
│   ├── Lab.unity                    — 室内实验室场景（含 Lightmap ×2、NavMesh、ReflectionProbe）
│   ├── Outdoor.unity                — 室外城市场景（仅 NavMesh，无烘焙光照贴图）
│   └── Warehouse.unity              — 仓库场景（Lightmap ×9，光照最复杂）
├── Depreciated/                    — 11 个已废弃场景(AgentControlLabScene/RobotControlXxx/
│                                     SmallerWarehouse/University 等)，用途待确认(疑似历史遗留)
├── SocNavBench/                    — Hotel.unity / Zara.unity，SocNavBench 基准测试场景
├── SocNavDeprecated/                — ETH/Hotel/RobotControlHotel/Uni/Zara，SocNavBench 旧版场景
├── GroupFormation/                 — CocktailParty.unity，群体队形研究场景
├── Studies/                        — RobotControlLabSceneBlocksGraph.unity，人类被试研究场景
├── Test/                           — PlaybackPersonTestScene.unity / SFTestScene.unity，测试场景
└── Util/                           — EmptyScene.unity / LoadingScene.unity，工具场景
```

---

## 5. `Assets/Resources/SEAN/` 与行人角色资源

```
Assets/Resources/
├── SEAN/                            — SEAN.cs 在 Awake() 按子物体名字查找的运行时 prefab 库
│   ├── SEAN.prefab                    — 顶层 prefab，拖入场景即完成整套框架装配
│   ├── Controllers.prefab             — 挂载 VelocityController 等 ControlSubscriber
│   ├── PedestrianBehaviors.prefab     — 行人 scenario 选择器根，6 个子物体：None/GraphNav/
│   │                                   HandcraftedSocialSituation/LabStudy/Playback/Random
│   │                                   （ConfigurableSpawner 为本次新增第 7 种，需确认是否已
│   │                                   加入该 prefab 的子物体列表）
│   │   ├── GraphNav.prefab
│   │   ├── HandcraftedSocialSituations.prefab
│   │   └── Random.prefab
│   ├── Robots.prefab                  — 可选机器人集合
│   │   ├── Kuri.prefab
│   │   ├── P3DX.prefab
│   │   └── Unitree A1.prefab
│   ├── Player.prefab, Players.prefab  — 玩家角色
│   ├── Tasks.prefab                   — 各 Tasks.Base 子类实例(RobotTasks)
│   ├── TF.prefab                      — 各 TF/Odometry publisher
│   ├── Metrics.prefab / Clock.prefab / Display.prefab / Input.prefab / Util.prefab
│   ├── StartAndGoal.prefab            — 起点/终点标记物体
│   ├── RuleBasedSituationClassifier.prefab
│   ├── ML.prefab                      — 用途待确认
│   ├── Sensors/                       — OverheadCamera / RaycastLaserScanner / ROSCameraDepth /
│   │                                   ThirdPersonCameraParent 共享相机与传感器 prefab
│   ├── Visualizations/                — AttentionValCanvas / GlobalPlanVisualizer
│   └── LabStudy/                      — block1/block2/block3（仅 .meta，实际内容未展开）
└── Prefabs/
    └── Rocketbox/                    — ★ 111 个 Rocketbox 人物角色 prefab 库(Male/Female ×
                                        Adult/Business/Sports/Police/Construction/Military 等
                                        分类)，供 RandomAvatar/AppearanceAvatar 随机挑选使用
```

---

## 6. `Assets/Robots/URDF/` 机器人模型位置

```
Assets/Robots/URDF/
├── a1/urdf/a1.urdf                          — Unitree A1(四足) URDF 入口
├── Fetch/robot_description.urdf             — Fetch(移动机械臂) URDF 入口
├── Jackal/urdf.urdf                         — Clearpath Jackal(轮式) URDF 入口
├── Kuri/robot_description.urdf              — Kuri(轮式，Mayfield Robotics) URDF 入口
├── P3DX/                                    — Pioneer 3-DX：无 .urdf，仅 obj/fbx 网格
│                                              (P3DX Simplified.fbx 等)，模型来源/装配方式待确认
├── Turtlebot3/robot_description.urdf         — TurtleBot3(轮式) URDF 入口
└── Warthog/robot_description.urdf            — Warthog(轮式，Clearpath) URDF 入口
```
各机器人目录下另含 `Materials/`(逐材质 .mat) 与 `*_description/meshes`(网格，未展开)。
每个 URDF 通过 Unity URDF Importer 转换为场景 prefab 后，`Scenario/Robot.cs` 在其上挂载
`base_link`/相机引用；无根 ArticulationBody 的机器人（轮式）由 `VelocityController.DriveRigidbody()`
驱动，Unitree A1 等含根 ArticulationBody 的由 `DriveArticulation()` 驱动。

---

## 7. 跳过的生成/工具目录

`Library/`、`Temp/`、`Logs/`、`.git/`、`UserSettings/`、`_site/`(部分已在第1节提及为文档产物)、
`Output/`、`Recordings/`、`.vscode/`、`Rerun/`、`RerunData/`，以及悬空自引用符号链接
`social_sim_unity` — 均为 Unity/工具生成内容或与本次结构梳理无关，未展开。
