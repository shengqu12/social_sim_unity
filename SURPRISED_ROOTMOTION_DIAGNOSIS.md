# Surprised 转错方向 —— Root Motion 诊断

只读诊断,未改代码/未改任何导入设置。结论:**你观察到的现象(红线指对、
动画一播身体就转到别的方向)+ Unity 动画执行时序 + 这个 clip 的原始
mocap 数据本身带明显旋转,三条证据互相印证,root motion 覆盖脚本朝向
是目前最站得住脚的解释。** 但有一处设置项(Bake Into Pose 具体勾选状态)
只靠读 `.meta` 文本我没法 100% 确定,报告里会明说哪里需要你在 Editor 里
肉眼确认一下,不是我在这里含糊。

---

## 1. SurprisedReaction 用的到底是哪个 clip —— 再次确认

`Assets/IVI/Controllers/BaseSFControllerNormalized.controller` 里
`SurprisedReaction` 状态块(L748-772):

```yaml
m_Name: SurprisedReaction
...
m_Motion: {fileID: 7400000, guid: a48c91245085a664d94f998ef9891fd6, type: 3}
```

`guid: a48c91245085a664d94f998ef9891fd6` 对应
`Assets/IVI/Animations/Interactions Pack/Surprised.fbx.meta` 里的
`guid:`(第 2 行)——**确认无误**,和上一份诊断报告的结论一致,不是
`Assets/CustomAnimations/Surprised.fbx`(那份 guid 是 `449ad7e8...`,
且那份的 Avatar 导入本身是坏的,但没被这个 Controller 引用,不影响本次)。

顺带确认了两条转场的具体参数(跟你背景描述的一致):

```yaml
# Any State -> SurprisedReaction(触发 Surprised trigger)
m_TransitionDuration: 0.25
m_ExitTime: 0.75
m_HasExitTime: 0

# SurprisedReaction -> Locomotion
m_TransitionDuration: 0.25
m_ExitTime: 0.9
m_HasExitTime: 1
```

进入 SurprisedReaction 时有 0.25s 的 cross-fade(和之前状态混合过渡),这个
混合窗口本身也会让人**短暂**看到两个状态的朝向叠在一起,但不足以解释你说的
"一播动画整个转到另一个方向"这种持续性的错位——持续性的错位更像是 root
motion 在每一帧都往一个固定方向加转动,不是一次性的过渡抖动。

另外发现这个 clip 的 guid 还被 `Assets/IVI/Controllers/BaseSFController.controller`
(注意,没有 "Normalized")里的一个 BlendTree 引用了(用 `Forward` 参数做混合,
和 SurprisedReaction 这套 Any-State 机制完全无关,像是旧的/实验性的用法)。
已经用 guid 反查过,**这份 `BaseSFController.controller` 没有被任何 prefab/scene
引用**(只有它自己的 `.meta` 命中,场景里实际在用的都是
`BaseSFControllerNormalized.controller`)。这条信息留给 §4 判断改导入设置的
副作用范围。

---

## 2. Surprised.fbx 的 Root Transform Rotation / Bake Into Pose 设置

`Assets/IVI/Animations/Interactions Pack/Surprised.fbx.meta` 里这个 clip
(`name: Surprised`, `takeName: mixamo.com`)的相关字段:

```yaml
clipAnimations:
- name: Surprised
  takeName: mixamo.com
  ...
  loopBlendOrientation: 0
  loopBlendPositionY: 0
  loopBlendPositionXZ: 0
  keepOriginalOrientation: 0
  keepOriginalPositionY: 1
  keepOriginalPositionXZ: 0
  heightFromFeet: 0
  ...
  animationImportWarnings: "\nClip 'mixamo.com' has import animation warnings that
    might lower retargeting quality:\n...'mixamorig:Spine2' is inbetween humanoid
    transforms and has rotation animation that will be discarded.\n"
```

