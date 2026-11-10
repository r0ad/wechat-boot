# 多开原理与实测记录

本文记录**在本机真实测到**的行为，不含推测。推测部分单独标注。

## 1. 微信到底怎么判断“已经有一个实例”

微信启动时调用 `CreateMutexW(Local\_WeChat_App_Instance_Identity_Mutex_Name)` 一类命名互斥量；
若返回 `ERROR_ALREADY_EXISTS(183)`，就说明已有实例：它会把已有主窗口
`SetForegroundWindow/BringWindowToTop` 还原，然后**自己退出**。

实测到的对象名（会话级命名空间 `\Sessions\1\BaseNamedObjects\`）：

| 版本 | 互斥量名 | 证据 |
| --- | --- | --- |
| 3.x | `_WeChat_App_Instance_Identity_Mutex_Name` | 3.9.12.55 句柄表命中 |
| 4.2 | `XWeChat_XWEB_Instance_Identity_Mutex_Name` | 4.2.0.51 主进程 13568 句柄表命中（`probe mutants`） |
| 4.0.1 | 无互斥量，用 `Lock.ini` 文件锁 | 社区资料，未在本机复现 |

4.2 主进程里同时还有 `wxid_xxx_WeChat_Win_User_Identity_Mutex_Name`、
`Weixin_id_pp_cd_<pid>`、`XWeChat_System_Tray_Icon_<hash>` 等对象，都和单实例判断无关。

## 2. 失败长什么样（这是本工具最容易被骗的地方）

用错互斥量名时，微信**不报错**，行为是：

```
启动 → 新进程出现 → 主窗口一闪 → 进程退出（耗时约 2–3 秒）
```

如果只等 1.5 秒就判定“出现了新窗口=成功”，会 100% 假阳性。
本项目早期版本就是这么被骗的：日志写“多开成功”，几秒后实例自己没了。

因此 `Launcher` 的判据是：

1. 出现**启动前不存在**的同安装实例 PID；
2. 该 PID 的窗口**连续**存在 ≥ 5 秒（`StabilityMs`，实测折叠延迟 2–3 秒的 2 倍余量）；
3. 一旦“出现过又消失超过 2.5 秒”，立刻判失败并返回原因，不再傻等超时。

另外 `Process.HasExited` 在 `UseShellExecute=true` 时不可用（没有句柄），
所以存活判定走 `Process.GetProcessById(pid)`。

## 3. ACL 布防为什么有效，以及它的两个坑

做法：自己 `CreateMutexW` 同名对象 → `SetSecurityInfo(SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION)`
写入 `Deny Everyone MUTEX_ALL_ACCESS`。

微信此时 `CreateMutexW` 拿到的是 `ERROR_ACCESS_DENIED(5)`，不是 `ERROR_ALREADY_EXISTS(183)`，
单实例分支不成立，于是正常启动。**全程不需要管理员权限**，也不碰微信进程内存。

坑 1：**ACL 是“对象属性”，对象没人持有就没了。**
布防进程一退出，只要微信也没持有该对象，内核对象销毁，deny DACL 一起消失，
下一次启动又回到单实例状态。所以布防必须覆盖“启动 → 微信尝试 CreateMutex”的整个窗口，
GUI 里由 `using var guard` 包住整轮启动。

坑 2：**布防具粘性，会污染后续测试。**
只要还有进程持有该对象，deny DACL 会一直生效；后续 probe 运行会看到
“已被其它进程以拒绝访问方式占用”，误以为是“已经布防好了”。
调试时务必先 `close` 全部实例，再重新布防。

## 4. 本机实测矩阵（4.2.0.51，D:\Program Files\Tencent\Weixin）

| 场景 | 结果 |
| --- | --- |
| 已有 1 个实例，`--strategy direct` | ❌ 新进程 2–3 秒内自杀（被折叠） |
| 已有 1 个实例，布防后 `--strategy direct` | ✅ 2 个实例稳定共存 ≥ 30 秒 |
| 空场景，布防后一次拉起 2 个（GUI「自动」） | ✅ 2 个实例，30 秒后仍在 |
| 关闭运行实例的单实例互斥量句柄后再启动 | ❌ 仍有 1 个额外窗口闪现后消失，句柄法对 4.2 无效 |
| 布防后立刻退出布防进程（短命探针） | ❌ 时好时坏——对象/ACL 生命周期问题，见坑 1 |

结论：**4.x 走“ACL + 布防进程存活覆盖启动过程”这条路**，句柄法留作兜底。

3.x（3.9.12.55）同一套布防下第二个实例能起来，但约 2 秒后仍退出——
3.x 除互斥量外还有 `FindWindow(WeChatMainWndForPC/WeChatLoginWndForPC)` 的二次检查，
非注入手段绕不过去，故 3.x 标注为“半可用”。

## 5. 实例怎么数

一个实例 = **一个主窗口**，不是进程数：4.x 单个实例会拉起 Weixin.exe 主进程
+ 多个 `WeChatAppEx.exe`（插件运行时，路径在 `%APPDATA%\Tencent\xwechat\xplugin\...`），
数进程会得到 5+ 的错值。

主窗口识别：

- 4.x：窗口类以 `QWindowIcon` 结尾（目前是 `Qt51514QWindowIcon`，Qt 版本号会变，所以只比对后缀）；
- 3.x：`WeChatMainWndForPC` / `WeChatLoginWndForPC`；
- 兜底：标题是 `微信` / `Weixin` / `WeChat`，但排除 `#32770`（原生对话框类），
  否则“微信已在运行”的提示框会被当成一个实例。

另外**不能要求窗口可见**：4.x 主窗口在托盘时 `IsWindowVisible=false`，
早期版本因此把“明明在运行”的微信数成 0 个。

## 6. 多版本共存

本机同时装了 4.1.15.56（`C:\Program Files\Tencent\Weixin`）和 4.2.0.51（`D:\Program Files\Tencent\Weixin`），
两者进程名都是 `Weixin.exe`。实例统计必须按**安装目录**分组，否则实例数会互相污染；
`Launcher.InstancesOf(exePath)` 用 `ImagePath` 的目录比对来区分，两个版本可以各自运行。

## 7. 坑 3：超时后重试，会多开一个

布防成功、启动请求已发出，但冷启动实测要 **7 秒上下**才出主窗口，早期默认只等 6 秒。
等不到就按「这个策略失败」处理、换下一个策略再发一次启动请求 —— 而第一次那条请求
其实会成功。一个位置于是开出两个窗口，用户看到的是「我没让它多开，它自己又蹦出来一个」。

现在的处理：

- 换策略**之前**先 `ArrivedLate()` 再宽限 6 秒，确认不是「窗口来晚了」；确认到了就算成功；
- 启动循环的判据是「已经成功开了几个」，不是「现在总共有几个」——用户中途关窗口不会被补回来；
- 界面日志和 `wechatboot.log` 都记下每次「第几次尝试」，事后能直接对时间线。

实测（2026-11-10 13:17，4.2.0.51，干净状态开 3 个）：三次尝试全部走 ACL 一次成功，
间隔约 7 秒，日志 `这一轮结束：新开 3 个（目标 3 个）`。
