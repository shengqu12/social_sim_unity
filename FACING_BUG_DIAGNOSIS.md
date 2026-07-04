# Surprised 朝向覆盖转错方向 —— 诊断报告

只读诊断,未改代码。结论先说:**逐行核算下来,`Base.cs` 的 `Move()` 公式本身,
以及 `overrideDir = robot.position - self.transform.position` 的写法,在符号/参数顺序/
归一化上都是对的,和原有 `goalDir` 用的是同一套约定,推导不出这四个假设里的
任何一个成立。** 所以这份报告的重点是:把每个假设逐一证伪的过程给你看,再给
接下来该去 Unity 里实际验证什么(因为纯读代码已经排除了"公式错了"这个类别,
剩下的多半是运行时/资产层面的东西,静态读代码看不出来)。WASD 部分confirmed 是真
bug,细节在最后。

---

## 1. Move() 朝向计算完整链路(带行号)

当前 `Base.cs`(已包含你上一轮加的 `overrideDir` 分支):

```csharp
// L201 Move()
private void Move()
{
    float angle = 0;
    if (!(GetType().Equals(typeof(Scenario.Agents.Playback.Agent)) || GetType().Equals(typeof(PlayerAgent))))
    {
        if (destPos == Vector3.zero) { return; }               // L208

        Vector3 goalDir;
        if (modulator != null && modulator.TryGetFacingOverride(out Vector3 overrideDir))  // L214
        {
            goalDir = overrideDir;                              // L216
        }
        else
        {
            goalDir = nearestGoalPoint - transform.position;    // L220
            float goalWeight = 0.5f;
            goalDir = goalWeight * goalDir.normalized + (1 - goalWeight) * velocity.normalized;  // L222
        }
        goalDir.y = 0;                                          // L224
        angle = -Vector3.SignedAngle(goalDir, transform.forward, Vector3.up);  // L225
    }
    ...
    if (Mathf.Abs(angle) > ANGULAR_SPEED * Time.deltaTime)       // L243
    {
        angle = Mathf.Sign(angle) * ANGULAR_SPEED * Time.deltaTime;
    }
    transform.RotateAround(transform.position, Vector3.up, angle);  // L248
    ...
}
```

逐段解释:

**goalDir → angle(L225)**

`Vector3.SignedAngle(from, to, axis)` 的 Unity 官方实现是:

```
unsignedAngle = Angle(from, to)                         // 0~180°,无符号
sign = Sign(Dot(axis, Cross(from, to)))
return unsignedAngle * sign
```

代码写的是 `-SignedAngle(goalDir, transform.forward, up)`,注意参数顺序是
`from=goalDir, to=transform.forward`,和"直觉上"应该写的
`SignedAngle(forward, goalDir, up)`(forward 转到 goalDir 需要转多少度)是反的,
但外面又套了一个负号。因为 `SignedAngle` 关于 from/to 是反对称的
(`SignedAngle(A,B,axis) = -SignedAngle(B,A,axis)`,对调 from/to 就是变号),
两次变号抵消:

```
-SignedAngle(goalDir, forward, up) = SignedAngle(forward, goalDir, up)
```

所以 **`angle` 最终等于"把 transform.forward 转到 goalDir 方向需要转多少度"**——
这是一个标准的"转向目标方向"公式,`goalDir` 只要是"指向目标"的世界空间向量就行,
长度、来源都无所谓(`SignedAngle`/`Cross` 只看方向)。

用一个具体例子验证:agent 朝 +Z(forward=(0,0,1)),goalDir=(1,0,0)(目标在正右方 +X)。
`Cross((0,0,1),(1,0,0)) = (0,1,0)`,`Dot(up,(0,1,0))=1>0`,`Angle=90°`,
所以 `SignedAngle(forward,goalDir,up)=+90°`。Unity 里 Y 轴正向旋转是"从上往下看
顺时针"(这是 Unity 社区公认的约定,鼠标视角、指南针脚本等到处能验证),
从 +Z 顺时针转 90° 正好转到 +X——和 goalDir 完全对上。**公式本身没问题。**

**angle → RotateAround(L243-248)**

