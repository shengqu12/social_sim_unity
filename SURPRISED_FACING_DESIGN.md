# Surprised 冻结时面向机器人 —— 调研与设计

只读调研,未改代码。目标:Surprised personality 冻结播放惊讶动画时,行人应该
转身面向机器人,而不是保持随机游走时的朝向。

---

## 1. Move() 朝向逻辑分析

`Base.cs` 的 `Move()`(private,当前在 L201-267)是整个朝向计算和落地的唯一
入口——所有 agent 子类(SFAgent / ORCA.Agent / Playback.Agent / PlayerAgent)
每帧都会经过这里,朝向不是"设置"出来的,而是通过 `transform.RotateAround`
增量旋转出来的。

关键路径(普通 SF 行人,包括所有挂了 `PedestrianModulator` 的行人,走的都是
这条分支,因为它们既不是 `Playback.Agent` 也不是 `PlayerAgent`):

```csharp
// Base.cs L205-218
if (!(GetType().Equals(typeof(Scenario.Agents.Playback.Agent)) || GetType().Equals(typeof(PlayerAgent))))
{
    if (destPos == Vector3.zero) { return; }   // L208: destPos 全零直接跳过整个 Move()

    Vector3 goalDir = nearestGoalPoint - transform.position;   // L213: 指向 nmPath 下一个拐点
    float goalWeight = 0.5f;
    goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;  // L215: 50/50 混合
    goalDir.y = 0;
    angle = -Vector3.SignedAngle(goalDir, transform.forward, Vector3.up);   // L217
}
```

- `nearestGoalPoint`(L186-200)读的是 `nmPath.corners`,即导航路径上下一个
  拐点,本质是**指向 destPos 方向**,和机器人位置完全无关。
- `velocity` 是 `Update()` 里 `ModulateVelocity()` 算完之后赋的值(L75),
  Surprised 冻结时这个值恒为 `Vector3.zero`(见下方 §2),`Vector3.zero.normalized`
  在 Unity 里是 `Vector3.zero`(不会抛异常/NaN),所以冻结期间这一项贡献为 0。
- 结论:`goalDir` 在冻结期间退化成"纯粹指向 destPos 方向",和机器人朝向
  完全没有关系——这就是"对着空气吓一跳"的根因。

之后角速度被限幅、再用增量旋转落地,这一段本身就是"平滑转向"逻辑,不是瞬间赋值:

```csharp
// Base.cs L234-240
if (Mathf.Abs(angle) > ANGULAR_SPEED * Time.deltaTime)   // ANGULAR_SPEED = 120 deg/s (L15)
{
    angle = Mathf.Sign(angle) * ANGULAR_SPEED * Time.deltaTime;
}
transform.RotateAround(transform.position, Vector3.up, angle);
```

这一点对后面的方案选择很重要(见 §3、§5)。

---

## 2. 时序冲突确认:Modulate() 设朝向会不会被 Move() 覆盖?

`Update()`(Base.cs L73-89)的调用顺序是:

```csharp
void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());  // L75 -> 内部调用 modulator.Modulate()
    Move();                                          // L77
}
```

`ModulateVelocity()`(L171-174)只返回一个 `Vector3` 赋给 `velocity` 属性,
**不会、也没有能力**去碰 `transform.rotation`——`PedestrianModulator.Modulate()`
拿到的参数只有 `(Vector3 socialForceVelocity, Base self)`,`self.transform` 是
可以访问的,如果在这里直接写 `self.transform.rotation = ...`,时序上确实是
"先于" Move() 执行的。

但结论仍然是:**会被覆盖/对抗**。原因不是执行顺序反了,而是 Move() 在
L240 的 `transform.RotateAround` **不是幂等的**——它每帧都会重新根据
`goalDir`(destPos 方向)和当前 `transform.forward` 算一个新的增量角度,
并顶着 `ANGULAR_SPEED` 限幅继续转向 destPos 方向。也就是说:

