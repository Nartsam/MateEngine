# MateEngine 二次开发 — 交接文档

> 本文档供 Claude Code 新对话快速接手。上次更新：2026-06-18。

---

## 一、项目概况

**目标**：基于 MateEngine（3D 桌面宠物开源项目）进行 Fork 二开，做成**绿色便携**的个人自用软件。

- **仓库**：https://github.com/Nartsam/MateEngine（个人 fork）
- **Unity 版本**：**6000.2.6f2**（必须精确匹配）
- **Unity 安装路径**：`D:\Program Files\Unity\6000.2.6f2`
- **主场景**：`Assets/MATE ENGINE - Scenes/Mate Engine Main.unity`
- **构建方式**：Mono（开发阶段），详见 `BUILD_MANUAL.md`

---

## 二、已完成的工作

### 2.1 Steam 依赖移除（已完成，未提交）

所有 Steam 相关代码已从项目中移除，构建通过，零编译错误。

**删除的文件/目录**：

| 文件/目录 | 说明 |
|---|---|
| `Assets/MATE ENGINE - Packages/com.rlabrecque.steamworks.net/` | Steamworks.NET SDK 整个包 |
| `Assets/Scripts/Steamworks.NET/SteamManager.cs` | Steam 初始化管理器 |
| `Assets/MATE ENGINE - Scripts/APIs/SteamWorkshopHandler.cs` | 创意工坊上传/下载 |
| `Assets/MATE ENGINE - Scripts/APIs/SteamWorkshopAutoLoader.cs` | 创意工坊自动加载 |
| `Assets/MATE ENGINE - Scripts/APIs/SteamDRM.cs` | Steam DRM 验证 |
| `Assets/MATE ENGINE - Scripts/APIs/SteamVersionObjects.cs` | Steam 版本信息 |
| `Assets/Plugins/steam_api64.dll`, `.lib`, `steam_appid.txt` | Steam 原生插件 |
| `Assets/Editor/PostBuildCopy.cs` | 构建后复制 Steam 文件 |
| `Assets/MATE ENGINE - Scripts/Tools/ModUploadButton.cs` | Mod 上传按钮 |
| `Assets/MATE ENGINE - Scripts/Tools/ModUploadHoldHandler.cs` | Mod 上传长按 |
| `Assets/MATE ENGINE - Scripts/Settings/UploadButtonHoldHandler.cs` | 头像上传长按 |
| `steam_appid.txt`（根目录） | Steam App ID |

**修改的文件**：

| 文件 | 修改内容 |
|---|---|
| `MEModHandler.cs` | 移除 `using Steamworks`、上传按钮代码块、`ResolveWorkshopIdForPath` 方法 |
| `AvatarLibraryMenu.cs` | 移除 `SteamWorkshopAutoLoader` 引用、Steam Workshop 目录检测、上传按钮逻辑；`uploadButton`/`uploadSlider` 直接 `SetActive(false)` |
| `ModRemoveButton.cs` | 完全重写，仅保留本地文件删除 + 刷新列表功能 |
| `DeleteButtonHoldHandler.cs` | 移除 `SteamWorkshopHandler.Instance.UnsubscribeAndDelete` 代码块 |
| `AccessoiresHandler.cs` | 移除 `SteamDRM.Initialize` 调用、`ReinitLoop` 协程、`steamExclusive` 条件判断；保留 `steamAppId`/`ttlDays`/`steamExclusive` 等序列化字段避免破坏场景引用 |

**设计决策**：`AvatarEntry` 类中的 `isSteamWorkshop`/`steamFileId` 字段、`AccessoiresHandler` 中的 Steam 相关序列化字段均保留，避免破坏 Unity 场景序列化和 JSON 向后兼容。

### 2.2 构建脚本（已完成）

创建了 `Assets/Editor/BuildScript.cs`：
- 原因：`EditorBuildSettings.asset` 中 `m_Scenes: []` 为空，直接用 `-buildWindows64Player` 会报 `ArgumentException: Scene file not found`
- 解决：自定义 BuildScript 显式指定场景路径，使用 `-executeMethod BuildScript.BuildWindows`
- 构建产物输出到 `Build/MateEngine.exe`（约 530 MB）

### 2.3 .gitignore 清理（已完成）

- 新增：`UserSettings/`（整目录）、`.vs/`、`steam_appid.txt`、`Assets/Plugins/steam_api*`、`*_BurstDebugInformation_DoNotShip/`、`Assets/StreamingAssets/LLMManager.json`
- 已用 `git rm --cached` 移除了 `.vs/`（3文件）、`UserSettings/`（6文件）、Steam 插件文件等共 16 个不应追踪的文件

### 2.4 文档（已完成）

- `BUILD_MANUAL.md`：构建步骤文档（中文）

---

## 三、当前 Git 状态

**分支**：`main`（所有变更尚未提交）

变更统计：306 个文件变动，+21 / -43293 行（绝大部分是 Steamworks.NET SDK 删除）。

