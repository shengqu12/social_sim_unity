# Personality 行为重设计 V2：Curious 三阶段状态机 + Scared/Surprised/Indifferent

> 只读代码调研 + 设计方案，**未修改工程任何文件**。
> 调研日期：2026-07-02
> 配套参考：`PEDESTRIAN_SPAWNER_DESIGN.md`（spawner 整体架构、`ModulateVelocity` 钩子的由来）、`SEAN_architecture_analysis.md`（整体架构、执行顺序注意事项）
> 现状：`PedestrianModulator.cs`（`Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs`）已实现 Scared/Curious 的**无状态**持续调制版（每帧只读当前距离，不记忆跨帧状态），`Surprised` 是空 case，`Indifferent` 不挂组件。本文档评估把 Curious 改造成三阶段状态机、把 Surprised 做成真正带冷却的一次性触发，是否可行、怎么做。
> 全文分两部分：**第 1 章「调研发现」**（客观代码事实，均已 `Read`/`grep` 核实，附 `文件:行号`）与**第 2 章「设计方案」**（未落地，供讨论）。不确定处标「需确认」/「需实测」。

---

## 目录

1. [调研发现](#1-调研发现)
   1. [SFAgent 速度机制：`Modulate()` 在 `MAX_VEL` 夹紧之后执行，加速不受限](#11-sfagent-速度机制)
   2. [目标点机制：`InitDest()` 是唯一的持续更新接口，但会撞上 `PedestrianSpawner` 的随机游走循环](#12-目标点机制)
   3. [机器人朝向 + 速度：朝向直接能拿，速度建议用位置差分而不是 `Rigidbody.velocity`](#13-机器人朝向--速度)
   4. [让行人停下：`Modulate()` 返回 ~0 就能冻结动画，不冲突，但有两个边界情况要注意](#14-让行人停下)
   5. [状态机放哪：状态推进必须放在 `Modulate()` 内部，不能放独立的 `Update()`](#15-状态机放哪)
2. [设计方案](#2-设计方案)
   1. [用户需求里的一个措辞不一致，需确认](#21-用户需求里的一个措辞不一致需确认)
   2. [总体结构：`BehaviorState` 状态机 + 四条 personality 分支](#22-总体结构)
   3. [参数表（全部 public，Inspector 可调）](#23-参数表)
   4. [Curious：WANDER → APPROACH → FOLLOW 状态转移与执行逻辑](#24-curious)
   5. [Scared / Surprised / Indifferent](#25-scared--surprised--indifferent)
   6. [与 `PedestrianSpawner` 的协调：新增 `IsControllingDestination`](#26-与-pedestrianspawner-的协调)
   7. [哪些用现有钩子就够，哪些需要新接口](#27-哪些用现有钩子就够哪些需要新接口)
3. [改动面汇总](#3-改动面汇总)
4. [风险与需确认/需实测清单](#4-风险与需确认需实测清单)

---

## 1. 调研发现

### 1.1 SFAgent 速度机制

`Agents.Base.Update()`（`Assets/Scripts/SEAN/Scenario/Agents/Base.cs:73-75`，本次调研前上一轮已落地的钩子）：

```csharp
void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());   // UpdateVelocity() 先算，Modulate() 后处理
    Move();
}
```

`IVI.SFAgent.UpdateVelocity()`（`Assets/IVI/Scripts/SFAgent.cs:52-64`）：

```csharp
protected override Vector3 UpdateVelocity()
{
    SocialForce totalForce = ComputeForce();
    var accel = totalForce.force / MASS;
    Vector3 nextVelocity = velocity + accel * Time.deltaTime;
    nextVelocity.y = 0;
    if (nextVelocity.sqrMagnitude > 0)
    {
        nextVelocity = nextVelocity.normalized * Mathf.Min(nextVelocity.magnitude, Parameters.MAX_VEL);
    }
    return nextVelocity;
}
```

**关键事实**：`Parameters.MAX_VEL`（`Assets/Scripts/Agents/Parameters.cs:18`，`= 0.6f`）的夹紧发生在 `UpdateVelocity()` **内部**，`SFAgent` 把夹紧后的结果作为**返回值**交给 `Base.Update()`，此时才调用 `ModulateVelocity()` → `PedestrianModulator.Modulate(socialForceVelocity, self)`。也就是说：

- `Modulate()` 拿到的 `socialForceVelocity` 参数已经被 `MAX_VEL` 夹过一次，但 `Modulate()` **返回值不受这个夹紧约束**——现有 `Scared` 实现已经这么做了（`scaredMaxSpeed = 1.2f`，是 `MAX_VEL` 的 2 倍，`PedestrianModulator.cs:75-78` 直接在 `result` 上 `normalized * scaredMaxSpeed`），说明「调制层自己夹紧到更高上限」这个思路**已经验证可行、已经在跑**。`Curious` 的 `APPROACH` 加速可以照搬同一模式，不需要新机制。
- 有一个**轻微反馈但不会失控**的细节：下一帧 `UpdateVelocity()` 里 `nextVelocity = velocity + accel * Time.deltaTime` 用的 `velocity` 是 `Base.velocity`——即**上一帧 `Modulate()` 的输出**（因为赋值发生在 `Modulate()` 返回之后）。如果 `Modulate()` 把速度顶到远高于 `MAX_VEL`，下一帧 `CalculateGoalForce()`（`SFAgent.cs:140-146`，`MASS * (desiredVel - velocity) / T`）会因为 `velocity` 偏大而算出一个"减速力"，但这个力算出来的 `nextVelocity` 无论正负都会被再次 `Min(..., MAX_VEL)` 夹回 `[0, 0.6]`——即 SFM 自己的内部积分"不知道"外部加速的存在，但每帧都会被强制拉回到它自己认知的范围内，然后 `Modulate()` 再重新拔高。**结论：不会累积失控，但意味着"加速"这件事完全由调制层每帧重新施加，SFM 内部状态对此没有记忆**，是预期行为，不是 bug。
- **动画层的真实风险（比数值风险更值得关注）**：`Move()`（`Base.cs:242`）用 `animator.speed = velocity.magnitude` 直接缩放 Animator 播放速度，且全仓库唯一的运动相关 Animator 参数只有 `Forward`/`Strafe`/`Idling`（`Base.cs:245-246,239`，已 `grep` 确认无 `Run`/`Sprint` 等参数——见 [§1.1 补充](#技术备注animator-参数全集)）。由于走路是 **root motion** 驱动位移（`animator.applyRootMotion = true`，`Base.cs:64`，且全仓库没有 `OnAnimatorMove` 覆盖），把 `animator.speed` 顶到明显高于日常行走对应的 ~0.6 会让走路动画播放速度整体倍增——**这是"快进"观感风险，不是数值安全问题**。`approachMaxSpeed` 建议保守取值（见 §2.3），需要实机看效果调整，不能只看数值合不合理。

<a id="技术备注animator-参数全集"></a>
*技术备注*：`grep -rn "animator\." Assets/Scripts/SEAN/Scenario/Agents/*.cs Assets/IVI/Scripts/*.cs` 确认全仓库只在 `Base.cs` 里驱动 Animator，参数集合就是 `Forward`/`Strafe`（`SetFloat`）+ `Idling`（`SetBool`）+ 全局 `speed` 缩放，没有另外的跑步 blend tree 或参数可用。

### 1.2 目标点机制

`IVI.INavigable`（`Assets/IVI/Scripts/Navigation/INavigable.cs`）是 `Agents.Base` 的基类，`SFAgent : Base : INavigable`，所以任何 `Modulate(Vector3 socialForceVelocity, Base self)` 里的 `self` 参数**本身就是 `INavigable`**，可以直接调用它的两个 public 方法，不需要新接口：

- `public void InitDest(Vector3 destPos)`（`INavigable.cs:105-110`）：把目标点 `NavMesh.SamplePosition` 吸附到最近的 NavMesh 点（`SampledGoalPosition()`，初始搜索半径 0.25m，找不到就 +0.25m 递归扩大），然后立刻 `PlanNavigation()` → `Base.PlanNavigation()`（`Base.cs:93-103`）→ `ComputePath(destPos)` → `NavMesh.CalculatePath(...)` 重新算路径。
- `public bool CloseEnough()`（`INavigable.cs:130-137`）：与目标点水平距离 ≤ `Parameters.CLOSE_ENOUGH_MIN_DIST`（`= 1.0f`，`Parameters.cs:20`）。

**没有更轻量的"持续更新目标"接口**——`InitDest()` 就是唯一入口，每次调用都会触发一次 `NavMesh.SamplePosition` + `NavMesh.CalculatePath`。`FOLLOW` 状态如果每帧都调 `InitDest(机器人身后点)`，对典型场景（几十个行人以内）性能大概率无感知，但**没有必要**——`InitDest()` 只是重新规划路径的拐点（`nmPath.corners`），真正逐帧的转向平滑早就由 `Base.Move()`（`Base.cs:205-209`，`goalDir = goalWeight * goalDir.normalized + (1-goalWeight) * velocity.normalized`）在做。**建议节流**：按 `plannerFPS` 同频（`INavigable.plannerFPS` 默认 `5`，即 0.2s 一次，`INavigable.cs:12`）或更低频率调 `InitDest()`，没有必要每帧调。

**关键冲突（本次调研最重要的发现）**：`PedestrianSpawner.Update()`（`Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs:45-54`）：

```csharp
void Update()
{
    foreach (var agent in agents)
    {
        if (agent.CloseEnough())
        {
            agent.InitDest(Util.Navmesh.RandomPose().position);
        }
    }
}
```

**这是每帧、对 `spawnGroups` 里生成的每一个 agent 无条件执行的"到点就换随机目标"循环**，不管这个 agent 当前是什么 personality/状态。如果 `Curious` 的 `APPROACH`/`FOLLOW` 状态把目标改到"机器人附近/身后"，一旦这个 agent 真的靠近到 `CloseEnough()`（≤1m）——而这恰恰是 `APPROACH`/`FOLLOW` 的**目的**——`PedestrianSpawner.Update()` 会在下一帧立刻把目标点重新甩回一个随机点，**把刚建立的"跟随"状态冲掉**。

这个冲突现有代码里不会暴露，是因为目前只有 `Scared`/`Curious`（当前的无状态弱吸引版）从不调用 `InitDest()`——它们只改 `Modulate()` 的返回速度，不碰目标点，所以和 `PedestrianSpawner.Update()` 的随机游走循环相安无事。**一旦 `APPROACH`/`FOLLOW` 需要真正改目标点，就必须解决这个冲突**——方案见 §2.6。

### 1.3 机器人朝向 + 速度

**朝向**：`SEAN.Scenario.Robot`（`Assets/Scripts/SEAN/Scenario/Robot.cs`）用 `new` 覆盖了 `transform`（`Robot.cs:106-112`，返回 `base_link.transform`），并暴露 `public Quaternion rotation`（`Robot.cs:120-126`）。所以 `SEAN.instance.robot.transform.forward`（或 `robot.rotation * Vector3.forward`）就是机器人朝向，**已经能直接拿到，不需要新增接口**。（需确认：这个朝向是否总是等于机器人的行进方向——对差速轮/全向轮机器人一般成立，但没有在这次调研里逐一核实每种机器人模型的驱动方式是否可能出现朝向与行进方向不一致的情况。）

**速度——这里有一个值得注意的坑**：`IVI.SFAgent.CalculateAgentForce()`（`SFAgent.cs:179-180`）已经在读机器人速度：

```csharp
var robotRB = robot.GetComponentInChildren<Rigidbody>();
dampenFactor = robotRB.velocity.magnitude > 0.1f ? robotRepulsion : 1f;
```

但 `SEAN.Control.VelocityController`（`Assets/Scripts/SEAN/Control/VelocityController.cs`）里明确写着（`VelocityController.cs:14-18` 注释 + `FixedUpdate()` 逻辑）：

- **轮式/Rigidbody 机器人**（如 Kuri）：`DriveRigidbody()`（`VelocityController.cs:197-206`）直接设置 `rb.velocity = rb.transform.forward * targetLinVelocity`——这种情况下 `GetComponentInChildren<Rigidbody>().velocity` 是准确、实时的。
- **腿式/ArticulationBody 机器人**（注释明确点名 Unitree A1，URDF 导入）：由 `DriveArticulation()`（`VelocityController.cs:215-258`）驱动，运动的是 `ArticulationBody`（`artRoot.velocity`），**不是** `base_link` 的 `Rigidbody`。代码注释原文（`VelocityController.cs:14-18`）：「Articulation-based robots... move through their root ArticulationBody, which PhysX simulates in world space and does NOT follow its parent Transform. Driving the base_link Rigidbody alone therefore leaves the visible robot standing still」。

也就是说，对腿式机器人，`robot.GetComponentInChildren<Rigidbody>()` 很可能拿到一个**存在但不被驱动**的 `Rigidbody`（`.velocity` 恒为 0 或不可靠），甚至可能压根找不到（`ArticulationBody` 层级下通常不必然挂常规 `Rigidbody`，本次未在 Editor 里对具体机器人 prefab 逐一核实是否有——**需确认**）。当前工程里唯一读机器人速度的地方（`SFAgent.cs:179`）就是按轮式机器人假设写的，**没有对腿式机器人做兼容处理**，这是一个既有的、本次调研中新发现的潜在隐患（不在本次任务范围内修，但 `FOLLOW`「匹配机器人速度」如果照抄 `SFAgent.cs` 这个读法，会在腿式机器人场景下拿到不准的值）。

**建议**：`FOLLOW` 不要依赖 `Rigidbody`/`ArticulationBody` 这类物理内部状态，改用**位置差分**——`PedestrianModulator` 每帧记录 `lastRobotPos`，`estimatedRobotSpeed = (currentRobotPos - lastRobotPos).magnitude / Time.deltaTime`，纯粹基于 `SEAN.instance.robot.position`（已确认可用），**与机器人是轮式还是腿式无关**，天然兼容。这是本设计对研究问题 3 的核心结论，详见 §2.4。

### 1.4 让行人停下

`Modulate()` 返回接近 `Vector3.zero`——**不会和 SFM 冲突**，因为 `Modulate()` 的返回值就是最终写入 `Base.velocity` 的值（`Base.cs:75`），SFM 自己的 `UpdateVelocity()` 已经在这次调用里跑完了，不会被"覆盖"这件事本身干扰——SFM 只是在**下一帧**会因为 `velocity` 变小而重新算一个从低速起步的加速度（§1.1 已分析过这个反馈是安全的）。

`Move()` 在速度为 0 时的行为（`Base.cs:193-259`）：

- 转向逻辑（`Base.cs:205-209`）：`goalDir = goalWeight * goalDir.normalized + (1-goalWeight) * velocity.normalized`——`velocity=0` 时 `velocity.normalized` 也是 `Vector3.zero`，退化成 `goalDir = 0.5 * goalDir.normalized`，**朝向仍然会继续朝目标点转**，不会被冻结（如果不想让人物在"愣住"的同时还在转身，需要在 `Frozen` 状态里额外处理，见 §2.5 的需确认项）。
- 动画（`Base.cs:239-246`）：`animator.speed = velocity.magnitude ≈ 0` → **动画播放几乎完全停止，定格在触发瞬间的那一帧动作**——这正好是"急停冻结"想要的效果，不需要额外代码。`animator.SetBool("Idling", idle)` 那一行由于 `idle = animParams.magnitude < idleSpeed && !applyRootMotion`（`Base.cs:237`），而 `applyRootMotion` 恒为 `true`（`Base.cs:32,64`，没有任何地方改它），所以 `idle` 恒为 `false`——**`Idling` 这个 Animator 参数在当前代码路径下永远不会被设成 `true`**，冻结效果完全靠 `animator.speed≈0` 实现，不是靠切到 Idle 姿势。视觉上会定格在"半步走姿"而不是自然的立正/惊讶表情——**需实测**，如果观感不自然，属于 `Base.cs`/Animator Controller 层面的问题，超出本次"只加 personality 层"的范围，不在本设计处理。
- **边界情况 1**（`Base.cs:200-203`）：如果 `destPos == Vector3.zero`，`Move()` 整个函数直接 `return`，连动画都不更新。`Frozen` 状态**不会主动触发这个分支**——设计上 `Surprised` 只改 `Modulate()` 的返回速度，完全不碰 `destPos`/不调 `InitDest()`，`destPos` 始终是 `PedestrianSpawner.Update()` 正常维护的随机游走目标，不会变成 `Vector3.zero`。
- **边界情况 2**：`INavigable.Coroutine()`（`INavigable.cs:41-87`）以 `plannerFPS`（默认 5Hz）为周期检查 `CloseEnough()`，为真则 `StopNavigation()` → `destPos = Vector3.zero`（`Base.cs:105-110`）。这个协程和 `PedestrianSpawner.Update()`（每帧/60Hz+）都在检查同一个 `CloseEnough()` 条件，但 `PedestrianSpawner.Update()` 频率快得多，实践中总能在协程的 0.2s 窗口内先把 `destPos` 重新赋成一个新的非零随机点——这是 `Random`/`GraphNav` 场景已经在依赖的既有时序关系，`Surprised` 由于不接管 `destPos`，天然沿用这个既有的"自愈"机制，不受影响。**这不是本次新引入的风险，是确认现状安全**。

### 1.5 状态机放哪

`Agents.Base.Update()`（`Base.cs:73-77`）：

```csharp
void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());
    Move();
}
```

`ModulateVelocity()` → `modulator.Modulate(...)` 是**在 `Base.Update()` 内部同步调用**的，不是通过 Unity 的独立 `Update()` 回调触发。这意味着：

- 如果把状态推进逻辑放进 `PedestrianModulator` **自己的** `Update()` 方法里（一个独立于 `Base.Update()` 的 Unity 回调），就引入了一个 Unity **不保证**的执行顺序依赖——`PedestrianModulator.Update()` 和 `SFAgent`（继承链上的 `Base.Update()`）谁先跑，取决于 Unity 的脚本执行顺序设置（默认按脚本编译顺序，不稳定），除非显式配置 `[DefaultExecutionOrder]`。`SEAN_architecture_analysis.md:396` 已经点出这个仓库里对"同帧内多个方法调用顺序无强保证"的既有顾虑，`PEDESTRIAN_SPAWNER_DESIGN.md:301`（路线 B 排除理由）也明确因为同样的原因排除了另一个方案。**这个坑是有先例的，应该刻意避开**。
- **结论/建议**：状态推进（阶段切换判定、计时器自增、跨帧标志位更新）应该直接写在 `Modulate()` 方法体内部——它本来就是每帧被 `Base.Update()` 同步调用一次，`Time.deltaTime` 在这里用和在独立 `Update()` 里用没有区别，但完全避免了"两个 `Update()` 谁先跑"的不确定性。`PedestrianModulator` **不需要**自己的 `Update()` 方法；跨帧状态（当前 `BehaviorState`、计时器、"是否已触发过"标志位）作为 `private` 字段挂在组件实例上即可——组件实例本身跨帧存活，`Modulate()` 每帧被调用一次，天然具备"读当前状态 → 判断转移 → 执行 → 更新状态"的完整能力，不需要协程也不需要额外的 Update()。

---

## 2. 设计方案

> 以下均为**建议方案，代码未修改**。核心原则：状态机全部收在 `Modulate()` 内部（§1.5），`APPROACH`/`FOLLOW` 复用 `InitDest()`（§1.2）但必须先解决和 `PedestrianSpawner.Update()` 的目标点冲突（§2.6），速度层面的"加速/匹配速度"复用已经验证过的"调制层自己夹紧到更高上限"模式（§1.1，`Scared` 现有实现已经是这个模式）。

### 2.1 用户需求里的一个措辞不一致，需确认

任务描述里 `Curious` 写的是「两阶段状态机」，但接下来列的是 **WANDER / APPROACH / FOLLOW 三个阶段**。本设计按实际列出的三阶段处理，「两阶段」按笔误理解——**需确认**：如果确实只想要两阶段（比如没有独立的 `FOLLOW`，`APPROACH` 到位后就算数不再持续贴身跟随），请明确告知，会显著简化状态机（少一个状态、少一套"匹配速度+身后定位"的实现）。

### 2.2 总体结构

```csharp
public class PedestrianModulator : MonoBehaviour, IVelocityModulator
{
    public enum PersonalityType { Scared, Curious, Surprised, Indifferent }
    public PersonalityType personality = PersonalityType.Indifferent;

    // 只有 Curious 用到；Scared/Surprised/Indifferent 不需要三段式状态机，
    // 各自的"状态"更简单（见 §2.5），没必要共享同一个 enum，避免无意义的状态组合。
    private enum CuriousState { Wander, Approach, Follow }
    private CuriousState curiousState = CuriousState.Wander;

    // Surprised 的跨帧状态
    private bool wasInSurpriseRadius = false;
    private float frozenUntil = -1f;
    private float cooldownUntil = -1f;

    // Curious 的节流计时器（§1.2 建议不要每帧调 InitDest）
    private float nextRetargetTime = 0f;

    // Follow 用的机器人速度估计（§1.3，位置差分，不依赖 Rigidbody/ArticulationBody）
    private Vector3 lastRobotPos;
    private bool hasLastRobotPos = false;

    public Vector3 Modulate(Vector3 socialForceVelocity, Base self)
    {
        // ... 取 robot 引用（沿用现有 try/catch SEAN.instance 判空模式，PedestrianModulator.cs:46-59）
        switch (personality)
        {
            case PersonalityType.Scared:      return ModulateScared(socialForceVelocity, self, robot);
            case PersonalityType.Curious:     return ModulateCurious(socialForceVelocity, self, robot);
            case PersonalityType.Surprised:   return ModulateSurprised(socialForceVelocity, self, robot);
            default:                          return Scale(socialForceVelocity); // Indifferent
        }
    }
}
```

`IsControllingDestination`（新增 public 属性，供 `PedestrianSpawner` 读，见 §2.6）：

```csharp
public bool IsControllingDestination =>
    personality == PersonalityType.Curious &&
    (curiousState == CuriousState.Approach || curiousState == CuriousState.Follow);
```

### 2.3 参数表

全部 `public`，Inspector 可调，沿用现有 `[Header("...")]` 分组风格（`PedestrianModulator.cs:32,37` 已经这么做）：

| 分组 | 字段 | 建议默认值 | 含义 |
|---|---|---|---|
| Scared（现状不变） | `scaredRadius` | 3.0 | 触发距离 |
| | `scaredStrength` | 1.5 | 逃离分量强度 |
| | `scaredMaxSpeed` | 1.2 | 逃离时的速度上限（已超 `MAX_VEL`，验证过可行，§1.1） |
| Curious | `detectRadius` | 4.0 | WANDER→APPROACH 触发距离 |
| | `detectExitMargin` | 1.3 | 迟滞系数：距离 > `detectRadius * detectExitMargin` 才退回 WANDER，防止在临界值抖动 |
| | `followDist` | 1.8 | APPROACH→FOLLOW 触发距离（**必须 > `Parameters.CLOSE_ENOUGH_MIN_DIST`=1.0，见 §4 风险项**） |
| | `followExitMargin` | 1.3 | 迟滞系数：距离 > `followDist * followExitMargin` 才从 FOLLOW 退回 APPROACH |
| | `approachMaxSpeed` | 1.0 | APPROACH 阶段速度上限（比 `scaredMaxSpeed` 保守，§1.1 动画倍速风险） |
| | `followBehindOffset` | 1.2 | FOLLOW 时目标点相对机器人朝向的"身后"距离 |
| | `followSpeedMatchGain` | 1.0 | 匹配机器人速度的比例系数（1.0=完全匹配，可调低做"稍慢半拍"的观感） |
| | `retargetInterval` | 0.3 | `InitDest()` 节流间隔（秒），避免每帧调（§1.2） |
| Surprised | `surpriseRadius` | 1.5 | 触发距离（上升沿检测） |
| | `freezeDuration` | 1.5 | 冻结时长（秒） |
| | `cooldownDuration` | 4.0 | 冻结结束后的冷却时长（秒），冷却期内即使机器人再次进入 `surpriseRadius` 也不重新触发 |
| 通用（现状不变） | `walkSpeedMultiplier` | 1.0 | appearance 走速倍率，`Modulate()` 末尾统一乘（`PedestrianModulator.cs:103-106` 已有） |

### 2.4 Curious

**状态转移**（迟滞防抖，§2.3 已给参数）：

```
WANDER --[dist <= detectRadius]--> APPROACH
APPROACH --[dist <= followDist]--> FOLLOW
APPROACH --[dist > detectRadius * detectExitMargin]--> WANDER
FOLLOW --[dist > followDist * followExitMargin]--> APPROACH
FOLLOW --[dist > detectRadius * detectExitMargin]--> WANDER   （机器人快速远离，一步到位不经过 APPROACH）
```

**WANDER**：不做任何事，`Modulate()` 直接返回 `Scale(socialForceVelocity)`——`PedestrianSpawner.Update()` 的随机游走循环（§1.2）原样生效，和 `Indifferent` 表现一致，这也是为什么 `IsControllingDestination` 在 `Wander` 状态下必须是 `false`（§2.6）。

**APPROACH**：

- 进入状态时（`Wander→Approach` 这一帧）立即 `self.InitDest(robot.position)`，之后每 `retargetInterval` 秒重新 `InitDest(robot.position)` 追一次（机器人在动，目标点需要追更新；节流理由见 §1.2）。这样 SFAgent 自己的 `CalculateGoalForce()`/避障/`CalculateAgentForce()` 都会自然地把这个 agent 往机器人方向带，不需要在速度层面手搓方向。
- `Modulate()` 里只做**速度放大**：`result = socialForceVelocity.normalized * Mathf.Min(socialForceVelocity.magnitude * approachSpeedBoost, approachMaxSpeed)`——和 `Scared` 现有写法（`PedestrianModulator.cs:75-78`）同一模式，复用已验证可行的路径。

**FOLLOW**：

- 目标点：每 `retargetInterval` 秒 `self.InitDest(robot.position - robot.transform.forward * followBehindOffset)`（`robot.transform.forward` 取自 §1.3 已确认可用的朝向）。
- 速度：**方向沿用 SFM 计算出的 `socialForceVelocity` 方向**（已经包含了避障、和其他行人的社会力，不要用"机器人位置减自身位置"这种裸方向去覆盖，否则会撞墙/撞人——这点在 §1.1 已经论证过 `Modulate()` 是后处理钩子，改幅度不改方向是最安全的组合方式），**幅度替换成机器人速度估计**：

```csharp
Vector3 dir = socialForceVelocity.sqrMagnitude > 0.0001f ? socialForceVelocity.normalized
                                                            : self.transform.forward;
float robotSpeed = EstimateRobotSpeed(robot);   // 位置差分，见下
result = dir * Mathf.Max(robotSpeed * followSpeedMatchGain, 0.05f);  // 保留一点最小速度，避免机器人瞬时静止时行人卡死不转向
```

```csharp
private float EstimateRobotSpeed(Robot robot)
{
    if (!hasLastRobotPos) { lastRobotPos = robot.position; hasLastRobotPos = true; return 0f; }
    float speed = (robot.position - lastRobotPos).magnitude / Time.deltaTime;
    lastRobotPos = robot.position;
    return speed;
}
```

（§1.3 已论证：位置差分不依赖 `Rigidbody`/`ArticulationBody`，轮式/腿式机器人通用，规避了 `SFAgent.cs:179` 现有代码对腿式机器人不准确的隐患。）

### 2.5 Scared / Surprised / Indifferent

- **Scared**：现状（`PedestrianModulator.cs:69-80`）已经是"持续叠加远离速度分量 + 夹到更高上限"，和需求描述一致，**不需要改动逻辑**，只是要挪进新的 `switch`/方法拆分结构里（纯重构，非行为变更）。
- **Indifferent**：现状（不挂组件，或挂了但 `default` 分支直接透传）已满足，`IsControllingDestination` 恒 `false`。
- **Surprised**：

```csharp
private Vector3 ModulateSurprised(Vector3 socialForceVelocity, Base self, Robot robot)
{
    float dist = Distance(self, robot);
    bool inRadius = dist <= surpriseRadius;
    float now = Time.time;

    // 上升沿检测 + 冷却期内不重新触发
    if (inRadius && !wasInSurpriseRadius && now >= cooldownUntil)
    {
        frozenUntil = now + freezeDuration;
        cooldownUntil = frozenUntil + cooldownDuration;
    }
    wasInSurpriseRadius = inRadius;

    if (now < frozenUntil)
    {
        return Vector3.zero;   // §1.4 已确认：安全，动画自然定格，不碰 destPos
    }
    return Scale(socialForceVelocity);
}
```

不改 `destPos`，不调 `InitDest()`，`IsControllingDestination` 恒 `false`——`PedestrianSpawner.Update()` 的随机游走循环在冻结期间照常在背后维护目标点（§1.4 边界情况 2 已确认安全），解冻瞬间行人朝着这个早就存在、可能已经变了好几次的随机目标继续走，符合"愣住之后恢复正常游走"的预期。

**需确认**：`freezeDuration` 秒内机器人如果离开又回到 `surpriseRadius` 内，因为 `cooldownUntil` 在触发瞬间就已经算好（`frozenUntil + cooldownDuration`），不会二次触发——这个"边冻结边计冷却"的设计假设冷却期该从触发那一刻起算而不是从解冻那一刻起算，如果产品意图是"解冻后才开始算冷却"，`cooldownUntil` 应该在解冻的那一帧才赋值，是两种不同的语义，需要跟你确认想要哪种。

### 2.6 与 `PedestrianSpawner` 的协调

`PedestrianSpawner.cs`（`PedestrianSpawner.cs:45-54`）需要跳过"正被 personality 状态机接管目标点"的 agent，否则 §1.2 的冲突会让 `APPROACH`/`FOLLOW` 形同虚设：

```csharp
void Update()
{
    foreach (var agent in agents)
    {
        var modulator = agent.gameObject.GetComponent<PedestrianModulator>();  // 或缓存，见下
        if (modulator != null && modulator.IsControllingDestination)
        {
            continue;   // Curious 的 APPROACH/FOLLOW 自己在管 InitDest，spawner 不要插手
        }
        if (agent.CloseEnough())
        {
            agent.InitDest(Util.Navmesh.RandomPose().position);
        }
    }
}
```

这是**本设计唯一必须触碰的既有文件**（除了 `PedestrianModulator.cs` 本身的重写）。改动是纯增量的一个 `if (... ) continue;`，`Indifferent`/`Scared`/`Surprised`/`Curious.Wander` 这几种情况下 `IsControllingDestination` 恒 `false`，行为和现在完全一致。

**性能小优化建议（非必须）**：`SpawnAgent()`（`PedestrianSpawner.cs:95-115`）已经是唯一创建 `PedestrianModulator` 实例的地方，可以在那里顺手缓存一份 `Dictionary<IVI.INavigable, PedestrianModulator>`，`Update()` 里查字典代替 `GetComponent`，避免每帧每 agent 一次反射式查找——量级（几十个行人）目前无所谓，留作后续优化项即可，不影响本设计正确性。

### 2.7 哪些用现有钩子就够，哪些需要新接口

| 需求 | 是否够用 `IVelocityModulator.Modulate()` 单一钩子 | 说明 |
|---|---|---|
| Scared 持续远离 | ✅ 够 | 现状已验证 |
| Surprised 冻结/冷却 | ✅ 够 | 纯速度层面，状态存在组件私有字段里（§1.5） |
| Indifferent | ✅ 够 | 透传 |
| Curious APPROACH 加速 | ✅ 够（速度层面） | 但**方向**由改 `destPos`（`InitDest`）驱动，不是速度层面硬转向，见下一行 |
| Curious APPROACH/FOLLOW 改目标点 | ⚠️ 需要额外调用 `self.InitDest()`（`INavigable` 既有 public 方法，不用新接口） | 必须配合 §2.6 对 `PedestrianSpawner.cs` 的改动，否则被随机游走覆盖 |
| Curious FOLLOW 匹配速度 | ✅ 够 | 位置差分估算，组件内部实现，不需要新读机器人内部状态的接口 |

**结论**：不需要新增接口（`IVelocityModulator`/`InitDest`/`CloseEnough` 已经够用），**唯一超出"只改 `PedestrianModulator.cs`"范围的必要改动是 `PedestrianSpawner.cs` 的一个 `continue` 分支**（§2.6）。

---

## 3. 改动面汇总

| 文件 | 状态 | 说明 |
|---|---|---|
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianModulator.cs` | **重写** | 状态机化：新增 `CuriousState`、Surprised 的跨帧字段、`IsControllingDestination`、四条 personality 分支拆成独立方法 |
| `Assets/Scripts/SEAN/Scenario/Agents/PedestrianSpawner.cs` | **修改**（必须） | `Update()` 里加一个 `if (modulator.IsControllingDestination) continue;` 分支，§2.6 |
| `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` | 不改 | `ModulateVelocity()` 钩子已在上一轮落地，本次不需要再碰 |
| `Assets/IVI/Scripts/SFAgent.cs`、`Assets/Scripts/SEAN/Scenario/Robot.cs`、`VelocityController.cs` | 不改 | 全部只读，机器人速度改用位置差分绕开对 `Rigidbody`/`ArticulationBody` 的依赖（§1.3），不需要改这些文件本身 |

---

## 4. 风险与需确认/需实测清单

- **需确认**：任务描述"Curious 两阶段状态机"和实际列出的 WANDER/APPROACH/FOLLOW 三阶段不一致，本设计按三阶段做，见 §2.1。
- **需确认**：`followDist` 必须显著大于 `Parameters.CLOSE_ENOUGH_MIN_DIST`（`=1.0f`，`Parameters.cs:20`）。如果 `followDist` 太接近或小于 1.0m，FOLLOW 状态下 agent 会频繁触发 `INavigable.Coroutine()` 自己的 `CloseEnough()` 判定（这个协程独立于 `PedestrianSpawner`，`IsControllingDestination` 拦不住它，§1.4 边界情况 2），导致 `destPos` 被协程自己置零、`Move()` 提前 `return`（`Base.cs:200-203`）——本设计建议默认值 `followDist=1.8`（§2.3）已经留了余量，但精确阈值需要实机测试确认多大距离下不会出现这个抖动。
- **需确认**：Surprised 的冷却计时语义——"边冻结边计冷却"还是"解冻后才开始计冷却"，见 §2.5 结尾。
- **需实测**：`approachMaxSpeed`/`scaredMaxSpeed` 这类超过 `Parameters.MAX_VEL`（0.6）的速度上限，会等比放大 `animator.speed`，走路动画播放速度跟着倍增，视觉上可能出现"快进"感（§1.1）。建议先用较保守的倍率（如 1.5×~2× `MAX_VEL`）实机看效果，而不是直接照抄 `scaredMaxSpeed=1.2` 的既有值套到 Curious 上。
- **需实测**：`Surprised` 冻结时 `animator.speed≈0` 会定格在触发瞬间的动画帧（可能是半步走姿），不是切到自然的 Idle/惊讶姿势——因为 `Idling` 参数在当前 `applyRootMotion=true` 的配置下永远不会被设成 `true`（`Base.cs:237`，恒 `false` 分支）。如果这个观感不理想，修复点在 `Base.cs`（比如冻结时临时把 `applyRootMotion` 设 `false` 并强制 `Idling=true`），超出本次"只加 personality 层"的范围，需要你确认是否要连带处理。
- **需确认**：`robot.transform.forward` 是否在所有机型上都等价于"行进方向"（§1.3），本次未逐一核实每种机器人 prefab 的朝向约定。
- **既有隐患，非本次引入，但会被 FOLLOW 放大暴露**：`SFAgent.cs:179` 读机器人速度用的是 `GetComponentInChildren<Rigidbody>().velocity`，对腿式/`ArticulationBody` 机器人（如 Unitree A1）不准确（§1.3）。本设计的 `FOLLOW` 用位置差分绕开了这个问题，但如果之后有人复用 `SFAgent.cs` 这个既有读法去做别的功能，会踩到同一个坑，建议记录下来但不在本次范围内修。
