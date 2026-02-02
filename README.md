# Kemono 下载器 - fengmic

<div align="center">

![Version](https://img.shields.io/badge/version-2.0.0-blue.svg)
![License](https://img.shields.io/badge/license-MIT-green.svg)
![Electron](https://img.shields.io/badge/Electron-28.0-47848F.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey.svg)

一个基于 Electron 的 Kemono 下载器桌面应用，采用纯前端技术实现，只需node环境。

[功能特性](#-功能特性) • [快速开始](#-快速开始) • [构建打包](#-构建打包) • [技术栈](#-技术栈)

</div>

---

## ✨ 功能特性

- 🚀 **纯前端实现** - 所有爬虫逻辑使用 JavaScript 实现，无需 Python 运行时
- 💻 **跨平台支持** - 基于 Electron，支持 Windows、macOS 和 Linux
- 🎨 **现代化 UI** - 现代化的用户界面，实时进度显示
- ⚡ **并发下载** - 可配置 1-10 个并发任务，提升下载效率
- 📊 **进度追踪** - 实时显示下载进度、文件统计和任务状态
- 💾 **断点续传** - 自动保存进度，支持任务中断后继续
- 🔒 **反爬虫绕过** - 内置反爬虫策略，自动处理访问限制
- 🪟 **窗口记忆** - 自动记住窗口位置和大小
- 🎯 **智能跳过** - 可选跳过已存在的文件，避免重复下载
！！下载失败请自行解决网络代理问题！！

<div align="center">

### 界面预览
![主界面](assets/screenshot.png)


</div>

## 🚀 快速开始

### 方式一：直接下载（推荐）

**Windows 用户可直接下载编译好的程序：**

📥 [前往 Releases 下载最新版本](https://github.com/fengmic/Kemono-spider/releases/)

下载后解压即可使用，无需安装任何依赖！

---

### 方式二：从源码运行

#### 前置要求

- Node.js 18.0 或更高版本
- npm 或 yarn 包管理器

#### 安装

```bash
# 克隆仓库
git clone https://github.com/fengmic/Kemono-spider.git

# 进入项目目录
cd Kemono-spider

# 安装依赖
npm install
```

#### 运行

```bash
# 开发模式启动
npm start
```

---

## 🔨 从源码构建打包

### Windows

```bash
# 打包为便携版
npm run pack

# 打包为安装程序
npm run build:win
```

输出文件位于 `dist/` 目录：
- `win-unpacked/` - 便携版（绿色版）
- `Kemono下载器-fengmic Setup 2.0.0.exe` - 安装程序


### macOS

```bash
npm run build
```

### Linux

```bash
npm run build
```

## 📖 使用说明

### 如何获取作者ID？

1. 访问 Kemono 网站并进入目标作者的主页
2. 查看浏览器地址栏的 URL，格式如下：
   ```
   https://kemono.cr/fanbox/user/59336265
                    ^^^^^^      ^^^^^^^^
                    平台名      作者ID
   ```
3. 其中：
   - `fanbox` 是所在平台（如 fanbox、patreon、fanbox 等）
   - `59336265` 就是作者的 ID

### 下载步骤

1. **输入作者信息**
   - 在"作者ID"框中输入上面获取的数字ID（如：`59336265`）

2. **选择保存路径**
   - 点击 📁 按钮选择文件保存目录

3. **配置参数**
   - **限制数量**: 下载的帖子数量（默认 50，0为不限制即全部下载）
   - **并发数**: 同时下载的文件数（1-10，默认 5）
   - **跳过已存在**: 勾选后会跳过已下载的文件

4. **开始爬取**
   - 点击"🚀 开始爬取"按钮启动任务
   - 可以点击"⏸ 停止任务"中断下载
   - 使用"📂 续传"按钮从进度文件恢复任务

## 🛠 技术栈

### 前端框架
- **Electron** - 跨平台桌面应用框架
- **HTML5/CSS3** - 界面结构和样式
- **Vanilla JavaScript** - 无框架依赖的纯 JS 实现

### 核心技术
- **Node.js** - JavaScript 运行时
- **IPC通信** - 主进程与渲染进程的安全通信
- **文件系统** - fs 模块处理文件操作
- **HTTP/HTTPS** - 网络请求和文件下载

### 打包工具
- **electron-builder** - 应用打包和分发

## 📁 项目结构

```
kemono-downloader/
├── main.js           # Electron 主进程
├── preload.js        # 预加载脚本（IPC 桥接）
├── index.html        # 主界面 HTML
├── styles.css        # 样式表（现代化暗色主题）
├── app.js            # UI 控制逻辑
├── scraper.js        # 下载核心逻辑
├── package.json      # 项目配置
└── assets/           # 资源文件（图标等）
```

## 🔧 开发

### 调试模式

在 `main.js` 中取消注释以下代码启用开发者工具：

```javascript
mainWindow.webContents.openDevTools();
```

### 添加功能

1. **修改 UI**: 编辑 `index.html` 和 `styles.css`
2. **修改下载逻辑**: 编辑 `scraper.js`
3. **修改 UI 交互**: 编辑 `app.js`
4. **添加 IPC 通道**: 在 `main.js` 和 `preload.js` 中添加

## 🤝 贡献

欢迎贡献BUG 和功能需求！

请提交ISSUE 或提交PR。



## ⚠️ 免责声明

本项目仅供学习和研究使用，请勿用于非法用途。使用本工具下载内容时，请遵守相关网站的服务条款和版权法律。

## 📝 许可证

本项目采用 MIT 许可证 - 详见 [LICENSE](LICENSE) 文件

## 🙏 致谢

- [Electron](https://www.electronjs.org/) - 跨平台桌面应用框架
- [electron-builder](https://www.electron.build/) - 应用打包工具

---

<div align="center">

**如果这个项目对你有帮助，请给一个 ⭐️ Star 支持一下！**

Made with ❤️ by fengmic

</div>
