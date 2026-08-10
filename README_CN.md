# deepseek-planbar

[English](README.md)

Windows 托盘应用，一眼看清 **DeepSeek 余额与 token 用量**——不用再每天打开 <https://platform.deepseek.com/usage>。

> 非官方工具，与 DeepSeek 无关联。用量数据来自平台网页端的内部接口（无公开承诺，可能随时变更）；余额接口是官方公开 API。
>
> 姊妹项目：**[kimi-planbar](https://github.com/baigong-ai/kimi-planbar)**（Kimi Code 额度状态栏）与 **kimi-planbar-tray**（本应用改造自它的 WPF 托盘骨架）。

亮色 | 暗色
---|---
![亮色](docs/screenshot-light.png) | ![暗色](docs/screenshot-dark.png)

## 面板内容

- **余额卡**：总余额（赠送 + 充值）、可用状态；余额过低（< ¥10）标红
- **按模型用量卡**：每个模型一张卡（V4 Pro、V4 Flash……）：本月消费、本月 token（带相对进度条）、缓存命中率（≥85% 绿 / 60–84% 黄 / <60% 红）、今日消费与 token
- **合计行**：全部模型的本月消费/token 与今日消耗
- 托盘 tooltip 显示余额 + 本月消费；左键开合面板，右键弹出菜单

面板按可配置的间隔自动刷新（1/5/10/30 分钟），失败后 30 秒快速重试，刷新失败时保留上次成功的数据。

## 两套凭证（缺一不可，互不通用）

| 数据 | 接口 | 凭证 | 有效期 |
|---|---|---|---|
| 余额 | `GET https://api.deepseek.com/user/balance` | API Key（`sk-...`，控制台创建） | 长期有效 |
| token 用量/消费 | `GET https://platform.deepseek.com/api/v0/usage/amount|cost` | 网页登录 token（userToken） | 短期，过期需重新获取 |

用量 token 获取方式（两种）：

- **应用内登录（推荐）**：设置 → "网页登录自动同步" → 在内嵌 WebView2 窗口中登录 DeepSeek（扫码/验证码/密码均可）。登录后自动捕获 token、调接口验证、保存——不用碰 F12。token 过期后再点一次即可。
- **手动（永久兜底）**：浏览器登录 <https://platform.deepseek.com> → 按 `F12` → Console → 执行 `JSON.parse(localStorage.userToken).value` → 复制结果粘贴到设置。

API Key 需手动填一次（长期有效；控制台里已有 Key 只显示掩码，无法从网页会话自动导入，已探测确认）。新值会先调真实接口验证，通过才保存；401 表示无效或已过期。

## 构建与运行

需要 Windows + .NET 8 SDK。

```bash
cd tray-wpf
dotnet build
./bin/Debug/net8.0-windows/DeepseekPlanbarTray.exe
```

应用常驻系统托盘。从面板或右键菜单打开设置，粘贴 API Key 与用量 Token。

## 发布版本

两种版本，各自只有一个 exe，无需附带其他文件（均为 x64，均需 WebView2 Runtime——Windows 11 及大多数 Windows 10 自带）：

```bash
cd tray-wpf
# 框架依赖版（约 1.4 MB，需已安装 .NET 8 Desktop Runtime）：
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# 自包含版（约 69 MB，无需安装 .NET）：
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-sc
```

普通用户建议选自包含版；已装 .NET 8 Desktop Runtime 的可选小体积版。`IncludeNativeLibrariesForSelfExtract` 会把 WebView2/WPF 原生 DLL 一并打进 exe（首次运行时自解压到临时目录）。

## 文件位置

- 设置（主题/刷新间隔/开机自启）：`%APPDATA%/DeepseekPlanbarTray/settings.json`——exe 旁存在 `portable.dat` 时改写 exe 同目录（便携模式）
- 凭证：`%USERPROFILE%/.deepseek-planbar/credentials.json`（用户目录默认仅本人可访问）。环境变量 `DEEPSEEK_API_KEY` / `DEEPSEEK_USER_TOKEN` 优先
- 缓存：`%USERPROFILE%/.deepseek-planbar/cache.json`——最近一次成功拉取，原子写入；刷新失败时显示旧数据

凭证除调用 `api.deepseek.com` / `platform.deepseek.com` 外不离开本机，也绝不提交 git。注意：API Key 泄露会被盗刷余额，userToken 泄露会暴露用量明细；有疑虑请轮换 Key。

## Headless 自检

```bash
# 解析层断言（tests/fixtures 样本，无需凭证）
DeepseekPlanbarTray.exe --test-fixtures path/to/tests/fixtures

# 拉一次数据打印 JSON 退出（未配置/断网时输出结构化错误）
DeepseekPlanbarTray.exe --test-fetch

# 构造全部窗口验证 XAML/资源加载
DeepseekPlanbarTray.exe --test-ui

# 真实初始化一次 WebView2（验证单文件发布的自解压可用）
DeepseekPlanbarTray.exe --test-webview

# 渲染面板为 PNG（mock 数据，可选暗色）
DeepseekPlanbarTray.exe --screenshot out.png --mock [--dark]
```

## 目录结构

| 路径 | 说明 |
|---|---|
| `tray-wpf/` | WPF 托盘应用（.NET 8） |
| `tests/fixtures/` | `--test-fixtures` 用的接口响应样本 |
| `scripts/convert_logo.py` | `deepseek.webp` → `tray-wpf/deepseek-logo.png` 转换脚本 |
| `docs/` | 截图 |

## 致谢

- 用量接口结构与请求头核实自 [DeepSeekMonitorWindows](https://github.com/Joyi-code/DeepSeekMonitorWindows)（MIT）

## 许可证

MIT。非官方社区工具——使用风险自负：内部接口可能变更，凭证安全请自行保管。
