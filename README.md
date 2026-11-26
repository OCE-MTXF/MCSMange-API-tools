# MCSM Server Manager

一个基于 [MCSManager](https://github.com/MCSManager/MCSManager) 官方 API 的 Windows 桌面服务器管理客户端。

- 技术栈：.NET 9 + WPF（C#），界面语言为简体中文
- 交付形式：自包含单文件程序 + 自解压安装程序 `MCSMServerManager_Setup.exe`
- 界面：Codex 风格侧边栏 + 复刻 MCSManager 网页版的实例卡片布局
- 版本：1.1.0

## 功能

| 页面 | 能力 |
| --- | --- |
| 概览 | 面板版本、在线节点、系统 CPU / 内存、节点清单、最近日志 |
| 实例 | 网页版卡片布局：状态徽标、实例类型中文名、上次启动时间、玩家数；启动 / 停止 / 重启 / 查看日志 / 管理文件 / 删除；分页、批量操作、全部停止、控制台命令 |
| 节点 | 列表、刷新、添加节点、连接（握手）节点、删除节点 |
| 用户 | 按用户名分页搜索，查看权限、实例数、两步验证、注册与登录时间 |
| 文件管理 | 分页浏览、新建文件夹 / 文件、在线编辑保存、重命名、复制、打包 / 解压、URL 下载、多选删除、查看原始响应 |
| 运行日志 | 查看程序自身的完整日志，支持刷新、复制、打开日志目录 |
| 数据监控 | 面板与节点的 CPU / 内存实时数据，定时自动刷新（支持秒 / 分 / 时及自定义周期） |

## 安装目录中的文件

安装完成后，安装目录（`%LOCALAPPDATA%\Programs\MCSMServerManager`）包含：

| 文件 / 目录 | 说明 |
| --- | --- |
| `MCSMServerManager.exe` | 程序本体 |
| `config.json` | 配置文件，可手工编辑或用「设置」页修改 |
| `logs\` | 日志目录，按天分文件 `app-yyyy-MM-dd.log` |
| `LICENSE` | Apache License 2.0 全文 |
| `NOTICE` | 署名与鸣谢声明 |
| `Uninstall.cmd` | 卸载入口 |

## 配置文件（config.json）

首次启动会自动生成。所有项都可以在「设置」页里改，也可以直接编辑本文件。

| 键 | 说明 |
| --- | --- |
| `SavePanelUrl` | 是否记住面板地址 |
| `SaveApiKey` | **是否记住 API Key**（经 Windows DPAPI 加密后写入配置文件），默认关闭 |
| `LastPanelUrl` | 记住的面板地址（关闭「记住面板地址」时不会被写入） |
| `LastApiKeyProtected` | DPAPI 加密后的 API Key（Base64）。只有保存它的那个 Windows 用户能解密；换了用户或系统账户被重置后需要重新填写 |
| `IgnoreCertificateErrors` | 是否忽略 HTTPS 证书错误（不安全） |
| `AutoConnectOnStart` | 启动时自动连接 |
| `LogEnabled` / `LogLevel` / `LogHttpBody` | 日志开关、级别（调试 / 信息 / 警告 / 错误）、是否记录完整响应体 |
| `LogMaxSizeMb` / `LogKeepFiles` | 单个日志文件上限与保留数量（超出自动轮转） |
| `PerformanceMonitorEnabled` | 是否开启定时性能刷新 |
| `PerformanceIntervalValue` / `PerformanceIntervalUnit` | 刷新周期数值与单位（`秒` / `分` / `时`），如 `5` + `分` 即每 5 分钟 |
| `ShowErrorDetailDialog` | 接口出错时是否弹出可展开详情的错误弹窗 |
| `InstancePageSize` / `FilePageSize` / `LogViewerLines` | 各列表每页条数与日志查看行数 |

> 配置文件损坏时程序会回退到默认值并在启动时提示，不会崩溃。

## 日志

启动、连接、用户操作、每一次接口请求与响应、异常都会写入 `logs\` 下的日志文件。
**API Key 在写入前已脱敏**，日志与界面上只会出现 `***`。
日志页与概览页的「最近日志」会实时刷新。

## 错误详情弹窗

接口出错时会弹出一个可展开详情的错误弹窗：先给一句话结论，再按需展开：

- 该次请求的方法、路径、HTTP 状态与耗时
- 对返回 JSON 的**初步解析结论**（识别 `{ status, data, time }` 信封，说明 data 类型与条目数）
- 完整格式化后的响应 JSON，可复制

文件管理页的「查看原始响应」按钮可随时回看最近一次接口返回。

## 安全相关行为

- **凭据走请求头**。API Key 通过 `x-request-api-key` 头发送（与 MCSManager 权限中间件的读取顺序一致），不会进入面板侧的 Nginx / 反向代理访问日志。
- **旧版面板回退会暴露密钥，请注意**。若面板不识别该请求头而返回 403，程序会自动回退到 `?apikey=` 查询串重试一次以兼容旧版面板。此时密钥位于 URL 查询串中，**可能被面板前置的 Nginx / 反向代理写入访问日志**，也可能出现在浏览器历史、代理缓存等位置。程序在回退发生时会在界面提示一次并写入日志，并确保日志中的该参数被替换为 `***`。若你的面板版本支持请求头（MCSManager 10.x 均支持），可忽略此条；如需彻底避免该风险，请升级面板。
- **默认校验 HTTPS 证书**。证书无效时连接直接失败；仅当面板使用自签名证书时，才需要勾选连接栏的「忽略证书错误（不安全）」，勾选后状态栏会持续显示「证书未校验」。
- **在线编辑有白名单**。只有文本类扩展名可打开编辑器，单文件上限 2 MB，命中二进制特征（NUL 或 U+FFFD）会被拒绝，避免把二进制文件按 UTF-8 回写而损坏。
- **API Key 需在面板中开启**。MCSManager 的 `enableApiKey` 默认为关闭，需在「系统设置」中开启，否则接口返回 403。

## 对照官方 API 的接口覆盖

所有请求都遵循 MCSManager 的约定：统一响应信封 `{ "status": 200, "data": ..., "time": ... }`，`status == 200` 视为成功；必需请求头 `X-Requested-With: XMLHttpRequest` 与 `Content-Type: application/json; charset=utf-8`。

| 界面功能 | 方法 | 端点 |
| --- | --- | --- |
| 面板概览 / 数据监控 | GET | `/api/overview` |
| 节点列表 | GET | `/api/service/remote_services_list` |
| 节点（含实例概况） | GET | `/api/service/remote_services` |
| 添加节点 | POST | `/api/service/remote_service` |
| 删除节点 | DELETE | `/api/service/remote_service` |
| 连接节点 | GET | `/api/service/link_remote_service` |
| 实例列表 | GET | `/api/service/remote_service_instances` |
| 实例详情 | GET | `/api/instance` |
| 删除实例 | DELETE | `/api/instance` |
| 启动 / 停止 / 重启 / 强制结束 | GET | `/api/protected_instance/{open,stop,restart,kill}` |
| 发送控制台命令 | GET | `/api/protected_instance/command` |
| 读取控制台日志 | GET | `/api/protected_instance/outputlog` |
| 列出文件 | GET | `/api/files/list` |
| 读取 / 写入文件 | PUT | `/api/files/` |
| 删除文件 / 目录 | DELETE | `/api/files` |
| 新建目录 / 新建文件 | POST | `/api/files/{mkdir,touch}` |
| 复制文件 | POST | `/api/files/copy` |
| 移动 / 重命名 | PUT | `/api/files/move` |
| 打包 / 解压 | POST | `/api/files/compress`（`type=1` / `type=2`） |
| 从 URL 下载 | POST | `/api/files/download_from_url` |
| 搜索用户 | GET | `/api/auth/search` |

> 登录、创建 / 删除用户、取 token、退出登录等接口已在 `McsmApi` 中封装但当前界面未接入。

### 一个容易踩的坑

`/api/files/list` 的 `file_name` 参数**必须存在**，即使是空串。省略时面板会返回
`{"items":[],"total":0}` 且 HTTP 200 —— 静默空列表，不报任何错。程序因此始终传空串而非省略该参数。

## 开源协议与鸣谢

本软件采用 **Apache License 2.0** 发布，`LICENSE` 与 `NOTICE` 随安装包分发。

- **MCSManager** —— 提供了本软件依赖的全部接口与文档，Apache-2.0 协议，© MCSManager 开发团队
- **.NET 9 / WPF** —— 界面框架与运行时，MIT 协议，© .NET Foundation and Contributors
- **界面参考** —— 布局与交互参考了 MCSManager 网页版面板
- **PCL（Plain Craft Launcher）** —— 可展开的错误弹窗交互思路参考自 PCL

本软件为第三方客户端，与 MCSManager 官方无隶属关系。

### 关于 PCL 的许可说明

PCL 由成都瓜皮龙科技有限公司（Meloong）发布，其许可为 **《PCL 分发有限许可》**，
**并非开源许可**；其源码仓库另受《PCL 存储库合理使用指南》
（<https://shimo.im/docs/rGrd8pY8xWkt6ryW>）约束。

依该指南，本软件属于**轻度使用**：仅参考了极小部分内容，且本软件的实质功能
（通过 MCSManager 接口管理游戏服务器）与 PCL 的实质功能（启动 Minecraft）**不存在任何重叠**。

轻度使用对使用者提出两项要求，本软件均已履行：

1. **以适当的形式给出署名** —— 即上方与「关于」窗口中对 PCL 的致谢。
2. **不得将本软件与 PCL 混淆，或暗示本软件与 PCL 有关** —— 本软件不使用
   `Plain Craft Launcher`、`PCL`、`Potato Craft Launcher` 等名称，不使用 PCL 的任何
   图标与品牌标识，也不是 PCL 的分支、魔改或二次创作产物。

> 若日后本软件被认定构成**重度使用**（例如基于 PCL 进行修改，或重新实现其实质功能），
> 将优先履行该类别义务，包括必须以 `Plain Craft Launcher (PCL)` 开头命名、
> 明确标注为第三方二次创作、附上源码链接并采用 CC BY-SA 4.0 等要求。

### 安装包未做代码签名

本项目**没有可用的代码签名证书**，因此 `MCSMServerManager_Setup.exe` 与
`MCSMServerManager.exe` 均**未做 Authenticode 签名**，Windows SmartScreen 首次运行时
可能显示「已阻止」或「未知发布者」类提示。

获取安装包后请自行校验完整性：

```powershell
Get-FileHash .\MCSMServerManager_Setup.exe -Algorithm SHA256
```

构建脚本 `installer\build-installer.ps1` 在打包完成后会输出该安装包的 SHA256，
请与发布渠道公布的校验值比对一致后再运行。自包含发布目录下的
`MCSMServerManager.exe` 也可用同样方式校验。

## 从源码构建

需要 .NET 9 SDK（`dotnet --version` 为 9.x；更高的 SDK 版本也可以，但必须能还原 `net9.0-windows` 目标包）。
仓库根目录就是工程目录，没有嵌套子目录。

```powershell
# 1. 仅编译（验证改动）
dotnet build .\MCSMServerManager.csproj -c Release

# 2. 生成自包含单文件程序，输出 .\publish-single\MCSMServerManager.exe
dotnet publish .\MCSMServerManager.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -o publish-single

# 3. 生成自解压安装程序，输出 .\output\MCSMServerManager_Setup.exe
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

第 3 步依赖系统的 `iexpress.exe`，因此**生成安装程序只能在 Windows 上完成**；脚本会重新生成
`setup.sed`、刷新 `stage\` 下的载荷（程序本体 + `install.cmd` + `install.ps1` + `LICENSE` + `NOTICE`），
并在结束时打印安装包的 SHA256。

> `bin/`、`obj/`、`publish/`、`publish-single/`、`stage/`、`output/`、`setup.sed` 都是构建产物，
> 已在 `.gitignore` 中排除，不会进入版本库。

## 安装与卸载

- 安装：运行 `MCSMServerManager_Setup.exe`，默认装到 `%LOCALAPPDATA%\Programs\MCSMServerManager`，
  并创建桌面与开始菜单快捷方式、「程序和功能」中的卸载项。
- 卸载：运行安装目录下的 `Uninstall.cmd`。
- **安装与卸载都不会静默强杀正在运行的程序**。若检测到程序仍在运行，安装器与
  卸载器都会提示先手动关闭并最多等待 60 秒；若超时仍未关闭，则**中止并保留全部文件**，
  以免丢失尚未保存的文件编辑内容。
- 安装包与 `Uninstall.cmd` 均以 UTF-8 with BOM 写入，因此在中文等非 ASCII 安装路径下
  可正常工作。