- 第 1 帧:Modulate() 把朝向掰向机器人 → Move() 读到这个新 forward,
  发现和 goalDir(destPos 方向)有夹角,于是**当帧就开始把它往回转**。
- 后续每一帧:只要 `destPos != Vector3.zero`(冻结期间 destPos 通常不会被清零,
  PedestrianModulator 也没有去改它),Move() 都会持续把朝向"拉回" destPos 方向。
- 最终效果:两处写 rotation 的代码相互打架,朝向会在"看机器人"和"看 destPos"
  之间抖动或被 Move() 逐渐拉走,不会稳定地面向机器人。

所以 **不能**在 `ModulateVelocity`/`PedestrianModulator.Modulate()` 里直接设
`transform.rotation` 了事,必须让 Move() 自己知道"这一帧要面向机器人",
在 Move() 内部走一条不同的 goalDir 计算路径。

---

## 3. 推荐改法

### 方案对比

**a. Move() 里直接判断 PedestrianModulator 是否冻结**
直接 `GetComponent<PedestrianModulator>()` 或读一个缓存字段,`if (frozen) 面向机器人`。
最省事,但 Base.cs 是所有 agent 类型(SFAgent/ORCA/Playback/PlayerAgent)共用的
基类,现在唯一和"personality 系统"打交道的地方就是 `IVelocityModulator`
这个抽象接口(见 Base.cs L165-174 的既有注释,专门解释了这层解耦的目的)。
直接让 Move() 认识 `PedestrianModulator` 这个具体类型,等于绕开了这层已经
建好的抽象,并且 Base.cs 要重新实现"根据 SEAN.instance 找 robot 位置"的逻辑
(PedestrianModulator.DistanceToRobot 已经有一份,会重复)。

**b. 扩展 IVelocityModulator 接口,暴露"面向朝向覆盖"信息,Move() 读接口**
在 `IVelocityModulator` 上加一个方法,和 `Modulate()` 并列,例如:

```csharp
// IVelocityModulator.cs
public interface IVelocityModulator
{
    Vector3 Modulate(Vector3 socialForceVelocity, Base self);

    // 如果本帧需要覆盖默认的朝向计算(例如 Surprised 冻结要面向机器人),
    // 返回 true 并给出期望朝向(XZ 平面方向即可,不需要归一化);
    // 否则返回 false,Move() 走原来的 destPos 逻辑。
    bool TryGetFacingOverride(out Vector3 facingDirection);
}
```

`PedestrianModulator` 里实现时**复用 `ModulateSurprised()` 里已经算好的
robot 方向**,不用重新查 `SEAN.instance`:

```csharp
// PedestrianModulator.cs
private Vector3 facingOverrideDir = Vector3.zero;
private bool hasFacingOverride = false;

private Vector3 ModulateSurprised(Vector3 socialForceVelocity, Base self, Scenario.Robot robot)
{
    ...
    if (now < frozenUntil)
    {
        Vector3 toRobot = robot.position - self.transform.position;
        toRobot.y = 0;
        hasFacingOverride = toRobot.sqrMagnitude > 0.0001f;
        facingOverrideDir = toRobot;
        return Vector3.zero;
    }
    hasFacingOverride = false;
    return Scale(socialForceVelocity);
}

public bool TryGetFacingOverride(out Vector3 facingDirection)
{
    facingDirection = facingOverrideDir;
    return hasFacingOverride;
}
```

其他 personality(Scared/Curious/Indifferent)不设 `hasFacingOverride`,
默认 `false`,`Move()` 走原逻辑,完全不受影响。

这条路径和 Base.cs L165-170 那段既有注释里说的设计意图是一致的:
`IVelocityModulator` 就是"没有 modulator 就是 no-op"的可插拔钩子,现在只是
多给它一个可选的输出通道,Base.cs 不需要认识 `PedestrianModulator` 这个
具体类。而且目前全仓库只有 `PedestrianModulator` 一个实现类
(已用 `grep -rl IVelocityModulator` 确认),接口加方法不会破坏别的实现。

