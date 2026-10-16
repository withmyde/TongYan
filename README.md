# 通言 (TongYan)

<p align="center">
  <img src="app.png" width="96" height="96" alt="通言" />
</p>

<p align="center">
  轻量级原生 Windows 桌面划词 AI 助手，支持接入任意兼容 OpenAI 接口的大模型。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/Version-1.0.0-2ea44f" alt="Version" />
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET%20Framework-4.x-512BD4" alt=".NET Framework" />
  <img src="https://img.shields.io/badge/License-MIT-blue" alt="License" />
</p>

---

## 功能简介

**通言 (TongYan)** 是一款常驻系统托盘的桌面划词工具。在任意窗口选中文字并按下全局快捷键，即可在光标旁弹出悬浮卡片并流式输出模型回复。

- **通用接口兼容**：支持填写任意兼容 OpenAI 规范的 `API URL` 与 `API Key`，支持在线拉取模型列表。
- **思考模式开关**：支持在设置窗口或系统托盘右键菜单一键开启或关闭思考模式。
- **多套提示词预设**：内置默认翻译提示词，支持新建、重命名、删除自定义提示词，并可在系统托盘菜单随时切换。
- **原生轻量单文件**：基于 Windows 系统自带的 .NET Framework 4.x 与 Win32 API 编写，无第三方依赖库，点击外部区域或按 `ESC` 键即刻关闭悬浮窗。

## 快速开始

1. 运行 `build.bat` 编译，启动后程序将驻留于系统右下角托盘，首次运行会自动弹出设置窗口。
2. 在设置窗口填写 **接口地址** 与 **API Key**，点击 **「获取模型」** 并选择模型。
3. 按需调整提示词预设与全局快捷键，点击 **「保存」**。
4. 在任意应用中用鼠标选中文字并按下快捷键即可触发；点击窗口外部或按 `ESC` 关闭结果窗。

## 从源码构建

Windows 10 / 11 系统已内置 C# 编译器 `csc.exe`，无需安装 Visual Studio 即可直接编译：

```bat
:: 方式一：双击或命令行运行批处理脚本
build.bat

:: 方式二：使用 Node.js 编译脚本
node build.js
```

编译完成后将在项目根目录生成单文件可执行程序 `TongYan.exe`。

## 目录结构

```text
TongYan/
├── src/
│   ├── Program.cs          # 程序入口、系统托盘菜单、开机自启与单实例控制
│   ├── Config.cs           # 本地配置文件读写与提示词预设数据结构
│   ├── AiClient.cs         # OpenAI 兼容流式请求客户端与模型列表拉取
│   ├── ClipboardHelper.cs  # Win32 剪贴板序列号无锁监听与选中文本提取
│   ├── HotKey.cs           # Win32 全局热键注册与消息监听
│   ├── ResultForm.cs       # 双缓冲自绘悬浮结果窗口与拖拽缩放交互
│   ├── SettingsForm.cs     # 设置窗口界面
│   ├── StaWorker.cs        # 后台 STA 线程调度器
│   ├── MemoryManager.cs    # 闲置工作集内存回收
│   └── Logger.cs           # 本地运行日志记录
├── app.ico                 # 应用程序图标资源
├── app.png                 # 项目图标预览图
├── build.bat               # Windows 批处理编译脚本
├── build.js                # Node.js 编译脚本
└── LICENSE                 # MIT 开源许可证
```

## 开源许可

本项目采用 [MIT License](LICENSE) 开源许可。
