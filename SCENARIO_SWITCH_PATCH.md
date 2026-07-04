# "Play 只演一种 Scenario + Inspector 下拉切换" 代码补丁（待审阅，未落地）

> 只读查证 + 补丁草案。**本文档不是已应用的改动**——`.cs` 文件未被修改，补丁只以代码块形式写在下面，供 review 后再决定是否落地。
> 承接 `SEAN_architecture_analysis.md` 与 `PEDESTRIAN_SCENARIO_DESIGN.md`；本次用实际文件核对了之前设计文档里的假设，并把假设更新为已验证的事实。
> 查证日期：2026-07-01

---

## 第一部分：查证结果（用文件核对，非假设）

### 1.1 `Assets/Resources/SEAN/PedestrianBehaviors.prefab` 的真实子物体

直接解析该 prefab 的 YAML，拿到 `PedestrianBehaviors` 根节点（`fileID 1504409747876047988`，其 `Transform` 为 `fileID 1504409747876047991`）的 **6 个直接子物体**（即 `/SEAN/PedestrianBehaviors.transform` 下能被 `pedestrianBehaviors` / `SetPedestrianBehavior()` 遍历到的那一层，不含更深层嵌套的 `PointA-E`/`Positions` 等 spawn 标记点）：

| 真实 GameObject 名（精确大小写） | `m_IsActive` |
|---|---|
| `None` | **1** |
| `GraphNav` | **1** |
| `HandcraftedSocialSituation` | 0 |
| `LabStudy` | 0 |
| `Playback` | 0 |
| `Random` | 0 |

**与 `PEDESTRIAN_SCENARIO_DESIGN.md` 的假设对比**：设计文档假设的 6 个名字——`GraphNav`/`Random`/`HandcraftedSocialSituation`/`Playback`/`LabStudy`/`None`——**逐字核对全部命中，大小写、拼写完全一致，无需修正**。`GraphNav` + `None` 同时 `m_IsActive: 1` 这个此前的推断，也被直接从 prefab 源文件解析确认，不是猜测。

### 1.2 `Assets/Scenes/SEAN/Lab.unity` 是否对 `PedestrianBehaviors` 做了 override

查了两层：

1. **`/Environment/PedestrianControl` 子树**（环境数据层，不是选择器）：Lab.unity 里 `PedestrianControl`（`fileID 25720062`，非 prefab 实例）下有且仅有 2 个直接子物体，均为**嵌套 prefab 实例**：
   - `Assets/IVI/Prefabs/LabGraph.prefab` 的实例，改名为 `"Graph"`（`m_Modifications` 里 `propertyPath: m_Name, value: Graph`）
   - `Assets/Resources/SEAN/PedestrianBehaviors/Random.prefab` 的实例，改名为 `"Random"`
   - 两处 `m_Modifications` 列表里**都没有任何 `propertyPath` 涉及 `m_IsActive`**，即 Lab 场景没有覆盖这两个子物体的激活状态，各自沿用其源 prefab 里保存的 `m_IsActive`（这两个 prefab 各自的 `m_IsActive` 本次未展开解析，**需确认**，但与本次改动方案关系不大，因为它们是"环境数据"而非"selector"）。

2. **`/SEAN/PedestrianBehaviors` 选择器层**（本次改动真正关心的层）：确认 `SEAN.prefab` 整体在 Lab.unity（以及 Outdoor.unity、Warehouse.unity）里各被实例化一次（用 `SEAN.prefab.meta` 的 guid `0303bdb201b5e44c5ad3c7bf43ff670e` 在三个场景文件里分别搜到 34/56/37 处引用，确认三场景均含 `SEAN.prefab` 实例）。**用 `PedestrianBehaviors.prefab` 内 `GraphNav`（fileID `2323335051687940950`）和 `None`（fileID `2707643064931472505`）的 fileID 去搜三个场景文件的全文，均为 0 命中**——说明**三个场景没有任何一个对这两个子物体的 `m_IsActive` 做了场景级 override**。