先按 `ANGULAR_SPEED`(120°/s,L15)限幅,只影响转多快,不影响转的方向
(`Mathf.Sign(angle)` 保号)。然后 `transform.RotateAround(transform.position,
Vector3.up, angle)` 是**增量旋转**(在当前朝向基础上再转 angle 度),不是绝对赋值,
所以是逐帧平滑转过去的,不是瞬间转到位——这也是为什么"确实开始转了"符合预期
(平滑转向机制在生效),问题在于转的目标点不对/转到中途又被带偏。

**原 goalDir 的方向约定**

`nearestGoalPoint - transform.position`:`nearestGoalPoint` 是 nmPath 下一个
拐点(目的地方向),减去当前位置 → **指向目标的向量**(标准"终点减起点"写法)。
混合的 `velocity.normalized`(社会力模型算出来的实际速度)同样是"朝行进方向"的向量。
两者同号,混合后依然是"指向想要面朝的方向"。这就是整套公式对 `goalDir` 的
唯一约定:**世界空间、XZ 平面、指向目标**,不需要额外归一化或坐标变换
(L224 的 `goalDir.y=0` 已经统一处理了拍平)。

---

## 2. 四个假设逐一核实

你列的四个疑点,逐一看:

**a. SignedAngle 参数顺序问题?—— 不是。**
上面推导过,`-SignedAngle(goalDir, forward, up)` 这个"反着写再取负"的写法,
数学上等价于 `SignedAngle(forward, goalDir, up)`,是自洽的,不依赖 `goalDir`
具体是什么。这个负号不是给某个特定 `goalDir`(比如原来的目的地方向)专门配平的,
它对任何"指向目标"的 `goalDir` 都成立,包括 `overrideDir`。

**b. 负号配 overrideDir 时符号反了?—— 不是。**
`overrideDir = robot.position - self.transform.position` 和 `nearestGoalPoint
- transform.position` 是同一个写法模式("目标位置 - 自身位置"),同样的
"指向目标"约定,没有反过来写成 `self - robot`。代入公式后走的是同一条计算路径,
没有理由单独在这一条分支上符号反转。

**c. overrideDir 需要额外归一化/坐标变换?—— 不需要。**
`SignedAngle`/`Cross` 只看方向不看长度,`overrideDir` 不归一化也完全没问题
(原代码里 `goalDir` 在 else 分支归一化是因为要按 0.5/0.5 权重跟 velocity 混合,
纯粹是混合比例的需要,不是 SignedAngle 本身的要求)。坐标系上,`robot.position`
经 `Robot.cs` 的 `position` 属性返回的就是世界坐标(`base_link.transform.position`),
和 `self.transform.position` 同一个世界坐标系,不存在局部/世界坐标不匹配的问题。
`goalDir.y = 0` 在 if/else 分支合并之后统一执行(L224),`overrideDir` 一样会被拍平,
不会因为跳过了 else 分支就少做这一步。

**d. transform.forward 和行人实际朝向定义不一致?—— 排除不了完全,但可能性很低。**
这个假设唯一站得住脚的情况是:行人的可视模型(骨架/Mesh)"真正的脸"朝向,
不等于 root transform 的本地 +Z——比如模型导入时朝向就是反的。但如果真是这样,
**正常走路(else 分支)也会跟着错**,因为正常走路和 surprised 冻结走的是
完全相同的 `SignedAngle(forward, goalDir, up) → RotateAround` 这一段代码,
唯一的区别只是 `goalDir` 的来源不同。你没有反馈正常走路时人物朝向有问题,
只反馈 surprised 冻结时不对,说明"transform.forward vs 模型实际朝向"这层
如果真有偏差,那也是两条路径共享的常量偏差,不会是"只在 override 分支才转错"
的解释。所以这一条不太可能是本次症状的根因,但值得在 §3 里顺手验证一下。

**结论:代码里没能找出一个能解释"转错方向"的符号/公式 bug。** 这四个假设都
不成立,和 WASD 那种"约定搞反了"的 bug(见 §4)不是一回事。

---

## 3. 既然公式没问题,该怎么修 / 接下来验证什么

### 先说清楚:不要为了"看起来像修好了"而反手改 overrideDir 的符号