主要变更分类：
- **删除**：Steamworks.NET 包、Steam API 脚本、Steam 插件、上传相关 UI 脚本
- **修改**：5 个混合了 Steam 代码的脚本（最小化改动）、`.gitignore`
- **新增（未追踪）**：`Assets/Editor/BuildScript.cs`、`BUILD_MANUAL.md`、`HANDOFF.md`

---

## 四、技术栈

| 组件 | 说明 |
|---|---|
| Unity 6 + C# | 引擎 |
| UniVRM | VRM 0.x / 1.x 模型加载 |
| UniWindowController | 透明置顶窗口 |
| Newtonsoft.Json | 设置序列化 |
| MToon (URP) | 默认卡通着色器 |
| Poiyomi / lilToon | 可选高级着色器（通过 .me Mod） |
| QWEN 2.5 1.5b | 可选本地 LLM（AI 聊天） |
| VRM SpringBone | 物理（头发、衣物摆动） |

---

## 五、核心脚本

| 脚本 | 职责 |
|---|---|
| `VRMLoader.cs` | 模型加载、组件注入、持久化模型路径 |
| `AvatarAnimatorController.cs` | 动画状态机：Idle / Walk / Drag / Dance / Sit |
| `AvatarMouseTracking.cs` | 头部、眼睛、脊柱跟随鼠标 |
| `HandHolder.cs` | 手部 IK 跟随鼠标 |
| `PetVoiceReactionHandler.cs` | 鼠标悬停触发身体区域反应 |
| `AvatarTaskbarController.cs` | 坐到任务栏/窗口边缘 |
| `SaveLoadHandler.cs` | 设置 JSON 序列化与持久化 |
| `MEModHandler.cs` | .me 格式 Mod 运行时加载 |
| `AvatarLibraryMenu.cs` | 头像库 UI（选择/删除模型） |
| `AccessoiresHandler.cs` | 配饰跟随骨骼 |

---

## 六、构建产物与外部写入

### 项目内（可控）

| 目录 | 说明 |
|---|---|
| `Build/` | 最终可执行文件（.gitignore 已排除） |
| `Library/` | 资产导入缓存（可删除重建） |
| `Temp/` | 编译临时文件（编辑器关闭后自动清理） |
| `Logs/` | 编辑器日志 |
| `obj/` | .NET 中间输出 |

### 外部（不可避免）

| 位置 | 说明 |
|---|---|
| `%LOCALAPPDATA%\Unity\` | 全局着色器缓存、GI 缓存、License |
| `%APPDATA%\Unity\` | 编辑器偏好设置、License 激活 |
| `HKCU\Software\Unity\` | 注册表编辑器偏好 |

这些外部写入是 Unity 编辑器固有行为，无法通过构建参数重定向。

---

## 七、待做事项（按优先级）

### 7.1 提交当前变更

当前所有工作均未 commit。建议分 1~2 个 commit 提交：
- Steam 移除 + 构建脚本 + .gitignore 清理

### 7.2 绿色化改造（运行时数据路径）

目标：让软件运行时产生的所有数据都在程序目录内，删除文件夹即完全清理。

当前问题：

| 位置 | 内容 | 来源 |
|---|---|---|
| `HKCU\Software\<Company>\<Product>` | 模型路径 | `VRMLoader.cs`（PlayerPrefs） |
| `%AppData%\..\LocalLow\<Company>\<Product>\` | 设置 JSON、VRM 缓存 | `SaveLoadHandler.cs` |
| `%TEMP%\` | .me Mod 解压临时文件 | `MEModHandler.cs` |

改造方案：

```csharp
// VRMLoader.cs — 去掉 PlayerPrefs，改写本地文件
// 原: PlayerPrefs.SetString("SavedPathModel", path);
// 改: 写入 ./UserData/model_path.txt 或并入 SaveLoadHandler 的 JSON

// SaveLoadHandler.cs — persistentDataPath → 程序目录
// 原: Application.persistentDataPath + "/" + fileName
// 改: Application.dataPath + "/../UserData/" + fileName
```

### 7.3 可选后续方向

- **程序化空闲动作**：自主张望（随机兴趣点替代鼠标坐标）、自主踱步（Blend Tree + 程序化位移）
- **渲染升级**：针对特定角色挂载专用 Shader（GenshinCelShaderURP、StellarToon 等），通过 .me Mod 系统加载
- **外部通信接口**：`HttpListener` / WebSocket / VMC Protocol（OSC over UDP），参考现有 MateSignal（TCP+UDP 端口 32145）

---

## 八、Mod 系统

`.me` 文件本质是 ZIP，内含 AssetBundle + 元数据 + `scene_links.json`。可包含自定义 Shader、AnimatorController、AnimationClip。不能直接包含 C# 脚本（AssetBundle 限制），需编译为 DLL。

核心扩展点 `MEManipulator` 可覆盖内置 Controller 逻辑。

---

## 九、注意事项

- 默认 Avatar（Zome）版权为 All Rights Reserved，不可随 build 分发
- Unity 版本必须**精确匹配** 6000.2.6f2，否则会触发资产重导入甚至兼容性问题
- 不要打开 `DEV` / `InDev` 场景，仅使用 `Mate Engine Main`
- 项目使用 URP 渲染管线