**结论**：`GraphNav` + `None` 同时激活**不是某个场景特有的问题，而是 `PedestrianBehaviors.prefab` 这一份共享资源本身的状态**，Lab/Outdoor/Warehouse 三个场景运行时看到的都是同一份（未被覆盖的）激活组合。此前设计文档里"Lab/Warehouse 需确认"的疑问，在"selector 层是否被 override"这一点上，现在已经查清：**没有 override，问题在 prefab 本身，三场景一致**。

### 1.3 `SEAN.cs` 当前（合并 Howard 之后）实际内容确认

`git log`：Howard 合并进来的提交是 `42b6662 fix unitree model`（已确认现在本地 `main` 分支 HEAD 就是它，`ahead 1` of `origin/main`）。用 `git show 42b6662 --stat | grep -i SEAN.cs` 核实——**无输出，即 Howard 的提交完全没有改动 `SEAN.cs`**。`SEAN.cs` 最近一次被改动的提交是更早的 `2fd07ce publish people velocities and implement a new task topic`。`SeanEditor.cs` 同样未被 Howard 的提交触碰。

现读取 `Assets/Scripts/SEAN/SEAN.cs` 全文（483 行），确认以下几点：

- **`[ExecuteAlways]`**：类声明上确实有（`SEAN.cs:16`），确认无误。
- **`Awake()`**（`SEAN.cs:102-176`）：存在，内容为——单例赋值 → 找 `/Environment` → 遍历 `_SEAN.transform` 子物体按名字缓存 `_PedestrianBehaviors`/`_RobotTasks`/`_Robots`/`_Players`/`_Controllers`/`_input`/`_metrics`/`_StartAndGoal` 引用 → 处理 `player` 激活 → 调用 `ParseCommandLineArgs()` → 非 Editor 下设置 ROS 端口。**没有 `Start()` 方法**（`SEAN` 类本身不需要 `Start()`，所有真正的初始化都在 `Awake()` 里完成）。
- **`SetPedestrianBehavior(string name)`**（`SEAN.cs:197-217`）准确签名与实现：

  ```csharp
  public void SetPedestrianBehavior(string name)
  {
      bool found = false;
      foreach (Scenario.PedestrianBehavior.Base behavior in pedestrianBehaviors)
      {
          if (behavior.name == name)
          {
              _pedestrianBehavior = behavior;
              behavior.gameObject.SetActive(true);
              found = true;
          }
          else
          {
              behavior.gameObject.SetActive(false);
          }
      }
      if (!found)
      {
          throw new ArgumentException("Could not find scenario with name " + name + ", valid options are " + (from s in pedestrianBehaviors select s.name));
      }
  }
  ```

  确认：找不到匹配名字时是 **`throw new ArgumentException`**，不是静默失败、也不是 `Debug.LogError`——这意味着如果本次新增字段传入一个不存在的名字，`Awake()` 会直接抛异常导致场景初始化失败（这是任务里要求"加一个防御：Debug.LogError 提示而非静默失败"的原因——现状连"静默失败"都不是，是**直接抛异常崩掉**，比静默失败更严重，所以防御性检查更有必要）。
- **没有任何已存在的 scenario 相关 `public` 字段**——通读全文 `public` 字段/属性列表：`AgentController`、`ControlledAgent`、`TopDownViewOnly`、`PlayerControl`、`EvaluationMode`、`RosConnectionPort`，以及一堆只读 getter 属性（`environment`/`input`/`metrics`/`pedestrianBehaviors`/`pedestrianBehavior`/`robotTasks`/`robotTask`/`taskSocialSituation`/`robot`/`player`/`controller`）。**没有任何字段是"要激活哪个 scenario"这种可配置选项**，新增不会与现有字段重名/冲突。

---

## 第二部分：补丁草案（写在这里，未落地到 `.cs`）

改动只涉及 **`Assets/Scripts/SEAN/SEAN.cs`** 一个文件，`SeanEditor.cs` **不改动**（原因见 §2.4）。

### 2.1 新增 enum + 字段

**位置**：紧跟在 `SEAN.cs:28`（`public Scenario.Agents.ControlledAgent ControlledAgent;`）之后插入，属于同一组"运行时可配置的顶层选项"。