因为 §2 证明了当前写法(`robot.position - self.transform.position`)是符合
整套公式约定的唯一正确写法。如果强行改成 `self.transform.position -
robot.position`(或者在外面加个 `-1`),会让 override 分支和 else 分支用两套
相反的方向约定,即使"看起来"暂时转对了(比如恰好抵消了另一个真实 bug),
也是蒙对的,以后大概率在别的角度/场景下露馅。真正的问题大概率不在这段 C#
代码里,而在代码够不到的地方——继续往下看。

### 建议的验证顺序(按可能性排序)

**① 先肉眼确认 overrideDir 本身有没有指对**

在 `PedestrianModulator.ModulateSurprised()` 冻结分支里(现在的 L280-286)
临时加一行调试画线,直接看 Scene 视图里这根线是不是真的指向机器人:

```csharp
if (now < frozenUntil)
{
    Vector3 toRobot = robot.position - self.transform.position;
    toRobot.y = 0;
    hasFacingOverride = toRobot.sqrMagnitude > 0.0001f;
    facingOverrideDir = toRobot;
    Debug.DrawRay(self.transform.position, toRobot, Color.cyan, 0.1f);  // 临时调试用
    return Vector3.zero;
}
```

同时对比 `transform.forward`(Base.cs 已有 `ShowDebug` 分支画的是 `velocity`,
冻结时 `velocity` 恒为零,画不出线,帮不上忙,建议额外加一条画 `transform.forward`
的线)。如果青色线确实指向机器人,但角色模型转过去之后脸没对着机器人——
说明问题在下游(模型朝向或动画),不在这段脚本;如果青色线本身就没指向机器人
(比如指向了某个奇怪的点),说明问题在 `robot`/`self` 的取值上(见下面②③),
而不是符号写法。

**② 确认场景里 `SEAN.instance.robot` 拿到的就是你以为的那个机器人**

`PedestrianModulator.Modulate()`(L96-119)外层有 try/catch,场景里有 0 个或
超过 1 个机器人时会直接吞掉异常、整个 Surprised 分支都不跑(连冻结和动画都不会触发)。
既然你说冻结和动画已经能正常触发,说明场景里能唯一确定到一个 robot,这条
基本可以排除,但如果场景里其实有多个 Robot 组件只是没报错(比如都被同一个
try/catch 覆盖住了故障),还是值得跑起来时打印一下 `robot.name` 确认。

**③ 确认 `robot.position`(即 `base_link.transform.position`)不是一个偏得离谱的点**

`Robot.cs` 里 `transform` 被 `new` 关键字遮蔽,返回的是 `base_link.transform`
而不是 Robot 挂载脚本那个 GameObject 自身的 transform(L106-112)。对于
URDF 导入的多连杆机器人(Fetch/Unitree A1 之类),`base_link` 的原点理论上应该
在底盘几何/旋转中心附近,但如果 prefab 装配有问题,`base_link` 有可能偏到
底盘边缘甚至別的连杆上,导致"指向机器人"实际指向了机器人身体的某个角落而不是
中心——这最多是偏几十度,不太会造成"完全反方向",但值得跟①的调试线一起看一眼。

**④ 排查 SurprisedReaction 动画本身有没有在冻结期间"偷偷转"角色**

已确认 `BaseSFControllerNormalized.controller` 里 `SurprisedReaction` 状态
挂的 `m_Motion` 指向 `guid: a48c91245085a664d94f998ef9891fd6`,对应
`Assets/IVI/Animations/Interactions Pack/Surprised.fbx`(不是
`Assets/CustomAnimations/Surprised.fbx`——那个 fbx 的 `.meta` 里其实有
`rigImportErrors: "...Transform 'Hips' for human bone 'Hips' not found"`,
Avatar 是坏的,但好在它没被这个 Controller 引用,不影响当前这个 bug,只是
顺手提醒一下这个坏资产迟早要修/清理)。

