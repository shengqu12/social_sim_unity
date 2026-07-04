# Surprised "转身再转回" —— 诊断报告

只读诊断 + 方案,未改逻辑代码(`surpriseRadius` 已按你说"这个安全"直接改成
4.0)。结论:**这次不是动画骨骼自带转身,也不是 `OnAnimatorMove` 引用错——
是我上一轮"还原 Base.cs"这个操作本身留了一个坑:`Base.cs` 的 `Move()`
现在完全不知道"这个 agent 正 Surprised 冻结中",每一帧仍然照常朝 destPos
方向转,和 `OnAnimatorMove` 里朝机器人转的 Slerp 在同一帧里正面打架。**
"先转歪、再慢慢转回"这个特征描述,和这套"两个人同时抢着转"的机制精确对得上,
不是巧合。

---

## 1. OnAnimatorMove 是否真正接管了 —— 确认:接管本身没问题

**引用对不对:** `PedestrianModulator` 是在 `PedestrianSpawner.SpawnAgent()`
里用 `agent.gameObject.AddComponent<PedestrianModulator>();` 加上去的
(`agent` 就是 `AppearanceAvatar.Awake()` 里 `Instantiate` 出来的那个
Rocketbox avatar clone,`Animator`/`SFAgent`/`Rigidbody`/`CapsuleCollider`
都在同一个 GameObject 上)。`AddComponent<PedestrianModulator>()` 这一行
执行时会同步调用新组件的 `Awake()`,此时 `Animator.runtimeAnimatorController`
已经在更早的 `AppearanceAvatar.Awake()` 里设好了——`PedestrianModulator.Awake()`
里的 `GetComponent<Animator>()` 拿到的就是这个已经配置好的同一个 `Animator`,
**层级和时序都对,没有拿到 null 或者拿错对象。**

**会不会因为不可见而不被调用:** `Base.Start()` 里显式设了
`animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;`(`Base.cs` L65),
所以哪怕行人不在摄像机视野里,Animator 也会照常求值、照常调用
`OnAnimatorMove()`,不存在"因为看不见所以不转"的情况。

**冻结分支是不是真的在跑:** 上一轮加的调试红/蓝线就是挂在
`OnAnimatorMove()` 的冻结分支里的,只要 Scene 视图开着 Gizmos、Play 时
能看到红线(指向机器人)/蓝线(`transform.forward`),就说明这个分支确实
每帧在执行、`deltaPosition`/`deltaRotation` 也确实被跳过没有 apply。
建议你顺手确认一下这两条线现在还在(应该在,这轮没动它)。

**综合结论:`OnAnimatorMove` 这条链路本身没有问题,`animator` 引用是对的,
root motion 的 delta 也确实被冻结分支丢弃了。** 问题不在这里。

---

## 2. "转身"是 transform 朝向变了,还是动画骨骼自己在演

先说这次的判断方法:**"先转歪、再慢慢转回"这个时间形状本身就是线索**——
如果纯粹是骨骼动画自己在演一段"转身看"的表演(躯干动画,不碰 root
transform),`transform.rotation`(蓝色调试线代表的方向)应该全程稳定指向
机器人,不会有"跟着转歪"的阶段;如果纯粹是 `transform.rotation` 被什么
东西设歪了且没人管,应该是"歪了就一直歪着",不会自己"慢慢转回"。而
"转歪→自我修正回来"这种指数收敛的形状,是**两股力量同时、每帧都在起作用,
其中一股在把它拉偏、另一股在把它拉回**的典型特征——查代码,这正是当前
`Base.cs`/`PedestrianModulator.cs` 的实际结构。

**代码层面确认:**

`Base.cs` 上一轮已经按你的要求整个还原,现在 `Move()`(L201-217)完全不
认识 personality/frozen 这个概念了:

```csharp
// Base.cs, Update() (L73-77)
void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());   // Surprised 冻结时这里返回 Vector3.zero
    Move();                                           // 但下面这行完全不管 personality,照样跑
}

// Base.cs, Move() (L201-217),对 SFAgent 这类 agent 无条件执行
Vector3 goalDir = nearestGoalPoint - transform.position;   // 指向 destPos(行人的随机游走目标),和机器人无关
float goalWeight = 0.5f;
goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;  // velocity=0,这一项只是贡献 0,不代表"不转"
goalDir.y = 0;
angle = -Vector3.SignedAngle(goalDir, transform.forward, Vector3.up);
...
transform.RotateAround(transform.position, Vector3.up, angle);   // 每帧真实执行,朝 destPos 方向转
```

**冻结时 `velocity` 确实是零向量(`ModulateSurprised` 返回 `Vector3.zero`),
但 `goalDir` 另外 50% 权重来自 `nearestGoalPoint - transform.position`——
这一项和 `velocity` 无关,冻结时照样是非零的、指向行人原本要走的随机游走
目标,和机器人方向大概率完全不同。** 也就是说:**`Move()` 在 `Update()`
阶段,每一帧都在把朝向往 destPos 方向拉**(受 `ANGULAR_SPEED`=120°/s 限幅,
不是瞬间转到位,但持续在拉)。

然后同一帧稍后,`PedestrianModulator.OnAnimatorMove()` 的冻结分支执行:

