# SEAN 行人 Animator 结构调研 —— 面向 Mixamo 动画接入

只读调研，未修改任何工程文件。调研范围：`Assets/Scripts/SEAN/Scenario/Agents/*.cs` 与
`Assets/IVI/Controllers/BaseSFControllerNormalized.controller`（YAML 源码级解析）。

---

## 1. 行人用的 Animator Controller 定位

| Prefab | 引用脚本 | `animationController` 字段值 |
|---|---|---|
| `Assets/Resources/Prefabs/RocketboxRandomAnimatedAgent.prefab` | `RandomAvatar.cs` | `guid: d3b7ebf8605e64140b49960db196f694` |
| `Assets/Resources/Prefabs/SimpleAppearanceAgent.prefab`（新 spawner / simpleAvatars 用的那套） | `AppearanceAvatar.cs` | `guid: d3b7ebf8605e64140b49960db196f694` |

两个 guid **完全相同**，都指向：

```
Assets/IVI/Controllers/BaseSFControllerNormalized.controller
```

即：新写的 `PedestrianSpawner` / `AppearanceAvatar` 这套（simpleAvatars）和老的
`RandomAvatar`/Rocketbox 这套，**共用同一个 Animator Controller**，没有分叉。这意味着后续往
Controller 里加新 State，两套 spawner 都会同时受益（也同时受影响，需要注意不要破坏现有行为）。

`RandomAvatar.cs:8` / `AppearanceAvatar.cs:29,38`：两者都是在 `Awake()` 里
`Instantiate(avatarPrefab)` 之后，用 `animator.runtimeAnimatorController = animationController`
**运行时整体替换**掉 avatar 预制体自带的 Controller（Rocketbox 角色预制体自带的 Controller
`guid: 1780cce328bf44efc83e50128ac17813` 会被覆盖，不生效）。

---

## 2. `BaseSFControllerNormalized.controller` 结构解析

### 2.1 Animator 参数（`m_AnimatorParameters`）

| 参数名 | 类型 | 默认值 | Base.cs 是否实际驱动 |
|---|---|---|---|
| `Forward` | Float | 0 | ✅ `SetFloat` |
| `Turn` | Float | 0 | ❌ 从未被任何 SEAN 脚本 set，纯遗留 |
| `Strafe` | Float | 0 | ✅ `SetFloat` |
| `Crouch` | Bool | false | ❌ 遗留，未使用 |
| `OnGround` | Bool | true | ❌ 遗留，未使用 |
| `Jump` | Float | 0 | ❌ 遗留，未使用 |
| `JumpLeg` | Float | 0 | ❌ 遗留，未使用 |
| `Idling` | Bool | true | ✅ `SetBool`（但见 §3，实际恒为 false） |

`Turn/Crouch/OnGround/Jump/JumpLeg` 全局搜索 `Assets/Scripts/SEAN/` 均无
`SetFloat("Turn"...)` / `SetBool("Crouch"...)` 等调用 —— 这些参数是模板遗留物（見下）。

### 2.2 State Machine（唯一一个 Layer："Base Layer"）

`m_ChildStates` 里**只有两个真正被状态机使用的 State**：

```
[Entry] --(default)--> Locomotion  (默认状态, fileID -4309050833493709642)
Locomotion --[Idling == true]--> Idling   (fileID -727768650932479168)
Idling    --[Idling == false]--> Locomotion
```

- 无 `AnyState` transition（`m_AnyStateTransitions: []`），无其它 Entry transition。
- `Locomotion → Idling`：condition `Idling`（`ConditionMode:1` = If true），
  `TransitionDuration 0.25s`，`ExitTime 0.79`（但 `HasExitTime:0`，即不等待播放到该时间点，条件满足立即触发）。
- `Idling → Locomotion`：condition `!Idling`（`ConditionMode:2` = IfNot），`TransitionDuration 0.25s`。

**State 内容：**

