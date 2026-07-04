# SEAN PedestrianBehavior 场景级选择机制分析 + "单场景单 Scenario + Inspector 可切换"设计

> 只读分析产出，**未修改工程任何代码文件**。
> 分析日期：2026-07-01
> 配套参考：`SEAN_architecture_analysis.md`（同目录，§3 已覆盖行人系统整体两层架构，本文档在此基础上做场景级选择机制的专项深挖 + 改造设计）
> 全文分两部分：**第 1 章「现状分析」**（客观代码事实）与 **第 2 章「改造建议」**（我的设计方案，未落地）。不确定处标注"需确认"，未做的验证不编造结论。

---

## 目录

1. [现状分析](#1-现状分析)
   1. [PedestrianBehavior 全貌](#11-pedestrianbehavior-全貌)
   2. ["More than 1 Scenario is active" 警告溯源](#12-more-than-1-scenario-is-active-警告溯源)
   3. [`/Environment/PedestrianControl` 子树组织](#13-environmentpedestriancontrol-子树组织)
   4. [`SEAN.cs` 的 `SetPedestrianBehavior()` 逻辑](#14-seancs-的-setpedestrianbehavior-逻辑)
   5. [`-scenario` 命令行参数如何工作](#15--scenario-命令行参数如何工作)
   6. [`SocialSituation` 枚举](#16-socialsituation-枚举)
2. [改造建议（未落地，供讨论）](#2-改造建议未落地供讨论)
   1. ["Play 只演一种"：最小改动方案](#21-play-只演一种最小改动方案)
   2. [Inspector 下拉切换设计](#22-inspector-下拉切换设计)
   3. [为个性化（personality / belief-state）预留接口](#23-为个性化-personality--belief-state-预留接口)
3. [风险与需确认清单](#3-风险与需确认清单)

---

## 1. 现状分析

### 1.1 PedestrianBehavior 全貌

`SEAN.Scenario.PedestrianBehavior.Base`（`Assets/Scripts/SEAN/Scenario/PedestrianBehavior/Base.cs`）是**场景级"演哪种社交情况"的选择器基类**，与个体级 `Agents.Base.UpdateVelocity()`（走路的力学模型，实际只有 `IVI.SFAgent` 生效）是完全独立的两层——这一点 `SEAN_architecture_analysis.md` §3.1 已说明，此处不重复。

`Base.cs` 本身很薄：

```csharp
public abstract class Base : MonoBehaviour
{
    protected GameObject pedestrianControl;   // = GameObject.Find("/Environment/PedestrianControl")
    protected void Start() { pedestrianControl = GameObject.Find("/Environment/PedestrianControl"); }
    public virtual string scenario_name { get { return _name; } }
    public abstract Trajectory.TrackedGroup[] groups { get; }
    public abstract Trajectory.TrackedAgent[] agents { get; }
}
```

注意 `Start()` 是 `protected` 且**非 `virtual`**，子类各自定义同名 `public void Start()` 方法隐藏（而非 `override`）它，并在开头手动调用 `base.Start()`。这在 Unity 的消息分发（基于反射查找具体类型上的 `Start`，非虚方法多态）下没有问题，但意味着**每个子类的 `Start()` 都是独立入口**，Unity 只要发现该 GameObject `active` 就会调用——这是第 1.2 节问题的关键前提。

现有 **6 个子类**，对应的社交情况：

| 子类（文件） | GameObject 名<sup>†</sup> | `scenario_name` | 委托的 Manager | 代表的社交情况 |
|---|---|---|---|---|
| `GraphNav` | `GraphNav` | `"Graph_" + _name` | `IVI.NavManager` | 场景内预设导航图（节点+边），行人按占用均衡 + Dijkstra 在图上持续游走，群组节点处会"停留聊天"——模拟**持续存在的人流/群组分布**的常态环境 |
| `Random` | `Random` | `"Random"` | `Agents.RandomABNavAgentManager` | 纯随机 A→B 游走（NavMesh 随机点），到达后立刻换下一个随机目标——**无结构的背景人群噪声** |
| `Handcrafted` | `HandcraftedSocialSituation` | `"Handcrafted_" + current`（`current` 是当前 `SocialSituation`） | `Agents.Handcrafted` | 4 种**预设的具体社交情境**，见 §1.6：`JoinGroup`/`LeaveGroup`/`DownPath`/`CrossPath` |
| `Playback` | `Playback` | `"Playback"` | `Agents.Playback.LoadAllAvatar` | 回放真实数据集采集的行人轨迹 CSV（非仿真生成）——**真实数据复现** |
| `LabStudy` | `LabStudy` | `"LabStudy"` | 无（自身管理 `positions` 字典） | 用户研究专用固定路线场景，`agents` 恒为单个 `TrackedAgent` |
| `None` | `None` | （基类默认，通常为空） | 无 | **空场景**：`groups`/`agents` 恒返回空数组，不生成任何行人 |

†：这里的"GameObject 名"专指 `/SEAN/PedestrianBehaviors` 下对应子物体的 `m_Name`（即 `behavior.name`，因为 `PedestrianBehavior.Base` 是 `MonoBehaviour`，`.name` 属性等价于其所在 GameObject 的名字）。**这个名字与 `scenario_name` 属性是两回事**——`scenario_name` 是运行时展示/上报用的字符串（如 `"Graph_Outdoor"`），而 `SEAN.SetPedestrianBehavior(name)`／`-scenario` 参数比较的是 GameObject 名字，见 §1.4-1.5。这一区分在改造时容易踩坑。

### 1.2 "More than 1 Scenario is active" 警告溯源

- **文件位置**：`Assets/Scripts/SEAN/SEAN.cs:238`（`pedestrianBehavior` 属性的 getter 内）：

```csharp
public Scenario.PedestrianBehavior.Base pedestrianBehavior
{
    get
    {
        if (_pedestrianBehavior != null) { return _pedestrianBehavior; }
        int activeCount = 0;
        foreach (Scenario.PedestrianBehavior.Base scenario in pedestrianBehaviors)
        {
            if (scenario.gameObject.activeSelf)
            {
                _pedestrianBehavior = scenario;
                activeCount++;
            }
        }
        if (activeCount != 1)
        {
            Debug.LogWarning("More than 1 Scenario is active, using: " + _pedestrianBehavior.name);
        }
        if (activeCount == 0) { Debug.LogWarning("No Scenario is active"); }
        return _pedestrianBehavior;
    }
}
```

这个 getter 只是**事后检测并报警**——它不会主动去关掉多余的 scenario，只是遍历 `/SEAN/PedestrianBehaviors` 下所有子物体，数一下有几个 `activeSelf == true`，如果不是 1 个就报警，并把**最后一个被遍历到、且 active 的**赋给 `_pedestrianBehavior`（因为循环体内没有 `break`，每找到一个 active 的都会覆盖赋值）。**报警发生的时间点已经晚了**：在这之前，凡是 active 的 `PedestrianBehavior.Base` 子类，其自己的 `Start()` 早就已经运行过、已经把各自的 spawn 逻辑触发出去了。

**根因（已用脚本直接解析 prefab YAML 验证）**：`Assets/Resources/SEAN/PedestrianBehaviors.prefab`（即 `/SEAN/PedestrianBehaviors` 的源 prefab）里，**`GraphNav` 和 `None` 这两个子物体的 `m_IsActive` 同时为 `1`**（其余 `Playback`/`Random`/`LabStudy`/`HandcraftedSocialSituation` 均为 `0`）。这是一个**静态资源层面的作者遗留问题**——不是运行时逻辑 bug，是 prefab 本身在保存时就带着两个 active 状态。

```
Playback              -> active=0
Random                -> active=0
GraphNav              -> active=1   ← 
None                   -> active=1   ← 同时激活
LabStudy               -> active=0
HandcraftedSocialSituation -> active=0
```

（Lab.unity / Warehouse.unity 场景内 `/SEAN/PedestrianBehaviors` 是否有场景级 prefab overrides 覆盖了这个默认状态，**需确认**——受限于这两个场景内该节点是嵌套 prefab 实例，用静态 YAML 解析无法直接读出 override 后的最终 `m_IsActive`，需要在 Unity Editor 里直接查看该 GameObject 的 Inspector 勾选状态，或运行时打印 `pedestrianBehaviors` 列表核实。）

**这个警告具体触发的后果需要分情况看**（细节见下），并不总是等价于"两套完整的行人系统在跑"：

- `Agents.RandomABNavAgentManager`、`Agents.Handcrafted`、`IVI.NavManager` 都继承自共享单例基类 `Scenario.Agents.BaseAgentManager`（`Assets/Scripts/SEAN/Scenario/Agents/BaseAgentManager.cs`）：

  ```csharp
  protected virtual void Awake()
  {
      if (_instance != null && _instance != this) { Destroy(this.gameObject); }
      else { _instance = this; }
  }
  ```

  即**场景里只能有一个 `BaseAgentManager.instance` 存活**，谁的 `Awake()` 先跑谁留下，后来者的整个 GameObject 会被 `Destroy`。所以如果两个"有真实 Manager"的 scenario（例如 `GraphNav` 和 `Random`）同时 active，不会稳定地产生"两倍行人"，而是产生**依赖 Unity 内部 Awake 执行顺序、不确定哪个 Manager 存活**的行为——更糟的是，`GraphNav.Start()`/`Random.Start()` 里都有形如 `(Agents.RandomABNavAgentManager)Agents.BaseAgentManager.instance` 的**强制类型转换**，如果存活的单例类型和当前 scenario 期望的类型不一致，会直接抛 `InvalidCastException` 而不是"温和地"多生成一批行人。
- 当前实际触发的这次警告是 `GraphNav` + `None`：`None` 没有自己的 `Start()`（用的是基类那个几乎空的 `protected Start()`），不会额外 spawn 任何东西，所以这次具体报警**不会**导致双人群，只是让 `pedestrianBehavior` getter 的返回值依赖遍历顺序而不确定（`GraphNav` 或 `None` 谁被判定为"当前 scenario"取决于 `_PedestrianBehaviors.transform` 的子物体顺序，**未逐一验证具体顺序，需确认**）。
- 但这**不代表机制是安全的**——`None` 恰好无害只是巧合。任何时候只要有人不小心让 `Random` 或 `Handcrafted` 之类"有真实 spawn 逻辑"的 scenario 和 `GraphNav` 同时勾选 active，就会触发上面提到的单例竞争/类型转换崩溃或不确定行为。这正是用户想要结构性解决的问题。

### 1.3 `/Environment/PedestrianControl` 子树组织

`PedestrianBehavior.Base.Start()` 查找的是**每个场景自己的** `/Environment/PedestrianControl` GameObject（三个场景 `Lab.unity`/`Outdoor.unity`/`Warehouse.unity` 各自独立一份，装的是该场景专属的生成点/图节点数据，不是共享 prefab 的一部分——这点 `SEAN_architecture_analysis.md` §3.5 已提到）。各子类在其下按**固定名字**查找对应子树并 `SetActive(true)`：

| 子类 | 在 `/Environment/PedestrianControl` 下查找的子物体名 |
|---|---|
| `GraphNav` | `"Graph"` |
| `Random` | `"Random"` |
| `Handcrafted` | `"HandcraftedSocialSituations"` → 内部再找 `"Handcrafted"` |
| `Playback` | `"Playback"` |
| `LabStudy` | （自身子物体 `positions`，不查 `PedestrianControl`） |
| `None` | 无 |

**用脚本静态解析了三个场景文件**，能确认的实际状态：

- **`Outdoor.unity`**：`/Environment/PedestrianControl` 下三个直接子物体清晰可辨——`Graph`（`m_IsActive=1`）、`HandcraftedSocialSituations`（`m_IsActive=0`）、`Random`（`m_IsActive=0`）。即 **Outdoor 场景本身的环境数据子树层面只有一个是激活的**，与场景搭配的 scenario 应为 `GraphNav`，吻合。
- **`Lab.unity`**、**`Warehouse.unity`**：`PedestrianControl` 的子物体在这两个场景文件里是**嵌套 prefab 实例**（`m_Children` 引用的 fileID 在同一文件里找不到对应的 `GameObject`/`Transform` 文档），静态文本解析无法直接读出它们的名字和激活状态。**需确认**——需要在 Unity Editor 里展开这两个场景的该节点直接查看，或写一个 Editor 脚本用 `PrefabUtility` API 解析。

### 1.4 `SEAN.cs` 里 `SetPedestrianBehavior()` 如何选择/激活一个 scenario

（`Assets/Scripts/SEAN/SEAN.cs:197-217`）

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
        throw new ArgumentException("Could not find scenario with name " + name + ", valid options are " + ...);
    }
}
```

关键事实：**这个方法本身已经是一个完整、正确的"互斥选择器"**——遍历所有 scenario 子物体，命中的那个 `SetActive(true)`，其余全部 `SetActive(false)`。也就是说，**"只激活一个 scenario"所需的核心逻辑代码库里已经有了**，问题只在于：这个方法目前只在两个地方被调用（见下），**没有任何地方在 `Awake()` 阶段无条件地调用它来保证初始状态互斥**——如果场景/prefab 本身保存时就带着多个 active（如 §1.2 的 `GraphNav`+`None`），且没人传 `-scenario` 参数、也没人在 Editor 里点开自定义 Inspector 手动切换一次，这个互斥逻辑就永远不会被触发。

`SetPedestrianBehavior()` 目前的两个调用点：

1. `SEAN.Awake()` → `ParseCommandLineArgs()` → `-scenario <name>` 参数（见 §1.5）。
2. `Assets/Scripts/SEAN/Editor/SeanEditor.cs:38-41`——**已有的自定义 Inspector 下拉框**：

   ```csharp
   int selectedScenarioIndex;
   List<string> scenarios;
   script.UIGetPedestrianBehaviors(out scenarios, out selectedScenarioIndex);
   int scenarioResult = EditorGUILayout.Popup("Pedestrian Control", selectedScenarioIndex, scenarios.ToArray());
   if (selectedScenarioIndex != scenarioResult) {
       script.SetPedestrianBehavior(scenarios[scenarioResult]);
   }
   ```

   **这说明"Unity Inspector 里选择播放哪一种"这个 UI 交互已经存在**，只是：(a) 它是自定义 `Editor` 脚本画出来的 GUI 控件，不是一个被序列化保存的字段（关掉/重开 Inspector 或重新 Play 不会记住选择，需要用户在选中该 GameObject 时手动点一下下拉框才会生效）；(b) 它不会在 Play 开始时自动执行——只有当用户在 Play 模式下手动点选/切换下拉框选项时才会调用 `SetPedestrianBehavior()`。这正是第 2 章要补的缺口。

### 1.5 `-scenario` 命令行参数如何工作

`Assets/Scripts/SEAN/SEAN.cs:64-67`（`ParseCommandLineArgs()` 内）：

```csharp
else if (args[i] == "-scenario")
{
    SetPedestrianBehavior(value);
}
```

`ParseCommandLineArgs()` 在 `Awake()`（`SEAN.cs:171`）里被调用，晚于 `_PedestrianBehaviors` 引用被赋值（`Awake()` 内 `foreach (Transform child in _SEAN.transform)` 那段，`SEAN.cs:124-158`），早于任何子物体的 `Start()`——因为 Unity 保证"场景加载时所有激活对象的 `Awake()` 全部跑完，才会开始跑任何一个的 `Start()`"。**这正是为什么 `-scenario` 参数能够可靠地做到"只激活一个"**：`SetPedestrianBehavior()` 里对多余 scenario 的 `SetActive(false)` 发生在 `Awake()` 阶段，抢在它们自己的 `Start()`（真正触发 spawn 的地方）之前。

`-scenario <name>` 的 `<name>` 必须精确匹配 `/SEAN/PedestrianBehaviors` 下某个子物体的 **GameObject 名字**（不是 `scenario_name`！见 §1.1 脚注），当前合法取值为：`GraphNav`、`Random`、`HandcraftedSocialSituation`、`Playback`、`LabStudy`、`None`。传错名字会直接 `throw new ArgumentException`（`SEAN.cs:213-216`），会打印出所有合法选项。

### 1.6 `SocialSituation` 枚举

定义在 `Assets/Scripts/SEAN/Scenario/PedestrianBehavior/Base.cs:11-18`：

```csharp
public enum SocialSituation
{
    Empty,
    JoinGroup,
    LeaveGroup,
    DownPath,
    CrossPath,
}
```

**只在 `Handcrafted` 这一条链路里有意义**（`GraphNav`/`Random` 不涉及这个枚举）。语义（从 `Agents/Handcrafted.cs` 的 `NewScenario()` 实现推断）：

| 枚举值 | 代表的社交场景 |
|---|---|
| `Empty` | 空场景/默认值，不特指某个具体情境（`Handcrafted.current` 的初始值） |
| `JoinGroup` | 机器人/行人从场景外走向一个已有群组，加入群组的一个空位（目标点 = `agentManager.openGroupLocation`，若存在的话） |
| `LeaveGroup` | 反过来：从某个群组内部的位置出发，离开群组走向场景内随机点（起点 = `agentManager.openGroupLocation`） |
| `DownPath` | 沿一条固定的环形路径点列表行走的人流（`Handcrafted` 场景管理器里的固定路径场景） |
| `CrossPath` | 与 `DownPath` 类似但路径设计为与机器人路线交叉，制造横穿场景（模拟"迎面/横穿人流"社交情境） |

`SEAN.cs` 的 `-task-social-situation <SocialSituation>` 命令行参数（`SEAN.cs:72-75`）设置的是 `taskSocialSituation.socialSituation`（`Tasks.Handcrafted` 组件上的字段），**只有当 Task 也选中 `Handcrafted` 时才有意义**（`SeanEditor.cs` 里也能看到：只有 `tasks[selectedTaskIndex] == "Handcrafted"` 时才会画出 `socialSituation` 的字段）。这是 Task 层（机器人任务）的 `SocialSituation`，与 PedestrianBehavior 层的 `Handcrafted.current`（决定行人怎么摆）是**同一个枚举类型，但通过不同路径设置**——`Tasks.Handcrafted` 拿到 Task 层的选择后会调用 `PedestrianBehavior.Handcrafted.NewScenario(situation)` 把行人也摆成对应情境（具体调用链**需确认**，`SEAN_architecture_analysis.md` §2.6 提到 `Tasks/Handcrafted.cs` "转发给 `PedestrianBehavior.Handcrafted.NewScenario()`"，本次未重新逐行验证这条转发代码）。

---

## 2. 改造建议（未落地，供讨论）

> 以下均为**建议方案**，代码未修改。目标是复用现有机制、把改动量降到最低，而不是推倒重来。

### 2.1 "Play 只演一种"：最小改动方案

**不建议**新写一个"选择器/门禁"组件去做互斥逻辑——§1.4 已经说明 `SEAN.SetPedestrianBehavior(name)` 本身就是一个正确、完整的互斥实现（选中的 `SetActive(true)`，其余全部 `SetActive(false)`）。真正缺的只是"**在 `Awake()` 阶段无条件调用它一次**"这一步。

**最小改动**（两处，且互相独立、可以只做其中一个）：

1. **修复 prefab 层面的作者遗留问题（防御性，成本极低）**：把 `Assets/Resources/SEAN/PedestrianBehaviors.prefab` 里 `None` 的 `m_IsActive` 改回 `0`（或者反过来只留 `None` 关掉 `GraphNav`，取决于团队想要的默认 scenario）。这本身不能防止未来再次被误勾选 2 个，但能让"当前这次警告"立刻消失，且是原本就该做的资源清理。
2. **在 `SEAN.Awake()` 里加一次无条件的互斥调用**（这是结构性修复，见 §2.2 的字段设计）：在 `_PedestrianBehaviors` 赋值之后、`ParseCommandLineArgs()` 之前，读取一个新增的 Inspector 可配置字段，调用一次 `SetPedestrianBehavior(...)`。因为这发生在 `Awake()` 里，早于所有 scenario 子物体自己的 `Start()`（Unity 的 Awake-全部先跑完-再跑 Start 保证），可以**在任何一个多余 scenario 的 spawn 逻辑被触发之前就把它关掉**——即使将来又有人在 Editor 里不小心同时勾选了两个 scenario 的 Active，Play 一开始也会被这行代码强制收敛成 1 个。
   - `-scenario` 命令行参数（§1.5）在 `ParseCommandLineArgs()` 里晚于这次调用，如果两者都出现，命令行参数最终生效——与现状行为一致（命令行本来就该是最高优先级，用于评测/批处理场景）。
   - 需要用 `if (Application.isPlaying)` 包一层再调用，因为 `SEAN` 类是 `[ExecuteAlways]`，`Awake()` 在编辑器模式（非 Play）下脚本重编译等时机也会触发——不希望在编辑模式下也去强制 `SetActive`，避免打断美术/策划在编辑器里手动摆场景数据的工作流。

这样两步组合：**运行时永远只有一个 scenario 活着**，且这个保证不依赖任何人记得手动维护 prefab 的 active 状态。

### 2.2 Inspector 下拉切换设计

**加在哪个脚本/对象上**：直接加在 **`Assets/Scripts/SEAN/SEAN.cs`** 这个已有的单例类上，不新建脚本/对象。理由：
- `SEAN.cs` 已经是全局唯一装配器，`SetPedestrianBehavior()`/`pedestrianBehaviors`/`_PedestrianBehaviors` 全部在这里，字段和逻辑放在一起，不引入新的跨对象依赖。
- `SeanEditor.cs` 已经是 `SEAN` 的专属自定义 Inspector，天然是展示这个新字段、以及未来做更丰富 UI（比如给每个 scenario 加说明文字）的地方，不需要再写一个新 `CustomEditor`。

**字段设计**：

```csharp
// 与 /SEAN/PedestrianBehaviors 下现有子物体名字一一对应；
// 新增/改名 PedestrianBehavior 子类时需要同步维护这个枚举。
public enum ScenarioSelection
{
    GraphNav,
    Random,
    HandcraftedSocialSituation,
    Playback,
    LabStudy,
    None,
}

[Tooltip("Play 开始时自动激活的 pedestrian scenario；其余全部强制关闭。"
       + "命令行 -scenario 参数（如果传了）优先级更高，会覆盖这里的选择。")]
public ScenarioSelection selectedScenario = ScenarioSelection.None;
```

- 这是一个**普通 `public` 枚举字段**，Unity 默认 Inspector 会自动渲染成下拉框（Popup），不需要 `[SerializeField]` 以外的任何特殊 attribute（`public` 字段默认就会被序列化+显示）。也不需要额外的 `ExecuteAlways`——这个 attribute 已经加在 `SEAN` 类本身了。
- **默认值建议设为 `None`**（安全的空场景），避免"没人配置这个字段的旧场景/prefab 实例"在升级后被意外强制切到 `GraphNav`。每个场景（Lab/Outdoor/Warehouse）各自的 SEAN prefab 实例可以有各自的 override 值（这是 Unity prefab override 机制本来就支持的，不需要额外代码）——比如给 Outdoor 场景设成 `GraphNav`、给一个新的行人研究场景设成 `HandcraftedSocialSituation`。

**和现有 `SetPedestrianBehavior()` 对接**：

```csharp
private void Awake()
{
    ... // 现有的 _instance / _Environment / 遍历 _SEAN.transform 赋值逻辑不变
    ...
    if (Application.isPlaying)
    {
        SetPedestrianBehavior(selectedScenario.ToString());
    }
    ParseCommandLineArgs(); // 不变，-scenario 若出现会在这之后再调用一次 SetPedestrianBehavior，覆盖上面的结果
    ...
}
```

`selectedScenario.ToString()` 直接产出的字符串（`"GraphNav"`、`"HandcraftedSocialSituation"` 等）与 §1.1 表格里的 GameObject 名字逐一对应，天然兼容现有 `SetPedestrianBehavior(string name)` 的按名字匹配逻辑，不需要额外的映射表。

**`SeanEditor.cs` 现有下拉框怎么处理**：**建议保留**，不删除。理由是它和新字段服务于不同场景：
- 新的 `selectedScenario` 字段解决的是"**Play 一开始**该用哪个、且必须可靠"（构建版本/无人值守评测跑批也要生效，因为它是普通序列化字段+`Awake()` 里代码调用，不依赖任何编辑器专属 API）。
- `SeanEditor.cs` 里现成的 `EditorGUILayout.Popup` 解决的是"**Play 过程中**临时切换、调试用"的即时交互（比如已经在跑 `GraphNav`，想不重启 Play 直接切到 `Random` 看效果）。这个能力目前就有，值得留着。
- 两者不冲突：可以在 `SeanEditor.cs` 里把这个自定义 popup 的当前选中状态**同步显示**新的 `selectedScenario` 字段值（`serializedObject.FindProperty("selectedScenario")`），让用户在 Inspector 里既能看到"启动时会用哪个"，也能在运行中随手切换——但这属于锦上添花，不是最小改动的必需部分。

### 2.3 为个性化（personality / belief-state）预留接口

**结论先行**：调制点应该放在 `Assets/Scripts/SEAN/Scenario/Agents/Base.cs` 的 **`Update()` 方法、`velocity = UpdateVelocity();` 这一行之后**（`Base.cs:70`），而不是去改 `IVI.SFAgent.UpdateVelocity()` 内部、也不是新写一个替代 `SFAgent` 的 Agent 子类。理由：

```csharp
void Update()
{
    velocity = UpdateVelocity();   // <- Base.cs:70，SFAgent 算出来的"纯社会力"速度
    Move();                        // <- 消费 velocity 驱动旋转/动画
}
```

- **`Agents.Base.Update()` 是唯一的调用点**：已确认 `IVI.SFAgent`（`Assets/IVI/Scripts/SFAgent.cs`）只 `override` 了 `UpdateVelocity()`，没有重写 `Update()`——所以在这一行之后插入调制逻辑，能覆盖**所有**用 `SFAgent` 的行人，不需要在每个 scenario/manager 里分别处理。
- **是"调制"不是"替换"**：`UpdateVelocity()` 返回的已经是社会力模型算好的目标引力 + 行人斥力 + 墙体斥力合成后的速度向量。个性化（scared 会不会绕开人群走更宽的弧线、curious 会不会放慢速度多看一眼、annoyed 会不会加快通过节奏）本质上是对这个向量做**幅度缩放 / 方向偏转 / 阈值调整**，而不是重新发明一套走路逻辑——放在 `SFAgent` 算完之后拦截一次，正是"调制而非替换"的自然实现位置。
- **不要改 `Agents.Base` 抽象契约本身**（即不要把 `UpdateVelocity()` 从 `abstract` 挖空重构），因为 `ORCA.Agent`、`Playback.Agent` 也依赖同一个契约（虽然 `ORCA.Agent` 目前是占位未实现，`Playback.Agent` 是直接返回外部轨迹速度）——在 `Update()` 里加调制钩子对这两者同样适用（`Playback` 场景大概率不需要调制，但架构上不冲突）。

**具体建议的钩子形态**（这是设计层面的建议，未写代码）：

```csharp
// Agents/Base.cs 内新增一个 protected virtual 方法，默认恒等：
protected virtual Vector3 ModulateVelocity(Vector3 socialForceVelocity)
{
    return socialForceVelocity;
}

void Update()
{
    velocity = ModulateVelocity(UpdateVelocity());
    Move();
}
```

- 之所以建议 `protected virtual` 挂在 `Base` 上而不是要求"个性化逻辑必须继承 `SFAgent` 覆盖它"，是为了保留**组合（composition）优先于继承**的路子：未来接入 MetaUrban 的 personality/belief-state 时，更自然的做法是写一个独立的 `PersonalityModulator`（或类似命名）`MonoBehaviour`，通过 `GetComponent<IVI.SFAgent>()` 拿到同一 GameObject 上的 `SFAgent`（或者更松耦合地，通过一个 `IVelocityModulator` 接口 + `GetComponents<IVelocityModulator>()` 在 `Update()` 里依次调用所有挂载的调制器叠加效果），而不需要每加一种个性就派生一个新的 `Agents.Base` 子类。这样 `SFAgent` 保持不变、"标准/无个性"的行人完全不受影响，个性只是**额外挂上去的组件**。
- 挂载时机：参考 `Assets/Scripts/SEAN/Scenario/Agents/RandomAvatar.cs` 里 `avatarObject.AddComponent<IVI.SFAgent>();` 这一行（`RandomAvatar.Awake()` 内，决定用 SF 还是 ORCA 的地方）——未来给行人加个性，最自然的接入点就是紧挨着这行再加一句 `avatarObject.AddComponent<PersonalityModulator>()`（若该 avatar 需要个性化的话），两者在同一个 GameObject 上共存，`SFAgent` 完全不知道旁边多了个组件。
- belief-state（如果指的是"行人对机器人/其他行人意图的信念/预测"这类更复杂的状态机）大概率需要**跨帧维护内部状态**（不只是纯函数式的速度调制），这种情况下 `ModulateVelocity()` 钩子仍然够用——调制器组件自己在 `Update()`/协程里维护内部 belief 状态，`ModulateVelocity()` 只是它对外暴露的"每帧最终修正量"接口，内部怎么算不影响 `Agents.Base` 的契约。

---

## 3. 风险与需确认清单

- **需确认**：`Lab.unity`、`Warehouse.unity` 两个场景里 `/Environment/PedestrianControl` 子树、以及 `/SEAN/PedestrianBehaviors` 的实际 active 状态（本次静态解析因嵌套 prefab override 未能读出，需要在 Unity Editor 里直接查看或写 `PrefabUtility` 脚本解析）。
- **需确认**：`/SEAN/PedestrianBehaviors` 下子物体的实际遍历顺序（决定了当前 `GraphNav`+`None` 同时 active 时，`pedestrianBehavior` getter 具体会锁定哪一个）。
- **需确认**：`Tasks.Handcrafted` → `PedestrianBehavior.Handcrafted.NewScenario()` 的具体转发调用链（`SEAN_architecture_analysis.md` 提到但本次未重新逐行验证）。
- §2.1/2.2 的方案改动点集中在 `SEAN.cs`（新增字段 + `Awake()` 里一行调用），改动面很小，但**必须验证**：新增的 `if (Application.isPlaying) SetPedestrianBehavior(...)` 调用不会与 `SetPedestrianBehavior()` 现有的 `throw new ArgumentException`（找不到匹配名字时）在某些历史场景/prefab 实例上意外触发——例如如果某场景的 `/SEAN/PedestrianBehaviors` 缺失了枚举里列出的某个子物体名字（不太可能但未逐场景验证）。
- §2.3 的调制钩子方案本身不改变现有行为（默认恒等实现），风险很低；但真正接入 MetaUrban personality 时，"调制后的速度是否还需要重新过一遍 `SFAgent` 内部的速度上限（`Parameters.MAX_VEL`）等约束"需要在实现时确认，避免调制器把速度改到超出社会力模型原本假设的物理合理范围。