```csharp
// PedestrianModulator.cs, OnAnimatorMove() 冻结分支
Quaternion targetRot = Quaternion.LookRotation(toRobot.normalized, Vector3.up);
transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * facingTurnSpeed);
```

这里的 `Quaternion.Slerp` 读的是**这一帧已经被 `Move()` 拉偏过的**
`transform.rotation`,往机器人方向修正一部分(`facingTurnSpeed=10`,
60fps 下大约每帧修正剩余偏差的 16.6%)。

**逐帧看这是什么形状:** `Move()` 每帧把朝向往 destPos 方向拉开一个固定
角速度(clamp 在 120°/s,换算到 60fps 大约 2°/帧);`OnAnimatorMove` 每帧
把"当前偏差"按比例拉回一部分(16.6%)。这是一个经典的"漏斗"系统——两者会
在某个**非零的稳定偏差**附近达到平衡(`Move()` 每帧加进来的偏差 ≈
`OnAnimatorMove` 每帧修正掉的偏差),从冻结一开始的"转歪"到平衡点之间是
一段指数趋近的曲线,视觉上正好就是"先转歪、再慢慢转回(但不一定完全对准)"
——和你描述的现象完全吻合。

**结论:这次是 `transform.rotation` 真的在变,根因是 `Base.cs` 的 `Move()`
在 Surprised 冻结期间完全不知情、继续按 destPos 转,和 `OnAnimatorMove`
的 Slerp 每帧抢着改同一个 `transform.rotation`。** 不是动画骨骼自己在演
(那种情况下 `transform.rotation`/蓝线应该全程稳定,只有可视网格在动)。
这轮不需要去啃 Surprised.fbx 的骨骼曲线——代码层面的两处每帧都在写
`transform.rotation`,已经足够解释现象,不用叠加"动画内容"这个第二成因
来凑。

---

## 3. 为什么会变成这样——是上一轮"还原 Base.cs"漏掉的一环

问题出在一个我当时的判断失误:引入 `OnAnimatorMove` 时,判断依据是
"root motion 会被它接管,所以 Base.cs 那套 `TryGetFacingOverride` 覆盖
逻辑就不需要了"——**这个判断只对了一半**。`OnAnimatorMove` 确实接管了
"动画驱动的 root motion"(`animator.deltaPosition`/`deltaRotation`),
但 `Base.cs` 的 `Move()` 里那句 `transform.RotateAround(...)` **根本不是
root motion**,是脚本直接对 `transform` 发号施令的手动旋转,和
`OnAnimatorMove` 完不完全没关系、不受它节制。所以还原 `Base.cs` 那一步,
相当于把"每帧都会执行、和 Surprised 无关的转向逻辑"重新放回了赛场,而
`OnAnimatorMove` 那边并不知道场上还有这么一个对手。

---

## 4. 修法

**思路:不能再靠 `OnAnimatorMove` 单方面死扛,得让 `Move()` 在 Surprised
冻结期间干脆不参与转向这件事——两个人只留一个人转,而不是两个人往两个
方向拉扯再看谁力气大。**

和上一轮被移除的 `TryGetFacingOverride`(“给一个方向,Move() 用它替换
goalDir”)不同,这次不需要再让 `Move()` 知道"该往哪转"(那件事现在完全由
`OnAnimatorMove` 负责),只需要让 `Move()` 知道"这一帧我要不要转"——
接口改得比上次更小:

```csharp
// IVelocityModulator.cs,新增(替代已经删除的 TryGetFacingOverride)
bool IsRotationSuppressed();
```

```csharp
// PedestrianModulator.cs
public bool IsRotationSuppressed() =>
    personality == PersonalityType.Surprised && Time.time < frozenUntil;
```

```csharp
// Base.cs, Move() —— 只加一层最外层的判断,内部 goalDir/angle 计算一个字不改
if (modulator == null || !modulator.IsRotationSuppressed())
{
    Vector3 goalDir = nearestGoalPoint - transform.position;
    float goalWeight = 0.5f;
    goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;
    goalDir.y = 0;
    angle = -Vector3.SignedAngle(goalDir, transform.forward, Vector3.up);
}
```

这样,Surprised 冻结期间 `Move()` 直接不算 `angle`(保持默认值 0,
`RotateAround` 转 0 度等于没转),`transform.rotation` 这一帧唯一的写入者
就是 `OnAnimatorMove()` 的 Slerp,不会再被拉扯,平滑收敛到正对机器人。
非冻结的所有场景(`modulator == null`、Indifferent、Scared、Curious、
Surprised 非冻结)`IsRotationSuppressed()` 都返回 false,`Move()` 行为
和现在完全一样,不受影响。

**要不要动 Base.cs:需要,但改动量很小**——只加一个 if 包住已有的三行
计算,不改 `goalDir`/`angle`/`RotateAround` 本身任何一行,也不像上上轮
那样需要在 `Move()` 内部分叉出"用哪个方向"的逻辑(那部分已经交给
`OnAnimatorMove` 了,这次 `Base.cs` 只需要回答"要不要转"这一个布尔问题)。

---

## 5. surpriseRadius

已按要求改成 **4.0**(`PedestrianModulator.cs` L90,`public float
surpriseRadius = 4.0f;`),这一步已经落地,不影响本报告其余部分是"只诊断
不改代码"的定位。