| State | Motion | 说明 |
|---|---|---|
| `Locomotion`（默认状态） | BlendTree `fileID 3535481391988844194`（2D，见 §2.3） | 唯一承载移动动画的状态 |
| `Idling` | 单个 clip：`Idle.anim`（`guid 471152417041a2940a4295d01794f152`） | 见 §3，实际几乎不可达 |

### 2.3 真正生效的 Blend Tree（Locomotion 状态的 Motion）

`fileID 3535481391988844194`，`m_BlendType: 3`（**FreeformCartesian2D**，2D 自由笛卡尔混合），
混合参数：

- X 轴：`Strafe`
- Y 轴：`Forward`

9 个子动作（`m_Position` 为 2D 混合空间坐标，`(0,0)` 附近=静止/待机，`(0,1)`=正前方全速，以此类推）：

| Position (x,y) | TimeScale | Clip / 来源 | 备注 |
|---|---|---|---|
| (0, 1) | 0.6 | `IVI/Animations/Locomotion Pack/DefaultAvatar@WalkForward_NtrlFaceFwd.fbx` | 正前方走 |
| (0, -1) | 0.6 | `ExternalAssets/StandardAssets/Characters/ThirdPersonCharacter/Animation/WalkBack.fbx` | 正后退走，**Unity Standard Assets** 自带 Demo 角色的动画 |
| (-1, 0) | 0.6 | `IVI/Animations/NPCAnimations/Strafe_90HipsLeftFaceFwd.fbx`（子clip StrafeLeft） | 左横移 |
| (1, 0) | 0.6 | 同上 FBX（子clip StrafeRight，同一文件的另一 take） | 右横移 |
| (-0.7, 0.7) | 0.4 | `IVI/Animations/NPCAnimations/Locomotion/strafe_45.anim`（standalone .anim） | 左前方斜走 |
| (0.7, 0.7) | 0.4，`Mirror:1` | 同上 `.anim`，镜像复用同一 clip | 右前方斜走 |
| (0.7, -0.7) | 0.6，`Mirror:1` | `IVI/Animations/NPCAnimations/Locomotion/Strafe_Back.fbx` | 右后方斜走 |
| (-0.7, -0.7) | 0.6 | 同上 FBX | 左后方斜走 |
| (0, 0) | 1.0 | `IVI/Animations/NPCAnimations/Idle/Idle.anim` | **中心点=待机**，与 `Idling` State 用的是同一个 clip |

**关键发现**：`Idling` State 用的 `Idle.anim` 与 Locomotion 混合树中心点 `(0,0)` 用的是**同一个 clip**。
也就是说，即使 `Idling` State 永远进不去（见 §3），当 `Forward`/`Strafe` 都趋近 0 时，混合树权重会
自然收敛到中心点的 Idle 动作 —— 待机效果是靠混合树本身实现的，不依赖 `Idling` State 跳转。

`Locomotion` State 还声明了 `m_SpeedParameter: AnimationSpeed`，但 `m_SpeedParameterActive: 0`
（未启用）且 `AnimationSpeed` 根本不在 `m_AnimatorParameters` 列表里 —— 纯遗留死配置，实际播放速度
完全由 `animator.speed`（脚本直接设置）控制，见 §3。

### 2.4 大量"孤儿" BlendTree（未被任何 State 引用，死资产）

文件里还有 `fileID 20600002/20600004/20600006/20608386/20610787/20631403/20659883/20683409/
111584768676302085/1328237142771270242/5479271220753952673` 共 11 个 BlendTree 子资产
（名字如 `Idle`/`Walk`/`Run`/`Blend Tree`，混合参数涉及 `Turn/Jump/JumpLeg/AbruptStop` 等），
**均未被 `m_ChildStates` 中的两个 State 引用，也不存在其它 Layer**（`m_AnimatorLayers` 只有一个
"Base Layer"）。这些应是从 Unity 官方 Third Person / Locomotion 模板 Controller 拷贝过来时
残留的子资产，与 `Turn/Crouch/OnGround/Jump/JumpLeg` 几个未使用参数同源，**可以安全忽略**，不用
考虑对它们做改动。

