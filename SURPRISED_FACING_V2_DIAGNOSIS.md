# Surprised 持续朝向机器人 —— LateUpdate 为什么还是被覆盖 + V2 方案

只读诊断 + 方案,未改代码。结论先说:**LateUpdate 之所以还是被盖掉,根子在于
项目里从来没有人实现过 `OnAnimatorMove()`——这意味着 root motion 是 Unity
"自动应用"的,而"自动应用"这一步相对于脚本 `LateUpdate()` 的先后顺序,官方
文档画的是一条简化的时间线,实际引擎行为并不严格保证。与其继续猜"到底谁在
LateUpdate 前面还是后面",不如换成 Unity 专门为"我要自己接管 root motion"
设计的机制——`OnAnimatorMove()`。这不是权宜之计,是 Unity 官方文档明确给出的
标准解法。而且新需求(持续盯着机器人转,不是定住转一次)刚好和 `OnAnimatorMove`
"每帧都会调用"的特性完全契合,不需要额外加逐帧状态。**

---

## 1. Root motion 到底在什么时机、通过什么方式应用到 transform

**先查项目里有没有人已经接管过:**

```
grep -rn "OnAnimatorMove" Assets/Scripts/   →  没有任何匹配
```

**确认:整个项目里,包括 `Base.cs`,从来没有任何脚本实现过 `OnAnimatorMove()`。**
这意味着从头到尾,root motion 的应用方式一直是 Unity 的**默认自动模式**——
`Animator.applyRootMotion = true`(`Base.cs` L64 设置)之后,Unity 在内部
自己把当前动画片段这一帧的位移/旋转 delta 加到 `transform.position`/
`transform.rotation` 上,开发者的脚本完全不参与、也看不到这个过程。

两个 avatar prefab(`Female_Adult_01.prefab` L138、`Female_Adult_02.prefab` L53)
的 `Animator.m_UpdateMode: 0`,即 `AnimatorUpdateMode.Normal`——动画更新跟着
普通 `Update()` 走(不是 `AnimatePhysics`/`UnscaledTime`),这个没有问题,
排除"更新模式不同步"这个可能性。

**关键问题:Unity 官方"Order of Execution for Event Functions"那张图,把
"Animation"画在 `Update` 和 `LateUpdate` 之间——按这张图,root motion 应该
在你的 `LateUpdate()` 之前就已经应用完了,`LateUpdate()` 里设的朝向应该是
这一帧"最后写"的,理应生效。但你实测下来并不是这样。**

这里我要诚实说清楚:**这张图是简化示意,不是精确到"每个 Animator 组件在
每一帧里相对于每一个脚本 LateUpdate 的确切调用顺序"的保证。** Mecanim/
PlayableGraph 驱动的 Animator,其内部动画求值和 root motion 写入是通过
Unity 的动画 job 系统调度的,官方文档本身也承认:如果你需要"确定性地"
控制 root motion 什么时候、怎么应用,标准做法是自己写
`OnAnimatorMove()`,而不是依赖 `Update`/`LateUpdate` 相对顺序——这也是为什么
Unity 专门开了这个回调:**只要 GameObject 上任意一个脚本实现了
`OnAnimatorMove()`,Unity 就会整个关掉"自动应用"这条路径,改成调用你的
`OnAnimatorMove()`,由你自己决定要不要、怎么把 `animator.deltaPosition`/
`animator.deltaRotation` 写进 transform。** 这不是"猜时机去插一刀",是把
"谁来 apply root motion"这件事从"Unity 内部黑盒、时机不受你控制"直接换成
"你自己的代码、原地执行、没有时序竞争这一说"。

你这次实测(LateUpdate 里设了朝向,结果还是被带偏)本身就是最直接的证据:
**说明这台项目/这个 Unity 版本下,自动 root motion 的应用节点,实际上并不
严格保证在你的 `PedestrianModulator.LateUpdate()` 之前完成**——具体是在
LateUpdate 之后,还是在 LateUpdate 之中和你的脚本竞争 GameObject 遍历顺序,
光靠读代码/文档确定不了(需要 Profiler 抓帧才能看到 Unity 内部的实际调用点)。
但**不需要**把这个内部细节确定到底,因为 `OnAnimatorMove()` 从根上绕开了
这个问题——见下面方案。

---

## 2. Surprised.fbx 的 root rotation 改的是什么

延续上一份 `SURPRISED_ROOTMOTION_DIAGNOSIS.md` 的结论:这个 clip 改的是
**`Animator` 内部按当前采样时间算出来的 `deltaRotation`**(每帧一个增量,
不是一次性绝对值),然后这个增量在"应用 root motion"这一步被加到
`transform.rotation` 上。它不是直接、绕开 Animator 去改 `transform.rotation`
本身——`transform.rotation` 只是最终的"落地点",真正的数据源头是
`Animator.deltaRotation`,每一帧都会重新算一次(只要 SurprisedReaction 这个
state 还在播,且它的 root rotation 没有被 Bake Into Pose 完全吸收,这个
delta 就不是零)。这一点很重要:**它是持续每帧在改,不是触发的那一下改一次
就完了**——所以哪怕你在某一帧手动把朝向掰回来,下一帧动画系统又会重新算一个
新的 `deltaRotation` 加上去,是一场"每帧都要打一次"的仗,不是"打赢一次就
结束"的仗。`OnAnimatorMove()` 正好也是每帧都会被调用一次(和动画求值同步),
天然打得对称。

---

## 3. 让"持续朝机器人转向"真正生效的方案

### 用 OnAnimatorMove(),只加在 PedestrianModulator.cs,不用动 Base.cs

