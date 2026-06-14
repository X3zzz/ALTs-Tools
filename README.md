# ALTs-Tools (macOS)

Minecraft 多账号管理器的 **macOS 原生移植版** —— 存储、组织、一键切换多个 Minecraft 账号，并把当前账号**注入到正在运行的游戏**里，无需重启。

> 本项目是 [NoobCock/RefreshToAccess2](https://github.com/NoobCock/RefreshToAccess2) 的 macOS 分支：保留 100% 功能，UI 用 Avalonia + Material You 3 重构，账号注入针对 Apple Silicon (ARM64) 重新实现。

## 功能

- **账号管理** — 卡片墙展示头像 / 名称 / UUID / 登录时间，支持搜索排序，一键切换登录。
- **令牌转换** — Refresh Token / Cookie 转 Minecraft 访问令牌，支持 Vanilla、HMCL、PCL、Essential、BakaXL、LabyMod 等 7 种客户端格式，含令牌过期检测。
- **账号注入** — 把选中账号的令牌注入到运行中的 Minecraft JVM，实时改写游戏内会话（名称 / UUID / 令牌），**无需重启、支持反复换号**。
- **皮肤预览** — 从文件或链接换肤、在线改名、抓取他人皮肤，配**实时软件光栅化 3D 角色预览**（旋转、缩放、待机/行走动画、全景背景）。
- **设置** — 多语言、明暗主题、Material You 动态取色。

## 技术栈

- **UI**：Avalonia UI 11.2 + Material.Avalonia（Material You 3 风格，默认浅色，紫色强调）
- **运行时**：.NET 10，原生 ARM64
- **注入**：原生 ARM64 注入器（`task_for_pid` + Mach VM + `thread_create_running` 新建线程 → `pthread_create_from_mach_thread` → `dlopen`），dylib 内通过 `JNI_GetCreatedJavaVMs` 挂载 JVM、`defineClass` 注入 `cn.zhyujun.tokenswap.TokenSwapper`，用 `sun.misc.Unsafe` 改写会话字段；HTTP 协议端口 38964，与原版一致。
- **3D 预览**：纯 SkiaSharp 软件光栅化（透视投影 + z-buffer + 方向光）渲染到 Avalonia 位图。
- **存储 / 加密**：沿用原版 AES + LZMA + 偏移加密，数据存于 `~/Library/Application Support/RefreshToAccess/`。

## 构建

```bash
# 1) 先构建原生注入器与 payload（一次即可）
cd mac-inject-poc && make

# 2) 构建并运行 macOS 应用
export DOTNET_ROOT=/usr/local/share/dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd ../mac && dotnet run

# 3) 打包成 .app
./package.sh        # 产出 dist/AltsTools.app
```

## 运行前置

账号注入需要 **SIP 关闭**且**提权运行**（注入器需 debugger entitlement 才能调用 `task_for_pid`）。仅用于管理你**自己**的账号。

## 目录

- `mac/` — Avalonia macOS 应用（UI、ViewModel、Service、3D 渲染）
- `mac-inject-poc/` — 原生注入器、payload dylib、注入用的 `TokenSwapper.java`（mac/ 构建时依赖其产物）