**说实话的部分:** `keepOriginalOrientation` 这个字段在 Inspector 里对应的是
"Root Transform Rotation" 那一栏的 "Bake Into Pose" 复选框 + "Based Upon"
下拉框,这两个 UI 控件序列化成这一个 bool 的具体映射方向(勾选=0 还是
勾选=1),光读 YAML 文本我没法 100% 打包票——这个需要你在 Unity Editor 里
选中这个 fbx → Animation 标签页 → 展开 `Surprised` 这个 clip → 看
"Root Transform Rotation" 那一行 "Bake Into Pose" 是否打勾,30 秒能看到,
比我在这猜准。**这一点我不装懂,明确留给你肉眼确认。**

**但不需要靠这个字段也能给出结论,因为有更硬的证据:**

`animationImportWarnings` 直接说明,这份原始 mocap 数据(`mixamo.com` 这条
take)**本身就带着明显的躯干旋转**——警告说 `mixamorig:Spine2` 的旋转动画
因为是"人形骨骼中间层级"被丢弃了(humanoid 重定向只映射标准骨骼,中间层的
额外旋转数据保留不住,所以被扔了并给出警告)。这说明原始动画数据里,
不只是四肢在动,连脊柱这种接近根部的骨骼都有旋转变化——一个典型的"惊讶
后仰/转身"mocap 动作,躯干旋转贯穿整条动画,而不是原地不动只有手臂/头部动。
`transformMask` 里 `mixamorig:Hips` 权重是 1(未被裁剪掉,完整参与动画),
Hips 正是 Unity Humanoid 用来算 root motion 的关键骨骼。

综合来看:**这条 clip 的源数据本身包含真实的躯干/根部旋转,如果 Bake Into
Pose 这个开关没勾对,这部分旋转就会以 root motion 的形式被应用到
transform 上,跟脚本设的朝向对着干**——这和你实测看到的"红线指对、
一播动画身体就转别的方向"完全吻合。

---

## 3. 时序确认:root motion 会不会覆盖 Move() 设的朝向

`Base.cs` 里 `applyRootMotion` 默认 `true`(L32),在 `Start()` 里赋给
Animator(L64):`animator.applyRootMotion = applyRootMotion;`。

Unity 官方"Order of Execution for Event Functions"里,一帧内的顺序是:

```
FixedUpdate (物理)
  ↓
Update (所有脚本的 Update,包括 Base.Update() → Move() → RotateAround)
  ↓
Animation 系统求值 + 应用 root motion  ← 就在这一步把 transform 转/挪
  ↓
LateUpdate
  ↓
渲染
```

这是 Unity 明确写在文档里的、不含糊的顺序:**Animation(含 root motion 应用)
排在所有脚本 Update() 之后、LateUpdate() 之前。** 也就是说:

1. `Base.Update()` 先跑,`Move()` 里的 `transform.RotateAround(...)` 把这一帧
   该转的角度转好、设到 `transform.rotation` 上——这一步本身是对的(§1-§3
   在 FACING_BUG_DIAGNOSIS.md 里已经验证过,公式没问题)。
2. 但紧接着,**同一帧内**,Animator 才真正求值当前状态
   (`SurprisedReaction`)并把这个 clip 当前采样时刻的 root motion delta
   apply 到同一个 transform 上——如果这个 delta 里带着旋转分量(第 2 节的证据
   支持这一点),它会在 `RotateAround` 之后**追加**一个旋转,把 Move()
   刚设好的朝向又带偏。

**结论:时序上完全支持"root motion 覆盖脚本朝向"这个解释,是确定性的
Unity 行为,不是猜测。** 每一帧都是"脚本先转对,动画系统紧跟着再转歪",
所以视觉上看到的就是"红线一开始指对,动画一播身体转到别处"——跟你的实测
现象完全对得上。

---

## 4. 修法推荐

### a. 改 Surprised.fbx 导入设置:Root Transform Rotation → Bake Into Pose 勾选(推荐,优先做)