`OnAnimatorMove()` 只要求"挂在和 `Animator` 同一个 GameObject 上的任意脚本
实现它"——`PedestrianModulator` 本来就是 `AddComponent` 到和 `Animator`/
`Base`/`SFAgent` 同一个 GameObject 上的(见 `PedestrianSpawner.SpawnAgent()`:
`agent.gameObject.AddComponent<PedestrianModulator>()`),所以**不需要改
`Base.cs` 一个字**,直接在 `PedestrianModulator.cs` 里加这个方法就够了。

```csharp
private Animator animator;  // cache once, avoid GetComponent every frame

void Awake()
{
    animator = GetComponent<Animator>();
}

void OnAnimatorMove()
{
    bool facingRobot = personality == PersonalityType.Surprised && Time.time < frozenUntil;

    if (!facingRobot)
    {
        // Not in a frozen Surprised reaction -- reproduce exactly what Unity's automatic
        // root motion application would have done, so every other personality/state
        // (Scared/Curious/Surprised-not-frozen/Indifferent-with-a-modulator) is unaffected.
        transform.position += animator.deltaPosition;
        transform.rotation *= animator.deltaRotation;
        return;
    }

    // Frozen Surprised: still take the clip's translation (velocity is already zero during
    // freeze, so this is ~0 anyway, but stays consistent with default behavior), but discard
    // its rotation delta entirely -- that's the piece that was fighting us -- and instead
    // face the robot ourselves, continuously, every frame OnAnimatorMove runs.
    transform.position += animator.deltaPosition;

    Scenario.Robot robot;
    try
    {
        if (SEAN.instance == null) { return; }
        robot = SEAN.instance.robot;
    }
    catch (System.Exception)
    {
        return;
    }

    Vector3 toRobot = robot.position - transform.position;
    toRobot.y = 0;
    if (toRobot.sqrMagnitude <= 0.0001f) { return; }

    // Vector3.up as the up-vector: pure yaw, never tips onto its side/back.
    Quaternion targetRot = Quaternion.LookRotation(toRobot.normalized, Vector3.up);
    transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * facingTurnSpeed);
}
```

`LateUpdate()` 里原来那版(上一轮加的)要整个删掉,不能两个都留——`OnAnimatorMove`
和 `LateUpdate` 都在每帧对 `transform.rotation` 做 Slerp,两处叠加会导致
转速不可控(相当于把同一个插值系数应用了两次)。调试红蓝线原样保留,搬到
`OnAnimatorMove()` 末尾。

### 为什么这样做,`facingRobot` 为 false 的分支不能省

`OnAnimatorMove()` 一旦在这个 GameObject 上出现(不管是哪个脚本实现的),
Unity 就**整体**关掉这个 GameObject 的自动 root motion,不管当前是哪个
personality、哪个 state。也就是说:一旦 `PedestrianModulator` 上加了
`OnAnimatorMove()`,**Scared/Curious/Surprised-非冻结阶段这些原本靠 Unity
自动应用 root motion 走路的行为,如果这里不手动补上 `deltaPosition`/
`deltaRotation`,它们会突然"原地不动"**——因为没人再把动画算出来的位移/
转身加到 transform 上了。所以 `!facingRobot` 分支里手动复刻默认行为
(`transform.position += animator.deltaPosition; transform.rotation *=
animator.deltaRotation;`)不是可选的,是防止这次改动波及其他 personality
的必要部分。

**Indifferent 且没挂 `PedestrianModulator` 的行人完全不受影响**——`OnAnimatorMove`
只在挂了这个组件的 GameObject 上生效,没挂组件的 SFAgent/ORCA/Playback
agent 走的还是 Unity 原来的自动路径,行为零变化。

### 需不需要动 Base.cs?

**不需要。** `Base.cs` 现在完全不知道 root motion 是怎么被接管的,`Move()`
里正常的 `RotateAround` 转向逻辑(destPos 方向)在 Surprised 非冻结时继续跑,
`OnAnimatorMove()` 每帧把它算出来的转向"复刻"一遍应用上去,等价于原来
Unity 自动帮你做的事——只是这次由 `PedestrianModulator` 亲自动手,顺便在
冻结那一小段时间里,把"复刻默认行为"换成"转向机器人"。`TriggerAnimation`
那个公开方法保留不动,`Move()` 上一轮已经还原成原样,这次也不用再碰它。

### 和"持续朝向"这个新需求的关系

`OnAnimatorMove()` 每帧(和 Animator 求值同步,`updateMode: Normal` 即每个
`Update` 周期一次)都会重新执行一遍,`toRobot` 每次都用 `robot.position` 现算,
机器人动了这一帧就能读到新位置——天然满足"机器人移动,行人朝向跟着转"的
持续跟随要求,不需要额外维护"目标位置是不是变了"之类的状态。转速由已有的
`facingTurnSpeed`(上一轮加的 `[Header("Surprised")]` 字段)控制,沿用不变。

---

## 改动文件清单(未执行,仅方案)

| 文件 | 改动内容 |
|---|---|
| `PedestrianModulator.cs` | 删除上一轮的 `LateUpdate()`;新增 `private Animator animator` 字段 + `Awake()` 里 `GetComponent<Animator>()` 缓存;新增 `OnAnimatorMove()`,冻结时丢弃动画旋转、持续转向机器人,否则原样复刻默认 root motion;调试红蓝线搬到 `OnAnimatorMove()` 里 |
| `Base.cs` | 不改 |
| `IVelocityModulator.cs` | 不改(`TryGetFacingOverride` stub 继续保留,不涉及本次改动) |
