# CharTaiko

跨平台（Avalonia）太鼓谱面 IDE。第一版支持 TJA：左边写代码，右上是轨道预览，右下是可编辑的时间轴（参考 MiaCode 与 PeepoDrumKit）。解析用 [TJAParser](https://github.com/OurTaiko/TJAParser)——游戏与两站用的同一份解析器，所以编辑器里看到的就是游戏里玩到的。

## 构建与运行

```bash
git submodule update --init
dotnet run --project src/CharTaiko.App
dotnet test CharTaiko.slnx
```

需要 .NET 10 SDK。UI 测试（`tests/CharTaiko.UiTests`）用 Avalonia Headless + Skia 渲染真实窗口，截图存到 `$CHARTAIKO_SCREENSHOTS`（默认在测试程序目录的 `screenshots/`）。

## 目录

```
external/TJAParser/      子模块，按 tag 锁定
src/CharTaiko.Core/      项目模型、项目列表、谱面语言扩展点（IChartLanguage）与语言无关的分析模型
src/CharTaiko.Tja/       TJA 语言：分析（TJAParser + TjaSourceMap）、补全、高亮（Tja.xshd）、时间轴编辑转文本编辑
src/CharTaiko.App/       Avalonia 应用：Projects 与编辑两个场景
tests/CharTaiko.Tests/   语言与项目的单元测试
tests/CharTaiko.UiTests/ Headless UI 测试与截图
```

## 项目

项目是一个文件夹：谱面文件、音频，以及 `chartaiko.json`（`name`、`chartType`、`chart`、`created`、`lastBlock`）。应用在数据目录（macOS `~/Library/Application Support/CharTaiko/projects.json`）里记录跟踪的项目文件夹。

- **New Project**：在所选位置建文件夹，按语言模板写谱面，可选把音频复制进来并写入 `WAVE:`。
- **Open Folder**：有 `chartaiko.json` 就读它；没有就取文件夹里第一个已知语言的谱面文件，生成一个。
- 从列表移除只是不再跟踪，文件不动。

## 编辑场景

- **代码**（AvaloniaEdit）：TJA 高亮；块外补全头部字段，块内补全 `#` 指令，`COURSE:`／`STYLE:` 补全取值（输入时弹出，⌘/Ctrl+Space 手动）；错误与警告画波浪线并列在 Problems 里，点击跳到行。
- **难度标签**：文件里每个 `#START` 块一个标签；光标进入哪个块就切到哪个。
- **轨道预览**：在播放头处按每个音符自己的 BPM × SCROLL 滚动，含 go-go、`#LANE_ALPHA`、分歧路线（Route 选择）、连打与气球；点音符跳到代码。
- **时间轴**：每条路线一行（无分歧时一行），小节按 Grid（1/4–1/192，以 4/4 小节计）切分；选音符工具后单击放置，连打／气球拖到终点，右键删除；Select 工具单击音符跳到代码；点或拖标尺跳转播放头；滚轮横移，⌘/Ctrl + 滚轮缩放；1–9、G、C、F、Q 选工具，空格播放。
- **播放**：无声时钟（第一版没有音频），代码里正在演奏的字符会被框出并跟随滚动。
- 撤销覆盖时间轴编辑（一次放置是一次撤销）。保存保持原编码（UTF-8 / Shift-JIS，BOM 原样）。

### 时间轴编辑如何写回文本

代码是唯一的数据源：时间轴编辑先变成文本编辑，再由解析器重读。

- 落在已有字符格上就替换那一格；不在格上时把整小节每一格等分（`1020,` → `10002000,`），指令与换行原地不动；单小节最多 384 格。
- 放连打／气球会写 `8`，并清掉被它覆盖的音符；在连打中间放音符会被拒绝（否则连打提前结束）；删除或替换连打头时一并清掉它的 `8`。
- 气球数列表（`BALLOON`、`BALLOONNOR/EXP/MAS`）随气球的增删保持每个剩余气球的打数，新气球为 5；列表写在第一个 `COURSE:` 之前（各难度共用）时，给本难度另写一行而不改共用的。
- 跨分歧边界的连打、`#MEASURE` 小节中途改变等情况请在代码里写。

## 扩展新的谱面格式

实现 `IChartLanguage`（分析、补全、高亮、`IChartEditing`），在 `App.OnFrameworkInitializationCompleted` 里 `ChartLanguages.Register`，`chartaiko.json` 的 `chartType` 用它的 `Id`。预览与时间轴读的是语言无关的 `ChartAnalysis`（`TaikoChart` + 小节／字符格的源码位置）。

## 许可

GPL-3.0-or-later（与 TJAParser 相同）。