---

## 3. Base.cs 驱动动画的机制（带行号）

文件：`Assets/Scripts/SEAN/Scenario/Agents/Base.cs`

- `L19`: `private Animator animator;` —— 私有字段，子类和同 GameObject 上的其它组件（如
  `PedestrianModulator`）**都拿不到这个引用**，没有暴露任何 public accessor。
- `L32`: `private bool applyRootMotion = true;` —— 硬编码为 `true`，Base.cs 全文（以及
  `RandomAvatar.cs`/`AppearanceAvatar.cs`/`PedestrianModulator.cs`）中**没有任何地方**再对它赋值。
  （`ThirdPersonCharacter.cs:231/272/278` 里也有 `applyRootMotion` 赋值，但那是完全不相关的
  Unity Standard Assets Demo 脚本，未被任何 SEAN 预制体使用，见前面确认。）
- `L63-65`：
  ```csharp
  animator = GetComponent<Animator>();
  animator.applyRootMotion = applyRootMotion;   // = true
  animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
  ```
- `L235-246`（`Move()` 私有方法，每帧调用）：
  ```csharp
  Vector3 animParams = Quaternion.Euler(0, -transform.eulerAngles.y, 0) * velocity;
  animParams *= animationScale;                 // animationScale = 1.0，恒等
  var idle = animParams.magnitude < idleSpeed && !applyRootMotion;

  animator.SetBool("Idling", idle);
  if (!GetType().Equals(typeof(PlayerAgent)))
  {
      animator.speed = velocity.magnitude;
  }
  animator.SetFloat("Forward", animParams.z / ANIMATION_SMOOTHING);   // ANIMATION_SMOOTHING = 0.6
  animator.SetFloat("Strafe", animParams.x / ANIMATION_SMOOTHING);
  ```

**逐项确认：**

1. **Forward/Strafe 怎么算出来**：`velocity`（世界空间，SF/ORCA 算出的社会力速度，经
   `ModulateVelocity()` 调制后）先按 `-transform.eulerAngles.y` 旋转，转换成**agent 局部坐标系**下
   的速度分量（`animParams`），`z` 分量（局部前向）→ `Forward`，`x` 分量（局部侧向）→ `Strafe`，
   两者都除以 `ANIMATION_SMOOTHING = 0.6` 做了一次放大/归一化（不是严格 0~1 归一化，只是缩放常数）。
2. **`Idling` 何时为 true —— 确认之前的分析**：`idle = animParams.magnitude < idleSpeed && !applyRootMotion`。
   由于 `applyRootMotion` 恒为 `true`（`L32`，无处修改），`!applyRootMotion` 恒为 `false`，
   逻辑与运算结果**恒为 `false`**，与 `animParams.magnitude` 无关。**确认：`Idling` 参数每帧都被
   设为 `false`，`Locomotion → Idling` 的 Transition 在当前代码路径下永远不会触发**（除非有别的地方
   手动 `SetBool("Idling", true)`，全工程搜索未发现）。待机视觉效果完全靠 §2.3 提到的混合树中心点
   实现，`Idling` State 是死状态。
3. **`animator.speed = velocity.magnitude` 的影响**：不改变哪个 clip 播放，只改变**播放速率**。
   `velocity.magnitude` 越大，动画播得越快（用来匹配步幅，避免脚滑），当速度为 0 时
   `animator.speed = 0`，动画**冻结在当前帧**（不会主动回到待机起始帧，但因为此时 Forward/Strafe
   也趋近 0，混合树权重已经收敛到 Idle 附近，视觉上问题不大）。`PlayerAgent` 例外，不受此行影响
   （`L240` 判断）。