```diff
         public Scenario.Agents.LowLevelControl AgentController;
         public Scenario.Agents.ControlledAgent ControlledAgent;
 
+        // 与 /SEAN/PedestrianBehaviors 下现有子物体名字一一对应（已用
+        // Assets/Resources/SEAN/PedestrianBehaviors.prefab 逐字核对，2026-07-01）。
+        // 新增/改名 PedestrianBehavior 子类时需要同步维护这个枚举。
+        public enum ScenarioSelection
+        {
+            GraphNav,
+            Random,
+            HandcraftedSocialSituation,
+            Playback,
+            LabStudy,
+            None,
+        }
+
+        [Tooltip("Play 开始时自动激活的 pedestrian scenario；同一时刻 /SEAN/PedestrianBehaviors "
+               + "下其余 scenario 会被强制关闭。命令行 -scenario 参数（如果传了）优先级更高，"
+               + "会在这之后覆盖这里的选择。")]
+        public ScenarioSelection selectedScenario = ScenarioSelection.None;
+
         public bool TopDownViewOnly = false;
         public bool PlayerControl = false;
         public bool EvaluationMode = false;
```

- 枚举成员名与 §1.1 表格核对过的真实 GameObject 名**逐字一致**（含大小写），`ScenarioSelection.HandcraftedSocialSituation.ToString()` == `"HandcraftedSocialSituation"`，与 prefab 里的真实名字完全匹配，不需要额外映射表。
- 默认值选 `None`（枚举第一个值通常是 Unity 序列化的隐式默认，但这里显式写出 `= ScenarioSelection.None` 更清楚），对应"不强制指定，保持空场景"的最安全默认——避免所有历史场景/prefab 实例在字段刚加上去、还没人手动配置时，被一次性强制切到某个非空 scenario。

### 2.2 `Awake()` 里自动调用一次，实现"Play 只激活一种"

**位置**：`SEAN.cs:158` 和 `SEAN.cs:160`（`foreach` 遍历子物体结束之后，`if (ControlledAgent == ...)` 的 player 激活逻辑**之前**——此时 `_PedestrianBehaviors` 刚被赋值完毕，且仍处于所有子物体自己的 `Start()` 尚未运行的 `Awake()` 阶段）：

```diff
                 else if (child.name == "StartAndGoal")
                 {
                     _StartAndGoal = child.gameObject;
                 }
             }
 
+            // Play 模式下，Awake() 早于所有 PedestrianBehavior 子类自己的 Start()
+            // （Unity 保证同一次场景加载里所有激活对象的 Awake() 先跑完，才会开始跑
+            // 任意一个的 Start()）。在这里强制收敛成 1 个 active scenario，可以在任何
+            // 一个多余 scenario 的 spawn 逻辑被触发之前就把它关掉，从根源上避免
+            // "More than 1 Scenario is active" warning 以及随之而来的
+            // BaseAgentManager 单例竞争 / 强制类型转换崩溃风险。
+            // 用 Application.isPlaying 保护：SEAN 类是 [ExecuteAlways]，编辑模式下的
+            // Awake()（比如脚本重编译触发）不应该打断美术/策划手动摆场景数据的工作流。
+            if (Application.isPlaying)
+            {
+                TryActivateSelectedScenario();
+            }
+
             if (ControlledAgent == Scenario.Agents.ControlledAgent.Player)
             {
                 PlayerControl = true;
                 player.gameObject.SetActive(true);
             }
```

`ParseCommandLineArgs()` 调用（`SEAN.cs:171`）保持原位不动——它在这次新增调用**之后**执行，如果 `-scenario` 命令行参数出现，会再调一次 `SetPedestrianBehavior()`，结果覆盖 `selectedScenario` 字段的选择，与现状"命令行优先级最高"的行为完全一致，不需要额外处理优先级逻辑。

### 2.3 防御：找不到对应名字时 `Debug.LogError` 而非让异常直接炸穿 `Awake()`

新增一个私有辅助方法（放在 `SetPedestrianBehavior` 方法之后即可，`SEAN.cs:217` 后）：

