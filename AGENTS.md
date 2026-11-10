# AGENTS.md — 开发者与维护者指南

面向**改代码的人**（包括 AI agent）。普通用户看 [README.md](README.md) 就够了。

---

## 1. 项目定位

Windows 桌面小工具：一次性拉起多个微信实例。纯托管代码，零第三方依赖，
不注入、不 Hook、不改微信任何文件 —— 只在内核对象（命名互斥量）层面做文章。

技术栈：C# 13 / .NET 9 / WinForms（`net9.0-windows`）+ 手写 Win32 P/Invoke。
界面全部代码构建，无 `.Designer.cs`、无 `.resx`。

## 2. 环境要求

| 项 | 版本 |
| --- | --- |
| .NET SDK | 9.0+（`dotnet --version` 验证） |
| 目标运行时 | `win-x64`，需 .NET 9 桌面运行时（发布自包含版则不需要） |
| 操作系统 | Windows 10/11 |

## 3. 目录结构

```
src/WeChatBoot.Core/        核心逻辑，无 UI 依赖，可被任何宿主复用
  NativeMethods.cs          Win32 P/Invoke 声明（EnumWindows / CreateMutex / SetSecurityInfo / NtQueryObject …）
  WeChatLocator.cs          安装寻址：注册表 → 运行中进程 → 常见目录 → 用户手动指定
  InstanceManager.cs        实例计数（按主窗口去重）、进程扫描、窗口分类
  MutexGuard.cs             互斥量布防（ACL deny）与互斥量名自动发现
  HandleScanner.cs          句柄法：从微信进程里找到并关闭单实例互斥量句柄
  Launcher.cs               启动编排：策略降级、稳定性判据、实例数增量校验
  AppConfig.cs              便携配置读写（exe 同目录，退回 %APPDATA%）
src/WeChatBoot.App/         WinForms 界面（唯一入口 exe）
  Program.cs                启动、崩溃日志钩子、--settings 参数
  MainForm.cs               主界面：操作卡 / 实例列表 / 可收起日志
  SettingsForm.cs           高级设置（收纳所有不该出现在主界面的开关）
  Theme.cs                  颜色、字体、圆角路径
  Controls.cs               自绘控件：Card / PillButton / Stepper / PickerField / InstanceRow
tools/WeChatBoot.Probe/     无界面诊断探针（见 §7）
docs/mechanism.md           原理与实测记录（含失败模式时间线）
docs/research/              原始资料调研笔记
scripts/build-release.cmd   本地一键发布
.github/workflows/          CI：构建、发布
```

## 4. 多开原理（改代码前必须知道）

微信判断「是不是已经有一个我在跑」靠一个**会话级命名互斥量**：
`CreateMutex` 返回 `ERROR_ALREADY_EXISTS` 就把旧窗口还原到前台、自己退出。
这是多开失败的唯一表现——**不报错，只是把已有窗口拉到前面**。

所以只要让微信的 `CreateMutex` 返回 `ERROR_ACCESS_DENIED` 而不是「已存在」，
单实例判断就落空。做法：自己先 `CreateMutexW(同名)`，把它的 DACL 改成
**Deny Everyone `MUTEX_ALL_ACCESS (0x1F0001)`**，并**常驻持有**这个句柄。

互斥量名（`MutexGuard.DiscoverIdentityMutexNames()` 会从运行中进程的句柄表自动发现）：

| 版本 | 名字 |
| --- | --- |
| 3.x | `_WeChat_App_Instance_Identity_Mutex_Name` |
| 4.0.2+ | `XWeChat_XWEB_Instance_Identity_Mutex_Name` |
| 兜底 | 上面两个的 `_Global` 变体 + 用户配置里的额外名字 |

`HandleScanner`（关闭目标进程内的互斥量句柄，需 `PROCESS_DUP_HANDLE`）是**兜底**：
实测对 4.2 无效（关掉句柄后第二个实例仍被折叠），只在 ACL 全失败时降级使用。

3.x 除了互斥量还有一层 `FindWindow(WeChatMainWndForPC / WeChatLoginWndForPC)` 二次检查，
所以 3.x 多开是「半可用」——第二个实例能起来，约 2 秒后自退。**不要 hack 它**，
如实标注即可。

## 5. 实例计数规则（最容易写错的地方）

- **一个实例 = 一个主窗口**，不是进程数。4.x 单个实例会拉起 `Weixin.exe` 主进程
  + 多个 `WeChatAppEx.exe`（插件运行时，在 `%APPDATA%\Tencent\xwechat\xplugin\...`）。
- 主窗口识别：4.x 窗口类以 `QWindowIcon` 结尾（当前 `Qt51514QWindowIcon`，Qt 版本号会变，
  只比对后缀）；3.x 是 `WeChatMainWndForPC` / `WeChatLoginWndForPC`；标题兜底但**排除 `#32770`**，
  否则「微信已在运行」的提示框会被算成一个实例。
- **不要要求窗口可见**：4.x 主窗口在托盘时 `IsWindowVisible=false`，早期版本因此把
  「明明在运行」的微信数成 0。
- **按安装目录分组**：本机可能同时有 4.1 和 4.2 两个 `Weixin.exe`，进程名一样。
  `Launcher.InstancesOf(exePath)` 比的是 `ImagePath` 的目录，不是进程名。

## 6. 踩过的坑（照抄结论，别再踩）

1. **ACL 依附在内核对象上**：布防进程退出、且没有微信进程还持有该互斥量时，对象被销毁，
   DACL 一起消失。所以布防的 `MutexGuard` 必须**活着覆盖整个启动过程**（`using var guard` 包住循环）。