4. **Root Motion 如何与这套参数配合驱动位移**：`Base.cs` 全文**没有任何** `transform.Translate`
   /`rb.MovePosition`/直接写 `transform.position` 的代码（`Move()` 里唯一改变 transform 的调用是
   `transform.RotateAround(...)` 处理朝向，`L232`）。也就是说：**行人在世界中的实际平移完全由
   Animator Root Motion 提供**（`applyRootMotion = true`）。`Forward/Strafe` 参数决定混合树选中/
   混合哪些 clip、`animator.speed` 决定播放速率，而这些 clip 自身携带的 Root Motion 曲线
   （见 §4 的 Bake 设置）才是真正让角色向前/侧移的位移来源。NavMesh 路径（`nearestGoalPoint`/
   `destPos`）只用来算转向角度（`L205-209`），不产生位移。

---

## 4. 现有 clip 的来源与格式

### 4.1 Avatar Rig（Humanoid 定义）

以 `Assets/Resources/Prefabs/Rocketbox/Female_Adult_01.prefab` 为例，其 Animator 组件
`m_Avatar` 指向 `Assets/ExternalAssets/Microsoft-Rocketbox/Assets/Avatars/Adults/Female_Adult_02/
Export/Female_Adult_02.fbx`，该 fbx 的 `.meta` 中：

```
animationType: 3      # Human（Humanoid）
avatarSetup: 1         # Create From This Model
```

**确认：Rocketbox 角色使用标准 Humanoid 骨骼映射**，这是能够跨来源（Rocketbox 自带 / Unity
Standard Assets / Mixamo）混用动画 clip 的前提——Mecanim Humanoid retarget 机制。

### 4.2 混合树里 9 个 clip 的来源 / rig 类型 / 是否原地

| Clip 文件 | 来源 | Rig 类型（`animationType`） | Root Motion（Bake Into Pose） |
|---|---|---|---|
| `Locomotion Pack/DefaultAvatar@WalkForward_NtrlFaceFwd.fbx` | Rocketbox 官方配套 "Locomotion Pack"（命名风格 `DefaultAvatar@...`，与 Rocketbox 一致） | Humanoid (`animationType: 3`) | XZ 未烘焙进 Pose（`keepOriginalPositionXZ: 0`）→ 保留 Root Motion；Y 烘焙（`keepOriginalPositionY: 1`）→ 无垂直位移漂移 |
| `ExternalAssets/StandardAssets/.../WalkBack.fbx` | **Unity Standard Assets** Third Person Character Demo 包（骨骼名 `Hips/LeftUpLeg/...`，非 Rocketbox 命名） | Humanoid (`animationType: 3`) | 同上模式（XZ 保留 Root Motion，Y 烘焙） |
| `NPCAnimations/Strafe_90HipsLeftFaceFwd.fbx` | IVI 自建/第三方 NPC 动画包 | Humanoid (`animationType: 3`) | 同上模式 |
| `NPCAnimations/Locomotion/strafe_45.anim` | standalone `.anim`（非 FBX 直接导入，可能是从某个 Humanoid FBX 拆分出来的独立 clip；**具体拆分来源需确认**） | 无独立 `animationType` 字段（standalone clip），但曲线含 `RootT.x/y/z`、`RootQ.x/y/z/w`、`LeftFootT.*` 等 **Humanoid 肌肉/根骨曲线属性**，确认是 Humanoid 来源烘焙出的 clip | 见曲线自带的 Loop/Bake 设置 |
| `NPCAnimations/Locomotion/Strafe_Back.fbx` | **确认为 Mixamo 来源** —— 其骨骼层级命名为 `mixamorig:Hips / mixamorig:LeftUpLeg / ...`，是 Mixamo 导出文件的标准前缀 | Humanoid (`animationType: 3`) | XZ 未烘焙（保留 Root Motion），Y 烘焙 |
| `NPCAnimations/Idle/Idle.anim` | standalone `.anim`（同 strafe_45.anim，来源需确认），也是 `Idling` State 直接用的 clip | 曲线含 Humanoid Root/Foot 属性，确认 Humanoid | `m_LoopTime:1`，`m_LoopBlendPositionXZ/Y/Orientation: 1`（循环边界做了位置/朝向的融合处理，是"原地循环"待机动作） |

