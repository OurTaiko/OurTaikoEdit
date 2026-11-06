# CharTaiko

跨平台（Avalonia）太鼓谱面 IDE。第一版支持 TJA：左边写代码，右上是轨道预览，右下是可编辑的时间轴（参考 MiaCode 与 PeepoDrumKit）。解析用 [TJAParser](https://github.com/OurTaiko/TJAParser)——游戏与两站用的同一份解析器，所以编辑器里看到的就是游戏里玩到的。

## 构建与运行

仓库：<https://github.com/OurTaiko/OurTaikoEdit>（私有）。推送 main 或开 PR 时 GitHub Actions 跑全部测试（含 Headless UI 测试，截图作为构件上传）。


```bash
git submodule update --init
dotnet run --project src/CharTaiko.App
dotnet test CharTaiko.slnx
```

需要 .NET 10 SDK。UI 测试（`tests/CharTaiko.UiTests`）用 Avalonia Headless + Skia 渲染真实窗口，截图存到 `$CHARTAIKO_SCREENSHOTS`（默认在测试程序目录的 `screenshots/`）。

## 目录

```
external/TJAParser/      子模块，指 TJAParser 的 tag（现在 v0.12.0）
src/CharTaiko.Core/      项目模型、项目列表、谱面语言扩展点（IChartLanguage）与语言无关的分析模型，界面文字表（Localization）与设置（Settings）
src/CharTaiko.Tja/       TJA 语言：分析、诊断与补全都用 TJAParser 的编辑器接口（TjaDocument、TjaAnalysis、TjaCompletion），高亮（Tja.xshd），时间轴编辑转文本编辑
src/CharTaiko.Audio/     歌曲音频：解码 OGG（NVorbis）／MP3（NLayer）／WAV，波形，OpenAL Soft 播放，游戏的 21 套打击音（HitSounds/）
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

- **代码**（AvaloniaEdit）：TJA 高亮；补全来自库的 `TjaCompletion`（头部、取值、指令、分歧条件、音符；输入时弹出，⌘/Ctrl+Space 手动）；诊断来自 `TjaDocument.Diagnostics`（解析器错误与警告 TJA1xxx／2xxx，加编辑器检查 TJA3xxx），画波浪线并列在 Problems 里，点击跳到行。解析出错后库会继续读，预览照常显示，但时间轴在该块有错误时拒绝编辑。
- **难度标签**：文件里每个 `#START` 块一个标签；光标进入哪个块就切到哪个。
- **轨道预览**：按 OurTaikoPlay 的轨道版式画（Nijiiro 1920 宽舞台按面板缩放：轨道 200 高、判定点在轨道左起 120、下方音符文字条），音符位置用 TJAParser 的 `NoteScroll`／`ScrollClockTimeline`（v0.11.0 起从游戏移入库，游戏、View_Web 与 CharTaiko 共用），所以普通／BM／HB 滚动、`#SUDDEN`、连打作为刚体、`#JPOSSCROLL` 移动判定点都与游戏一致；小节线 3 宽（分歧起点 6 宽金色）；分歧决定后轨道按路线着色（玄人蓝、達人紫），go-go 时轨道变红，`#LANE_ALPHA` 调轨道透明度；音符下方显示音符文字（ドン、カッ、連打…ーっ!! 等，取自解析器的 `NoteMoji`）。音符、连打身体与尾巴、气球、音符文字都用游戏的贴图（`Assets/Game/`，取自 OurTaikoPlay，见其中的 PROVENANCE.md），按游戏的切片与摆法画（`Controls/GameSprites.cs`）；表情帧固定为第一帧（游戏里 50 连击后才随拍子换）。Ad-lib 游戏里不画，这里画淡圈方便找。点音符跳到代码。
- **时间轴**：每条路线一行（无分歧时一行），小节按 Grid（1/4–1/192，以 4/4 小节计）切分。
  - 音符工具：单击放置，连打／气球拖到终点，右键删除。
  - Select 工具（Q）：单击选中并在代码里定位，Shift 或 ⌘/Ctrl 单击加减选，空白处拖框选择；拖动选中的音符按网格整格移动（连打连同终点）；Delete 删除，Esc 取消选择，⌘/Ctrl+A 全选，⌘/Ctrl+C／X／V 复制、剪切、粘贴（粘贴到指针所在格，指针不在行上时到播放头）；←／→ 有选择时移动一格、没有时播放头走一格；有选择时按音符键（1–9、G、C、F）改种类（短音符之间、连打／气球之间）；右键菜单：删除、改为…、咚咔互换、剪切、复制、在此粘贴、全选。
  - 拖连打／气球的尾端（Select 下光标变为左右箭头）改长度。
  - Commands 行：块里的每条指令画成小刻度加标签（BPM 数值、×SCROLL、拍号、+DELAY、GO▸／◂GO、|off／|on，其余为指令名），另有 go-go 区间与开头的 BPM；拖动指令按网格移动（`#MEASURE` 只落在小节开头），双击改值，右键插入 `#BPMCHANGE`／`#SCROLL`／`#MEASURE`／`#DELAY`／`#GOGOSTART`／`#GOGOEND`／`#BARLINEOFF`／`#BARLINEON`，或改值、删除、在代码里显示附近的指令。
  - 点或拖标尺跳转播放头；滚轮横移，⌘/Ctrl + 滚轮缩放；空格播放。
- **代码辅助**（都来自 TJAParser）：悬停提示（含该处诊断）、写指令参数时的参数提示、块／分歧／路线折叠、行尾的小节号·时刻·BPM 提示、右键菜单或 ⌘/Ctrl+. 的快速修复与重构（拆分／合并小节、气球打数写成内联），工具栏 Format（Shift+Alt+F，保证每块哈希不变）；轨道下方显示所选块的音符数、最大连击、时长、峰值密度与哈希；诊断消息可选英／中／日文（默认与界面相同）。
- **播放**：读 `WAVE:` 指定的音频（OGG、MP3、WAV），后台解码，时间轴行背后画波形；播放头跟随歌曲位置（`OFFSET` 已含在音符时刻里，所以波形与音符直接对齐），播放头在 0 之前或歌曲之后用时钟走；可调音量，`Hits` 勾选时音符经过判定点播放游戏的打击音（设置里可在游戏的 21 套音色中选，默认「太鼓」；文件缺失时退回合成音）；设置里的「音频延迟」让歌曲比播放头提前相应毫秒播放、打击音提前发声，用来抵消设备输出延迟。没有音频设备时退回无声时钟。代码里正在演奏的字符会被框出并跟随滚动。
- 撤销覆盖时间轴编辑（一次放置是一次撤销）。读取时按 BOM、严格 UTF-8、再 Shift-JIS 的顺序判断编码，状态栏点编码可改用 Shift-JIS、GB18030、Big5、EUC-KR、UTF-16 重新读取；**保存一律写 UTF-8（无 BOM）**，因为游戏与两站只认 UTF-8——用其他编码读入的文件打开时就标为未保存，状态栏显示「SHIFT_JIS → UTF-8」。

## 界面语言与设置

- 界面有英文、简体中文、日文，默认跟随系统语言（其他语言用英文）。文字表在 `src/CharTaiko.Core/Localization/Strings.cs`，三种语言并排写；单元测试检查每条都有三种语言、占位符一致。XAML 里写 `{l:T 键}`，切换语言即时生效；代码里用 `Loc.T`／`Loc.F`／`Loc.Plural`。
- 诊断、悬停说明、补全说明、参数提示、修复与重构的标题、轨道下方的摘要来自 TJAParser（v0.10.0 起三种语言），语言由设置里的「谱面提示的语言」决定（默认与界面相同）。
- 中日文字体：先找 PingFang SC／微软雅黑／Noto Sans CJK 等（界面为日文时先找 Hiragino／Yu Gothic），避免同一行里字重不一；顺序在启动时按界面语言定，切换语言后重启才换。
- 设置（Projects 场景右上或编辑场景工具栏的「设置…」）存在数据目录的 `settings.json`：界面语言、诊断消息语言、新项目的默认位置、代码字号、行尾提示、自动换行、打开项目时的网格、歌曲音量、打击音开关与音量、音频延迟（0–500 ms）；另记窗口大小。工具栏上改的音量与打击音开关也会存下来。文件读不了或数值越界时退回默认值。

### 时间轴编辑如何写回文本

代码是唯一的数据源：时间轴编辑先变成文本编辑，再由解析器重读。

- 落在已有字符格上就替换那一格；不在格上时把整小节每一格等分（`1020,` → `10002000,`），指令与换行原地不动；单小节最多 384 格。
- 放连打／气球会写 `8`，并清掉被它覆盖的音符；在连打中间放音符会被拒绝（否则连打提前结束）；删除或替换连打头时一并清掉它的 `8`。
- 气球数列表（`BALLOON`、`BALLOONNOR/EXP/MAS`）随气球的增删保持每个剩余气球的打数，新气球为 5；列表写在第一个 `COURSE:` 之前（各难度共用）时，给本难度另写一行而不改共用的。
- 多个音符的移动、粘贴、改种类是一次「先删后放」：同一位置上放置优先于删除，所以音符可以移到彼此原来的位置；新连打覆盖的旧音符会被清掉，但同一批放进来的音符若落在新连打中间则拒绝。
- 指令写在它生效的字符格前：该格在行首时另起一行写在上面，否则在该处断行；不在格上时先等分小节。`#MEASURE` 只能写在小节开头。删除指令时，指令独占一行就连行删掉。
- 移动指令是「删掉原行＋写到新位置」的一次修改。指令的生效时刻是它之后的第一格的时刻；写在分歧标记之前的算共通。
- 字符格的时刻按小节内的 BPM 与 `#DELAY` 逐格推算（有音符的格直接用音符时刻），所以小节中途变速时网格也对。
- 跨分歧边界的连打、`#MEASURE` 小节中途改变等情况请在代码里写。

## 扩展新的谱面格式

实现 `IChartLanguage`（分析、补全、高亮、`IChartEditing`），在 `App.OnFrameworkInitializationCompleted` 里 `ChartLanguages.Register`，`chartaiko.json` 的 `chartType` 用它的 `Id`。预览与时间轴读的是语言无关的 `ChartAnalysis`（`TaikoChart` + 小节／字符格的源码位置）。

## 许可

GPL-3.0-or-later（与 TJAParser 相同）。