2. **deny DACL 有粘性**：一旦布防过，只要还有实例在跑，后续即使不布防也可能成功。
   调试前先 `probe close` 结束所有实例，否则你会得到一个「怎么改都能多开」的假象。
3. **超时重试会多开一个**：微信冷启动实测 ~7 秒才出主窗口，默认只等 6 秒。等不到就换策略
   再发一次请求，而第一条请求其实会成功 → 一个位置两个窗口。
   `Launcher.ArrivedLate()` 在换策略前宽限 6 秒，确认是「窗口来晚了」就算成功。
4. **判据必须是「本次新开了几个」**，不能是「现在总共有几个」。用总数当判据时，
   用户中途手动关掉一个窗口，程序会把那个位置补回来（用户视角：它自己又开了一个）。
5. **`UseShellExecute` 必须是 `true`**：`false` 时子进程继承父进程的 stdout 管道，
   3.x 写日志失败会闪退，而且调用方会一直等 EOF。
   代价是拿不到 `Process.HasExited`，存活判定用 `Process.GetProcessById`。
6. **不要混用 Dock**：自绘卡片 + 运行时改尺寸的窗口里，`Dock.Fill` + `Dock.Top` 的排列顺序
   不可靠（实测 header 被 body 覆盖）。全部用显式 `Bounds`，在 `LayoutShell()` / `LayoutCards()` 里排。
7. **锚点基准**：创建控件时窗口宽度还是默认值（200），此时用 `ClientSize.Width` 算坐标、
   再设 `Anchor = Right`，运行后会被二次补偿推到窗口外。先 `SetBounds` 到正确宽度再排子控件。
8. **自绘控件 `OnResize` 会在构造期触发**：此时子控件字段还是 null，必须判空，否则
   `NullReferenceException`（曾经把实例行整片打死，靠 `wechatboot-crash.log` 抓到的）。
9. **`PickerField.SetItems` 不触发 `SelectionChanged`**：它直接改索引。调用方要显式同步
   `_config.LastProductKey`，否则留下「配置记 A、界面选 B」的暗坑。

## 7. 探针（`tools/WeChatBoot.Probe`）

无界面验证工具，改核心逻辑时先用它验证，再跑界面：

```bash
dotnet run --project tools/WeChatBoot.Probe -- <命令>
```

| 命令 | 作用 |
| --- | --- |
| `detect` | 列出寻址结果（注册表/进程/目录）与版本号 |
| `instances` | 按主窗口统计实例，附进程清单 |
| `windows` | dump 所有可见顶层窗口（pid / class / title），排查窗口识别 |
| `mutants` | dump 微信进程里的所有 Mutant 句柄名，排查互斥量改名 |
| `discover` | 只用自动发现逻辑找出互斥量名 |
| `guard` | 布防一次并打印结果 |
| `handles` | 列出可关闭的单实例互斥量句柄 |
| `free` | 句柄法：关闭实例内的互斥量句柄 |
| `launch <exe> [--strategy ...] [--hold-after n] [--mutex name]` | 启动并校验 |
| `close` | 结束所有微信进程（**调试前必跑**） |
| `selftest` | 五项自检 |

`--strategy` 可选：`auto`（默认）/ `acl` / `handle` / `direct`。

## 8. 构建与发布

```bash
dotnet build WeChatBoot.sln -c Release          # 编译，0 警告是基线
scripts/build-release.cmd                        # 单文件便携版 → dist/WeChatBoot.exe（依赖运行时）
dotnet publish src/WeChatBoot.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist-standalone
```

发布走 GitHub Actions（`.github/workflows/release.yml`）：推送 `v*` 标签 →
自动构建「轻量版」（需 .NET 9 桌面运行时，~340KB）和「自包含版」（免安装运行时，体积大）
→ 作为 Release 资产发布。

本地发布前先关掉正在运行的程序，否则 `MSB3021`（exe 被占用）。

## 9. 界面约定

- **约定大于配置**：主界面只放「用哪个微信」「开几个」「打开」三件事；
  策略、间隔、超时、互斥量名全部进设置页，默认值够用。
- **文案去行话**：不出现「互斥量」「DACL」「折叠」这类词。失败原因走
  `MainForm.FriendlyReason()` 翻译成人话。
- **按钮说增量**：「打开 2 个微信」= 还要新开 2 个，不是总数；已经够了就显示
  「已经开着，不用重复打开」。
- **自绘要处理悬浮/按下态**，并且 `OnPaint` 先 `g.Clear(Parent.BackColor)` 再填圆角，
  否则圆角外会留残影。
- 配色见 `Theme.cs`：主色 `#07C160`，背景 `#F4F6F9`，卡片纯白，正文 `#1B1F24`。

## 10. 手工验收清单

改完之后依次确认（`dotnet run --project tools/WeChatBoot.Probe -- close` 起手）：

1. `instances` 数量与任务栏看到的窗口数一致；
2. 主界面「打开 3 个微信」→ 三个窗口各自出现，`wechatboot.log` 出现
   `这一轮结束：新开 3 个（目标 3 个）`；
3. 手动关掉其中一个 → 等 30 秒，数量不回升，日志没有新的「收到启动请求」；
4. 再点一次按钮 → 显示「已经开着，不用重复打开」；
5. 设置页改间隔/超时并保存 → 重启程序后仍生效；
6. 删掉 `wechatboot.config.json` 重启 → 自动推荐正确产品，界面不报错。

## 11. 已知限制

- 3.x 多开「半可用」（见 §4）。
- 多开出的实例都要单独扫码；同一账号的全局免扫码只有一份设备凭据。
- 未登录的实例可能几分钟后自己退出（微信侧行为）。
- 部分版本有官方「最多 4 个」限制。
- 微信 4.x 对「客户端被杀毒/注入改动」敏感，本工具不做任何改动，规避该风险。