```diff
             if (!found)
             {
                 throw new ArgumentException("Could not find scenario with name " + name + ", valid options are " + (from s in pedestrianBehaviors select s.name));
             }
         }
 
+        // 包一层 try/catch：selectedScenario 是 Inspector 里手选的枚举，理论上和
+        // /SEAN/PedestrianBehaviors 下的真实子物体名字不会对不上（枚举值就是照抄
+        // 真实名字定义的，见 §2.1），但如果未来有人往 PedestrianBehaviors.prefab
+        // 里删/改了某个子物体名字却忘了同步枚举，这里用 LogError 提示、并保留场景
+        // 原有的 active 状态（不 SetActive 任何东西），而不是让 SetPedestrianBehavior()
+        // 内部的 ArgumentException 直接把整个 SEAN.Awake() 炸穿、导致整个场景初始化失败。
+        private void TryActivateSelectedScenario()
+        {
+            try
+            {
+                SetPedestrianBehavior(selectedScenario.ToString());
+            }
+            catch (ArgumentException e)
+            {
+                Debug.LogError("SEAN.selectedScenario is set to '" + selectedScenario
+                    + "' but no matching child was found under /SEAN/PedestrianBehaviors. "
+                    + "Scene's existing scenario activation state is left untouched. "
+                    + e.Message);
+            }
+        }
+
         public Scenario.PedestrianBehavior.Base pedestrianBehavior
```

- 之所以用 `try/catch` 包一层而不是在 `TryActivateSelectedScenario()` 里重新实现一遍"查找+比较名字"的逻辑，是为了**完全复用** `SetPedestrianBehavior()` 已经验证过的互斥激活代码（§1.3 已确认其实现正确），避免出现两份逻辑不一致的风险。
- 需要留意：`SetPedestrianBehavior()` 在抛异常**之前**，`foreach` 循环已经把所有不匹配的 scenario `SetActive(false)` 过一遍了（只有匹配的分支没走到）——也就是说，即使抛出异常被这里 catch 住，**已经执行的 `SetActive(false)` 副作用不会被回滚**。实际后果：如果 `selectedScenario` 配置错误（比如手滑加了个不存在的枚举值或者 prefab 被改了忘记同步），场景会变成"所有 scenario 都被关掉、一个都没激活"，而不是"保留原有状态"——上面注释里"Scene's existing scenario activation state is left untouched"这句需要根据实际验证结果调整措辞，或者更彻底的做法是在 `TryActivateSelectedScenario()` 里自己先查一遍 `pedestrianBehaviors` 列表确认名字存在、确认存在才调用 `SetPedestrianBehavior()`，完全不触发那个会有副作用的 `foreach`。**这一点建议在真正落地前二选一并明确记录在 commit message 里**（见下方"落地前必须确认"）。

  更保险的写法（**建议采用这版**，避免上面提到的副作用问题）：

  ```csharp
  private void TryActivateSelectedScenario()
  {
      string targetName = selectedScenario.ToString();
      bool exists = pedestrianBehaviors.Any(b => b.name == targetName);
      if (!exists)
      {
          Debug.LogError("SEAN.selectedScenario is set to '" + targetName
              + "' but no matching child was found under /SEAN/PedestrianBehaviors. "
              + "Scenario activation left untouched; valid options are "
              + string.Join(", ", pedestrianBehaviors.Select(b => b.name)));
          return;
      }
      SetPedestrianBehavior(targetName);
  }
  ```

  这版先只读检查（`pedestrianBehaviors` 属性本身是只读遍历，不产生 `SetActive` 副作用），确认存在了再调用 `SetPedestrianBehavior()`，找不到时**真正做到"不动现场"**，比 try/catch 版本更符合"防御性检查"的本意。需要在文件顶部确认已有 `using System.Linq;`（`SEAN.cs:11` 已经有，`.Any()`/`.Select()` 可以直接用，不需要新增 using）。

### 2.4 `SeanEditor.cs` 现有下拉框：保留，不改动

**核实**：`Assets/Scripts/SEAN/Editor/SeanEditor.cs` 里确实已经有一个自定义 Inspector 下拉框（`SeanEditor.cs:34-41`），Play 模式下选中 `SEAN` 这个 GameObject 时可以手动切换：