**这是本次调研最重要的发现之一**：`Strafe_Back.fbx` 的骨骼命名前缀 `mixamorig:` 证明 **Mixamo
来源的动画早已经混在当前这套 Locomotion Blend Tree 里并正常工作**——即当前系统已经有
"Rocketbox Humanoid Avatar + Mixamo Humanoid clip 混合retarget" 的先例，技术路径是验证过的，
不存在兼容性障碍。

**Bake 设置的具体 Unity 语义（Bake Into Pose 复选框方向）依据惯例推断，未在 Unity Editor GUI
里逐个打开确认 —— 标记为"需确认"**，但从数值上看（所有 locomotion 类 clip 一致地
`keepOriginalPositionY:1` + `keepOriginalPositionXZ:0`），行为模式（Y 无位移、XZ 保留位移）与
"角色靠 Root Motion 走位、垂直方向不因步态产生起伏"的预期完全吻合，可信度高。

### 4.3 Rig 类型结论

**所有当前使用中的 clip 均为 Humanoid**，没有 Generic 骨骼。Mixamo 导出时只要在 Mixamo 网站或
Unity 导入设置里选择 **Humanoid**（而不是默认的 Generic）rig，即可直接对上 Rocketbox 角色的
Avatar 做 retarget，不需要额外重定向工具。

---

## 5. 接入新 Mixamo 动画的推荐方式

### 5.1 现状：没有为"加新动画"预留任何接口

- `AppearanceAvatar.cs`（`L18`）和 `RandomAvatar.cs`（`L8`）都只有一个
  `public RuntimeAnimatorController animationController` 字段，**没有** `overrideAnimatorController`
  或类似的按 personality/按 avatar 切换 Controller 的预留字段。
- `PedestrianModulator.cs` 里 `PersonalityType.Surprised` 分支（`ModulateSurprised()`，见该文件
  约 L130 起）**已经实现了"惊吓"行为**：检测到机器人进入 `surpriseRadius` 时，冻结速度
  `freezeDuration` 秒（`return Vector3.zero`），带 `cooldownDuration` 冷却。但这只是把
  `velocity` 归零，**没有触发任何专属的"惊讶"动画**——冻结时 Forward/Strafe 趋近 0，视觉上只是
  "停下变成待机"，不会有明显的惊讶反应姿态。这正是接入新 Mixamo "惊讶" clip 的最自然落点。
  （注：`PedestrianSpawner.cs` 注释里写"Surprised is reserved in the enum but not implemented"，
  与 `PedestrianModulator.cs` 里 `ModulateSurprised()` 已实现的事实不一致，疑似过期注释，
  **需确认**是否有更新计划。）
- `Base.cs` 的 `animator` 字段是 `private`，任何其它脚本（包括 `PedestrianModulator`）目前都无法
  调用 `SetTrigger`/`Play` 等 API 来驱动新动画。

### 5.2 推荐方案：直接扩展现有 Controller 的 State Machine + 新增 Trigger 参数

不建议用 **Animator Override Controller (AOC)**：AOC 的典型用途是"同一套 State 结构，换一批不同
外观角色的具体 clip"（例如同一个 Controller 给不同种族角色换皮肤动作），而这里的需求是
"新增一种之前没有的姿态/状态"（受惊、好奇张望等），本质上是 State Machine 结构性扩展，AOC 解决不了
"加新 State"这件事，只能换已有 State 的 Motion。

推荐做法（每个新动作，如"惊讶"）：

1. **导入 Mixamo fbx**：在 Mixamo 网站下载时选 **With Skin**，Unity 导入后 Rig 设为
   **Humanoid**、Avatar 选 **Copy From Other Avatar**（指向 Rocketbox 角色已有的 Avatar，
   例如 `Female_Adult_02.fbx` 里的 Avatar），保证与现有 Locomotion clip retarget 到同一套骨骼定义。