实际生效的这份 `Surprised.fbx.meta` 里,clip 的 Root Transform Rotation 设置是
`keepOriginalOrientation: 0`(即 Root Rotation 已经 Bake Into Pose,不会作为
额外的 root motion 转出去),所以理论上这个动画片段不该在播放期间对 Y 轴朝向
额外加一个转动去跟脚本打架。但 `Animator.applyRootMotion = true`
(Base.cs L64 设置)整体是开着的,而且 `Locomotion → SurprisedReaction` 是
`Any State` 触发、`Has Exit Time` 关闭(意味着可能在任意时刻打断当前动作),
这类打断通常会有一段默认的 Transition Duration 混合(如果没手动设成 0),
混合期间两个状态的 pose 会插值——如果混合窗口里恰好跨越了 Move() 刚把
角色转过去的那几帧,视觉上可能会看到一个短暂的"回摆"或"抖动",容易被
误读成"转错方向"。建议在 Animator 窗口里检查一下这条 `Any State →
SurprisedReaction` 的 Transition Duration 是不是 0(或者足够小),排除这个
可能性。

### 一句话总结

`overrideDir` 该怎么给已经是对的(`robot.position - self.transform.position`,
不归一化,不用额外变换),不需要改 C#。下一步不是改符号,是照 ①→④ 的顺序去
Unity 里实际看一眼 `overrideDir` 和 `transform.forward` 的真实值,把"公式错"
和"下游(动画/rig)问题"分开。

---

## 4. WASD A/D 是否反了(只报告,不修)

**确认:反了。** `Assets/Scripts/SEAN/Control/VelocityController.cs` L131-138:

```csharp
if (UnityEngine.Input.GetKey(KeyCode.A))
{
    moveHorizontal = -1.0f; // Move left
}
else if (UnityEngine.Input.GetKey(KeyCode.D))
{
    moveHorizontal = 1.0f; // Move right
}
...
targetAngVelocity = moveHorizontal * 1.0f; // rad/s
```

`targetAngVelocity` 随后在 `DriveRigidbody()`(L197-206)和 `DriveArticulation()`
(L215-258)里都是**取负**之后才写进 Unity 的角速度:

```csharp
// DriveRigidbody, L199-201
rb.angularVelocity = ... new Vector3(0, -1 * targetAngVelocity, 0);
// DriveArticulation, L253
artRoot.angularVelocity = levelAngVel + new Vector3(0f, -1 * targetAngVelocity, 0f);
```

这个 `-1 *` 是把 `targetAngVelocity` 当成 **ROS `cmd_vel.angular.z` 的约定**
(正值 = 逆时针 = 左转)来处理,再转换成 Unity 的 Y 轴角速度(Unity Y 轴正值
是"从上往下看顺时针"= 右转),两者相反所以要取负——这一步转换本身没错,
`CmdVelMessage`(L260-267)走 ROS 消息时也是直接把 `msg.angular.z` 原封不动
赋给 `targetAngVelocity`,同一套约定,自洽。

**问题出在 WASD 按键映射(L131-138)没有跟着这套"正值=左转"的约定走**,而是
按"D 是右边的键所以给正值"这种更直觉但相反的映射来写的:

- 按 `A`:`moveHorizontal = -1` → `targetAngVelocity = -1`(按 ROS 约定=右转)
  → `DriveRigidbody` 里取负变成 Unity Y 角速度 `+1`(顺时针=右转)。
  **代码注释写的是"Move left",实际效果是右转。**
- 按 `D`:`moveHorizontal = +1` → `targetAngVelocity = +1`(按 ROS 约定=左转)
  → Unity Y 角速度 `-1`(逆时针=左转)。
  **代码注释写的是"Move right",实际效果是左转。**

**和本次 Surprised 朝向 bug 是不是同一类问题:是同一个"类别"(方向/角度符号
约定不统一),但不是同一个坑。** WASD 这个是"两套明确存在、且相反的约定
(ROS 正值=左转 vs. 键盘直觉正值=右转)在同一条数据链路的两端各用了一套,
中间少做/搞反了一次转换";而 §1-§3 排查下来,Surprised 朝向这边只有
**一套约定**(‘指向目标’)从头到尾贯穿,没发现两套约定互搏的地方。
只是说这个代码库里确实存在"方向符号容易搞混"这类问题(Howard 那边这次
就踩了),所以才更值得把 §3 列的怀疑点仔细过一遍确认清楚,而不是想当然
地觉得"这次这个也一定是符号错了"就去动 `overrideDir` 的符号。

这一条只报告给你,VelocityController.cs 不属于本次改动范围,没有动。