```csharp
int selectedScenarioIndex;
List<string> scenarios;
script.UIGetPedestrianBehaviors(out scenarios, out selectedScenarioIndex);
int scenarioResult = EditorGUILayout.Popup("Pedestrian Control", selectedScenarioIndex, scenarios.ToArray());
if (selectedScenarioIndex != scenarioResult) {
    script.SetPedestrianBehavior(scenarios[scenarioResult]);
}
```

**本次补丁不改这个文件**，两者分工：

| | `selectedScenario` 字段（本次新增） | `SeanEditor.cs` 现有 Popup |
|---|---|---|
| 生效时机 | `Awake()`，Play **刚开始**就自动生效 | 只有用户在 Inspector 里**手动点选**下拉框选项时才生效 |
| 是否需要人在场 | 不需要，构建版本/命令行批量评测跑也生效 | 需要，纯 Editor-only GUI 交互，构建版本里没有这个界面 |
| 序列化保存 | 是普通字段，随场景/prefab 实例保存，重开 Unity 还在 | 不是持久状态，只是每次 GUI 重绘时读一次当前 active 状态 |
| 用途 | 决定"这个场景 Play 一开始该用哪个" | Play **过程中**不重启就临时切换看效果、调试用 |

两者都是通过同一个 `SetPedestrianBehavior()` 起作用，不存在互斥冲突——`selectedScenario` 决定初始状态，之后随时可以用现有 Popup 手动切换到别的 scenario（切换后不会有东西再把它切回来，因为 `Awake()` 只在场景加载时跑一次）。

---

## 3. 风险与落地前必须确认的点

- **是否与 Howard 的改动冲突**：**否**。已用 `git show 42b6662 --stat` 核实，Howard 唯一合并进来的提交 `fix unitree model` 完全没有涉及 `SEAN.cs` 或 `SeanEditor.cs`（改动的是 Unitree A1 相关的 `VelocityController.cs`/`A1PlaybackController.cs`/`Robot.cs`/`Base.cs`(Tasks)/`Metrics.cs` 等，与本次改动的文件无交集）。本次补丁落地不会与 Howard 的修复产生 merge 冲突或行为交叉。
- **§2.3 两个版本选哪个**：建议采用"先只读检查存在性、再调用 `SetPedestrianBehavior()`"的版本（`TryActivateSelectedScenario()` 第二版），语义更干净（失败时保证不改变现场），比 try/catch 版本更贴合"防御性检查"的设计初衷。落地时请二选一确认。
- **`selectedScenario` 默认值 `None` 的场景初始化行为**：`None.cs` 本身没有任何 spawn 逻辑（`groups`/`agents` 恒返回空数组），所以如果某个历史场景加了这个字段后没人去配置它（保持默认 `None`），Play 出来会是"没有任何行人"的空场景——这是一个**行为变化**：现状（未打补丁前）是"不受控制地依赖 prefab 里恰好激活的东西"（当前是 `GraphNav`，因为遍历顺序里它在 `None` 后面被最后赋值，**这一点此前"需确认"，本次也未继续深挖具体遍历顺序、只确认了两者都是 active**），打了补丁、如果不主动去把某场景的 `selectedScenario` 从默认 `None` 改成 `GraphNav`，会让本来"能看到行人"的场景突然"看不到行人"了。**落地时必须逐场景（Lab/Outdoor/Warehouse）手动检查并设置 `selectedScenario` 的 Inspector 值**，不能只依赖代码默认值。
- **prefab 本身的 `GraphNav`+`None` 双激活是否也要顺手修掉**：本次补丁是在**代码层面**（`Awake()`）做强制收敛，不依赖修 prefab 也能保证"Play 时只有一个生效"；但 prefab 里"两个子物体同时 `m_IsActive: 1`"这个作者遗留状态本身建议还是清理掉（比如都改回一个 active、一个不 active），否则 Editor 里非 Play 模式下用户手动查看该 prefab 层级视图时，仍然会看到"两个都勾着"的困惑状态，只是不再影响 Play 时的实际行为。这个 prefab 清理**是否要一并做，还是只做代码层面的补丁**，需要与你确认范围。
- **本文档给出的所有 diff 都还没有写入 `.cs` 文件**，仅供 review；review 通过后需要我再单独执行落地（新建/修改 `Assets/Scripts/SEAN/SEAN.cs`）。
