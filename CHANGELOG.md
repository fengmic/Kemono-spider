# 更新日志 (Changelog)

本项目的显著变更记录于此。版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/) 规范。

## [3.0.0] - 2026-09-07

> 提交：`4abda21` · 作者：renwentong · 变更规模：60 个文件，+6,880 / −9,403 行

3.0.0 是一次全面重写版本：应用从 Electron + Node.js 迁移至 **.NET 10 + WPF**，在获得更好性能与更小体积的同时，不再支持 Windows 以外的平台分发。

### 新增 (Added)

- **任务队列**：支持添加多个作者/作品任务进行批量下载；任务运行中可继续追加新任务，当前任务完成后自动执行下一个；队列项实时显示作者名称、平台、服务器、进度与当前文件；支持移除等待中任务、清理已结束任务，手动停止后可通过"继续队列"恢复。
- **域名切换**：内置 `kemono.cr`、`kemono.su`、`pawchive.pw` 三个服务器，并支持添加自定义域名——适用于 Kemono 频繁切换域名及基于 Kemono 二次开发、爬取逻辑相同的站点。
- **缩略图补全**：对于无法获取原图的作品，支持使用缩略图进行补全。
- **服务器与作者资料兼容**：统一 Cookie、代理、Referer、Accept-Language 与自动解压请求行为；支持 HTTP/HTTPS/SOCKS5 代理及代理认证；作者名称兼容 `public_id` 与 `name` 字段（Pawchive 返回 `public_id: null` 时回退至 `name`）。
- **单元测试**：新增 `KemonoDownloader.Tests` 项目，覆盖作者队列、Kemono 规则、网络与下载、进度存储、修复服务、爬取服务、设置存储共 7 个测试模块。
- **应用图标与发布配置**：正式应用图标（`app-icon.ico` / `app-icon.png`）及 Windows x64 自包含单文件发布配置。
- **窗口状态记忆**：记忆窗口位置、大小、最大化状态与常用页面配置。
- **安全存储**：代理密码使用 Windows DPAPI 保护，设置保存至 `%AppData%/KemonoDownloader/settings.json`。

### 变更 (Changed)

- **技术栈迁移**：Electron + Node.js → .NET 10 + WPF，性能更好、体积更小。
- **架构分层**：重构为三层结构——
  - `KemonoDownloader.Core`：接口、数据模型与 Kemono 规则；
  - `KemonoDownloader.Infrastructure`：文件下载器、API 客户端、网络提供器、爬取/修复服务、进度与设置存储、SOCKS5 代理、DPAPI 加密；
  - `KemonoDownloader.Wpf`：基于 MVVM 的深色主题桌面界面。
- **重试机制优化**：默认最大尝试次数由 5 次调整为 3 次（仍可设置 1–20 次）；附件下载采用响应头超时、连续无数据超时、低速窗口与动态总时长上限，大文件持续传输时不再受旧固定总时长限制。
- **断点续传优化**：使用 `.part` 与 `.part.meta.json` 保存断点、长度、ETag 和 Last-Modified；支持重试及软件重启后的 HTTP Range 续传；自动处理服务器忽略 Range、验证器变化、416 与响应长度不完整等异常情况；404/410/401/403、HTML/JSON 错误页与零长度响应快速失败；连续两次无新增数据时提前终止。

### 移除 (Removed)

- 移除全部 Electron 运行时文件（`app.js`、`main.js`、`preload.js`、`scraper.js`、`index.html`、`styles.css`、`package.json`、`package-lock.json`，约 9,400 行）。
- 不再提供 Electron、Node.js、macOS 或 Linux 并行构建入口。

### 兼容性说明

- 目标平台：Windows 10 及以上（x64）；发布包为自包含单文件，无需预装 .NET Desktop Runtime。
- 进度文件升级为 `schema_version: 2`；旧 Electron 进度格式不会被静默覆盖，会显示不支持提示，请使用新版重新建立任务。
- 旧设置中的 5 次重试、视频 1200 秒默认值会自动迁移为 3 次和 120 秒。
- 已生成的 `user_<作者ID>` 目录在目标名称可用时自动迁移。

### 发布文件

```text
artifacts/publish/win-x64/KemonoDownloader.exe
```