在 Unity 里选中 `Assets/IVI/Animations/Interactions Pack/Surprised.fbx` →
Animation 标签页 → 展开 `Surprised` clip → "Root Transform Rotation" 勾上
"Bake Into Pose" → Apply(触发重新导入)。

**为什么优先选这个:**
- 直接修在问题真正出处——这条 clip 的躯干旋转本来就不该被导出成 root
  motion,让它去跟角色控制器的朝向逻辑打架;正确做法本来就是把这类
  "由脚本/AI 控制朝向,动画只提供肢体表演"的 reaction 动画统一勾上
  Bake Into Pose,这是 Mecanim 处理这类素材的标准方式,不是权宜之计。
- 不动代码,`Base.cs`/`PedestrianModulator.cs` 保持现在这版就是对的,
  不需要再引入"什么时候该关 root motion"这类新的状态管理。
- 副作用范围可控:已经用 guid 反查过,这个 clip 还被另一个
  `BaseSFController.controller`(注意没有 "Normalized")里的一个旧
  BlendTree 引用,但那个 Controller **没有被任何 prefab/scene 实际引用**
  (孤儿资产),改这个 clip 的导入设置不会影响任何正在跑的东西。
- 唯一要注意:Bake Into Pose 只处理旋转,不动位移(`keepOriginalPositionY`/
  `keepOriginalPositionXZ` 是位移的独立开关,这次不用碰),如果之后发现
  这个动画播放时角色还有不自然的位移漂移,那是另一个独立的开关,不在这次
  排查范围内,届时再单独看。

### b. Base.cs 里冻结时临时关 applyRootMotion(可选、更省事但是治标)

在 `Move()` 里已经算出的 `hasFacingOverride`(即 `modulator.TryGetFacingOverride(...)`
的返回值)基础上顺手加一行:

```csharp
if (modulator != null && modulator.TryGetFacingOverride(out Vector3 overrideDir))
{
    goalDir = overrideDir;
    animator.applyRootMotion = false;   // 面朝覆盖生效时,不让 root motion 抢朝向
}
else
{
    animator.applyRootMotion = true;
    ...
}
```

**什么时候选这个,而不是 a:**
- 如果暂时没法/不方便重新导入 fbx(比如美术资源那边在改,或者想先跑起来看效果),
  这是一个不用碰资产、立刻生效的兜底。
- 额外好处是"防御性"更强:以后要是又加了别的 personality 状态、又挂了别的
  没调好 Bake Into Pose 的动画 clip,这行代码能兜住同一类问题,不用每次
  出问题都去挨个检查动画导入设置。

**为什么不作为首选:**
- 这是治标不治本——真正该修的是这条 clip 的导入设置错了,代码层面绕开只是
  掩盖了资产配置问题,以后新人接手这条 clip 或者复用到别的地方,同样的 bug
  还会犯一次,而且这次可能没人再记得代码里悄悄关了 applyRootMotion。
- `applyRootMotion = false` 是整体开关,连位移一起关了,如果这条动画其实
  还带了美术想要的"轻微后仰/踉跄"位移(目前没法确认,因为
  `keepOriginalPositionY/XZ` 这两个位移相关开关我们没细查),这个办法会
  把这部分位移表现也一起吃掉;而方案 a 可以只精确关旋转,位移不受影响。
- `Move()` 现在是 Base.cs 里所有 agent 类型共用的方法,这里加一行只在
  `modulator != null` 分支里生效,风险可控,但每多一处"因为某个 personality
  的需要而在通用代码里加条件分支"的口子,以后攒多了会让 Move() 越来越难读。

### 结论

先做 a(改 Surprised.fbx 的 Bake Into Pose),这是本质修复,而且已确认没有
其他活跃引用会受影响。如果暂时没法碰资产,再退而求其次上 b 应急,但记得
在 a 修完之后把 b 这行代码撤掉,别让两层修复叠在一起、以后没人知道为什么
这里还留着一个 applyRootMotion 的开关。