2. **动画本身设置**：这类"反应类"动画（受惊/惊讶/好奇）通常应该是**原地播放**（Bake Into Pose 的
   XZ 打勾，不带位移），避免和 Root Motion 位移逻辑打架；参考 `Idle.anim` 的设置
   （`m_LoopTime` 视是否需要循环而定：受惊一次性动作建议 `Loop Time = false`）。
3. **在 `BaseSFControllerNormalized.controller` 里新增**：
   - 新增一个 **Trigger 类型**参数，例如 `Surprised`（当前 8 个参数里没有任何 Trigger 类型，
     `Idling` 是 Bool，这会是本 Controller 第一个 Trigger，不影响已有参数）。
   - 新增一个 State，例如 `SurprisedReaction`，Motion 指向新导入的 clip。
   - `Any State → SurprisedReaction`：condition = `Surprised` trigger，`Has Exit Time = false`
     （立即打断当前动作播放）。
   - `SurprisedReaction → Locomotion`：`Has Exit Time = true`，`Exit Time ≈ 1.0`（播完自动回到
     行走/待机混合树），不需要额外条件。
   - 不需要碰 `Locomotion` 混合树本身（它只负责移动方向混合），也不需要动 `Idling` State
     （反正它已经是死状态）。
4. **`Base.cs` 需要一个很小的改动**（不可避免，因为 `animator` 目前完全私有）：
   - 最小侵入方案：加一个 `public void TriggerAnimation(string triggerName) { animator.SetTrigger(triggerName); }`
     方法（或者直接暴露 `public Animator Animator => animator;`）。
   - 在 `PedestrianModulator.ModulateSurprised()` 检测到"上升沿"触发惊讶的那一行
     （`frozenUntil = now + freezeDuration;` 那个 if 分支里），加一行
     `self.TriggerAnimation("Surprised");`（`self` 这个 `Base` 引用该方法本来就已经作为参数传进来了，
     不需要额外 GetComponent）。
   - 除此之外，`Move()` 里每帧仍然会持续 `SetFloat("Forward"/"Strafe")` / `SetBool("Idling", false)`，
     这些不会破坏 Trigger 驱动的 State 跳转（Trigger 一旦消费即清零，且 `Any State` transition
     优先于当前所在 State 的正常 Forward/Strafe 混合），可以放心叠加，不需要改 `Move()` 本身的逻辑。

**结论：需要改 `Base.cs`，但改动极小（新增一个暴露 `SetTrigger` 的 public 方法/属性），
不需要改 `Move()` 里 Forward/Strafe/Idling 的既有驱动逻辑。** 主要工作量在 Controller 编辑
（新增 State + Trigger 参数 + Any State transition）和 Mixamo 素材的 Humanoid 重定向导入，
代码改动很小。

### 5.3 待办 / 需确认清单

- `strafe_45.anim` 与 `Idle.anim` 两个 standalone `.anim` 的具体拆分来源（哪个 FBX 拆出来的）未
  确认，不影响接入新动画的结论，仅为历史溯源缺口。
- Bake Into Pose（`keepOriginalPositionXZ/Y`）在 Unity Editor GUI 里的勾选方向是按惯例推断的，
  建议实际打开 Unity 项目时用 Inspector 目视确认一遍，尤其是给新 Mixamo clip 设置这两个选项时。
  Idle 与 Loop 相关的 Human Motion 设置需以Unity实际显示为准。
- `PedestrianSpawner.cs` 注释与 `PedestrianModulator.cs` 实现不一致（"Surprised 未实现" vs
  实际已有 `ModulateSurprised()`），建议找相关开发者确认是文档过期还是另有计划。
- Mixamo 素材导入后具体挂到 `Any State` 还是仅从 `Locomotion` 单独连线，取决于是否希望"惊讶"能
  打断待机之外的其它未来 State；本报告按只有 `Locomotion`/`Idling` 两态的现状给出最小方案，
  若之后新增更多 State，需要重新评估 transition 拓扑。