**c. 其他方式(不推荐)**
比如给 Base 加一个通用的 "facing target" 字段,由外部(PedestrianModulator)
直接写、Move() 读——本质和方案 b 一样,只是把"接口方法"换成"公开可写字段",
但字段没有接口那种"这是 modulator 的职责"的语义约束,容易被其他代码误写。
不如接口方法干净。

### 结论:推荐方案 b

理由:
1. 和现有的 `IVelocityModulator` 抽象一致,Base.cs 保持对 personality 系统无感知。
2. 不重复查 robot 位置——直接复用 `ModulateSurprised()` 里已经算好的向量。
3. 改动集中,`PedestrianModulator.cs` 内部改动、`IVelocityModulator.cs` 加一个
   接口方法、`Base.cs` 的 `Move()` 只加一段 if/else,不动其余逻辑。

（备注:`PedestrianSpawner.cs` 目前是直接 `GetComponent<PedestrianModulator>()`
读 `IsControllingDestination` 的,和方案 a 是同一种模式——但那是因为
`PedestrianSpawner.cs` 本身就是个 personality-spawning 专用脚本,和
`PedestrianModulator` 强耦合是合理的。`Base.cs` 不一样,它是所有 agent 类型
共用的基类,不应该被迫认识某个具体 personality 组件,所以这里不套用同一模式。)

---

## 4. 需不需要动 Base.cs 的 Move(),动多少

需要动,但改动量很小,只改 L213-217 这一段(计算 `goalDir` 的部分),
其余(限幅、`RotateAround`、动画参数、Debug 画线)完全不动:

```csharp
// 原 L213-217
Vector3 goalDir = nearestGoalPoint - transform.position;
float goalWeight = 0.5f;
goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;
goalDir.y = 0;

// 改为
Vector3 goalDir;
if (modulator != null && modulator.TryGetFacingOverride(out Vector3 overrideDir))
{
    goalDir = overrideDir;
}
else
{
    goalDir = nearestGoalPoint - transform.position;
    float goalWeight = 0.5f;
    goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;
}
goalDir.y = 0;
```

`modulator` 字段已经在 Base.cs 里存在并在 `Start()` 缓存好了(L20、L69),
直接用,不需要新增字段或 GetComponent。

---

## 5. 平滑转向 vs 瞬间面向

**推荐:复用现有的平滑机制,不要另写 Slerp。**

Move() 后半段(L234-240)本来就是把"目标朝向"和"当前朝向"的夹角限幅到
`ANGULAR_SPEED * Time.deltaTime`(120°/s)再用 `RotateAround` 增量转过去——
这本身就是一个逐帧平滑转向的实现,效果上等价于 Slerp。方案 b 只是把
"目标朝向"这个输入换成了机器人方向,后面的平滑限幅逻辑完全复用,不用改。

好处:
- 改动最小,不引入新的插值代码路径。
- 和行人平时走路转弯的手感一致(同样的最大角速度),不会显得突兀或太快。
- 如果以后想要"惊讶时转身更快",只需要在冻结期间对这一帧的角速度限幅单独
  调参(比如乘一个系数),而不需要重新设计转向机制。

不推荐瞬间面向(直接赋值 `transform.rotation`):会导致行人在触发冻结的瞬间
脸"啪"一下转过去,和播放的 Surprised 表情动画不搭调,视觉上比较突兀。

---

## 改动文件清单(未执行,仅设计)

| 文件 | 改动内容 |
|---|---|
| `IVelocityModulator.cs` | 接口新增 `bool TryGetFacingOverride(out Vector3 facingDirection)` |
| `PedestrianModulator.cs` | `ModulateSurprised()` 冻结分支里顺带记录朝向机器人的方向;新增 `TryGetFacingOverride()` 实现 |
| `Base.cs` | `Move()` 内 L213-217 的 `goalDir` 计算加一层 if/else,读 `modulator.TryGetFacingOverride()`;其余不动 |
