# OurTaikoEdit

跨平台（Avalonia）太鼓谱面 IDE。第一版支持 TJA：左边写代码，右上是轨道预览，右下是可编辑的时间轴（参考 MiaCode 与 PeepoDrumKit）。解析用 [TJAParser](https://github.com/OurTaiko/TJAParser)——游戏与两站用的同一份解析器，所以编辑器里看到的就是游戏里玩到的。

## 构建与运行

仓库：<https://github.com/OurTaiko/OurTaikoEdit>（私有）。开 PR 时 `ci.yml` 跑全部测试（含 Headless UI 测试，截图作为构件上传）；推送 main 或 `v*` 标签时 `build.yml` 先跑同一份测试再打包发布（见下文「发布」）。


```bash
git submodule update --init
dotnet run --project src/OurTaikoEdit.App
dotnet test OurTaikoEdit.slnx
```

需要 .NET 10 SDK。UI 测试（`tests/OurTaikoEdit.UiTests`）用 Avalonia Headless + Skia 渲染真实窗口，截图存到 `$OURTAIKOEDIT_SCREENSHOTS`（默认在测试程序目录的 `screenshots/`）。

## 发布

`build.yml`（Build 工作流）照 OurTaikoPlay 的做法：

- 版本号是 `版本-提交数`（例如 `0.1.0-22`）。版本取 `Directory.Build.props` 的 `<Version>`，推 `vX.Y.Z` 标签时取标签。
- macOS（Apple Silicon，GitHub `macos-latest`）与 Windows（x64，Ubuntu 上交叉发布）都由 `build/package.sh` 自包含发布：macOS 拼成 `OurTaikoEdit.app`（`Info.plist`、图标、ad-hoc 签名，bundle id `org.ourtaiko.edit`），Windows 是带 `OurTaikoEdit.exe` 的文件夹；原生库的 `.pdb` 不带。
- 两个包都通过 RhythmHub 的 `upload-build` Action 上传到 S3 桶 `sparkbarry-public`（CDN `distrib.sparkbarry.com`），游戏 ID `OurTaikoEdit`：`main` 上线 nightly 频道（只留最新），`v*` 标签上线 stable 频道。仓库需要 Variable `AWS_ROLE_ARN` 与 Secret `RHYTHMHUB_SIGNING_KEY`，CI 角色要信任本仓库（见 sparkbarry/RhythmHub `docs/operations.md` §5）。
- 同时把 zip 发到 GitHub Release：`main` 覆盖 `nightly` 预发布，`v*` 标签建正式 Release。

本地打包：

```bash
build/package.sh osx-arm64 dist/osx
```

## 目录

```
build/package.sh         自包含发布并打成 .app（osx-arm64）或文件夹（win-x64），CI 与本地共用
external/TJAParser/      子模块，指 TJAParser 的 tag（现在 v0.13.0）
src/OurTaikoEdit.Core/      项目模型、项目列表、谱面语言扩展点（IChartLanguage）与语言无关的分析模型，界面文字表（Localization）与设置（Settings），Mod 的接口与加载（Mods/）、导入器的接口（Importing/）
src/OurTaikoEdit.Tja/       TJA 语言：分析、诊断与补全都用 TJAParser 的编辑器接口（TjaDocument、TjaAnalysis、TjaCompletion），高亮（Tja.xshd），时间轴编辑转文本编辑
src/OurTaikoEdit.Audio/     歌曲音频：解码 OGG（NVorbis）／MP3（NLayer）／WAV，波形，OpenAL Soft 播放，游戏的 21 套打击音（HitSounds/）
src/OurTaikoEdit.App/       Avalonia 应用：Projects 与编辑两个场景；界面的颜色、图标与控件样式在 Theme/
src/OurTaikoEdit.Mods.ChartImage/  Mod「谱面图片导入」：从谱面图片新建项目（见下文「Mod」）
tests/OurTaikoEdit.Tests/   语言与项目的单元测试
tests/OurTaikoEdit.UiTests/ Headless UI 测试与截图
```

## 项目

项目是一个文件夹：谱面文件、音频，以及 `ourtaikoedit.json`（`name`、`chartType`、`chart`、`created`、`lastBlock`）。应用在数据目录（macOS `~/Library/Application Support/OurTaikoEdit/projects.json`）里记录跟踪的项目文件夹。

- **New Project**：在所选位置建文件夹，按语言模板写谱面，可选把音频复制进来并写入 `WAVE:`。有 Mod 提供导入器时，对话框多一栏「起点」：选「空白谱面」之外的一项（例如「谱面图片」）就再选一个要读取的文件，谱面由它转成所选的谱面类型（只列出这个导入器能写的类型）；读不了时原因写在对话框里，对话框不关。
- **Open Folder**：有 `ourtaikoedit.json` 就读它；没有就取文件夹里第一个已知语言的谱面文件，生成一个。
- 从列表移除只是不再跟踪，文件不动。
- 2026-11-11 由 CharTaiko 改名：旧项目里的 `chartaiko.json` 在打开时改名为 `ourtaikoedit.json`；数据目录 `CharTaiko` 在应用第一次取数据目录时整体改名为 `OurTaikoEdit`（已有新目录时不动旧的）。

## 编辑场景

- **代码**（AvaloniaEdit）：TJA 高亮；补全来自库的 `TjaCompletion`（头部、取值、指令、分歧条件、音符；输入时弹出，⌘/Ctrl+Space 手动）；诊断来自 `TjaDocument.Diagnostics`（解析器错误与警告 TJA1xxx／2xxx，加编辑器检查 TJA3xxx），画波浪线并列在 Problems 里，点击跳到行。解析出错后库会继续读，预览照常显示，但时间轴在该块有错误时拒绝编辑。
- **难度标签**：文件里每个 `#START` 块一个标签；光标进入哪个块就切到哪个。分歧里也一样：光标进入另一条路线（`#N`／`#E`／`#M`）的音符、指令或其间的空行，轨道预览右上角的 Route 就切到那条路线（当前路线在这个分歧上本来就落到它时不切）；分歧标记行与各路线共用的部分不改路线。什么都没写的小节（只有逗号）以它的逗号为位置：播放头走到这样的小节时光标与红框落在逗号上，光标放在逗号上时播放头到该小节开头。
- **新建难度**：难度标签后面的「＋」。对话框里选难度（只列谱面里还没有的：Easy／Normal／Hard／Oni／Edit）与星级，再选「继承自」哪个已有难度（默认是正在编辑的那个；也可以选「不继承」，得到四个空小节）以及继承什么：
  - 速度与小节：`#BPMCHANGE`、`#MEASURE`、`#DELAY`——决定每个小节落在什么时刻的指令，所以三个是一起的；
  - 分歧：`#BRANCHSTART`／`#N`／`#E`／`#M`／`#BRANCHEND`（连同 `#SECTION`、`#LEVELHOLD`），各路线都写出来；不继承分歧时，每个分歧只取普通路线（没写普通路线的取写在最前面的那条），成为各路线共用的小节；
  - Go-go time：`#GOGOSTART`、`#GOGOEND`。

  新难度写在文件最后（`COURSE:`、`LEVEL:`、空的 `BALLOON:`、`#START`…`#END`）：被继承难度的每个小节写成一个空小节（`,`），勾选的指令留在原来的位置；写在小节中间的指令靠补 `0` 保住位置，补得尽量少（十六个音符正中间的 `#BPMCHANGE` 写成 `0`、指令、`0,`）。音符、气球打数、`#SCROLL` 等其他指令和注释都不带过去。写完会读回核对：新难度没有错误，继承了速度时每个小节的起止时刻与原难度一致，否则拒绝。被继承的难度有错误时拒绝。一次修改，撤销一步恢复；完成后切到新难度。
- **轨道预览**：按 OurTaikoPlay 的轨道版式画（Nijiiro 1920 宽舞台按面板缩放：轨道 200 高、判定点在轨道左起 120、下方音符文字条），音符位置用 TJAParser 的 `NoteScroll`／`ScrollClockTimeline`（v0.11.0 起从游戏移入库，游戏、View_Web 与 OurTaikoEdit 共用），所以普通／BM／HB 滚动、`#SUDDEN`、连打作为刚体、`#JPOSSCROLL` 移动判定点都与游戏一致；小节线 3 宽（分歧起点 6 宽金色）；分歧决定后轨道按路线着色（玄人蓝、達人紫），go-go 时轨道变红，`#LANE_ALPHA` 调轨道透明度；音符下方显示音符文字（ドン、カッ、連打…ーっ!! 等，取自解析器的 `NoteMoji`）。音符、连打身体与尾巴、气球、音符文字都用游戏的贴图（`Assets/Game/`，取自 OurTaikoPlay，见其中的 PROVENANCE.md），按游戏的切片与摆法画（`Controls/GameSprites.cs`）；表情帧固定为第一帧（游戏里 50 连击后才随拍子换）。Ad-lib 游戏里不画，这里画淡圈方便找。点音符跳到代码。
- **时间轴**：每条路线一行（无分歧时一行），小节按 Grid（1/4–1/192，以 4/4 小节计）切分。
  - 音符工具：单击放置，连打／气球拖到终点，右键删除。
  - Select 工具（Q）：单击选中并在代码里定位，Shift 或 ⌘/Ctrl 单击加减选，空白处拖框选择；拖动选中的音符按网格整格移动（连打连同终点）；Delete 删除，Esc 取消选择，⌘/Ctrl+A 全选，⌘/Ctrl+C／X／V 复制、剪切、粘贴（粘贴到指针所在格，指针不在行上时到播放头）；←／→ 有选择时移动一格、没有时播放头走一格；有选择时按音符键（1–9、G、C、F）改种类（短音符之间、连打／气球之间）；右键菜单：删除、改为…、咚咔互换、剪切、复制、在此粘贴、全选。
  - 拖连打／气球的尾端（Select 下光标变为左右箭头）改长度。
  - Commands 行：块里的每条指令画成小刻度加标签（BPM 数值、×SCROLL、拍号、+DELAY、GO▸／◂GO、|off／|on，其余为指令名），另有 go-go 区间与开头的 BPM；拖动指令按网格移动（`#MEASURE` 只落在小节开头），双击改值，右键插入 `#BPMCHANGE`／`#SCROLL`／`#MEASURE`／`#DELAY`／`#GOGOSTART`／`#GOGOEND`／`#BARLINEOFF`／`#BARLINEON`，或改值、删除、在代码里显示附近的指令。
  - 分歧：每个 `#BRANCHSTART` 在各行上画一条紫色实线，线顶的旗标写着它的条件（如「分歧 p,60,85」）；条件的判定点（库的 `ChartBranch.DecisionTime`：分歧前最后一条小节线，连打／气球条件会等到那一小节里的连打结束）画虚线和「判定」旗标。单击旗标在代码里定位到 `#BRANCHSTART`，双击改条件，右键菜单有编辑、「在代码中显示」和「删除分歧（保留普通路线）」：删掉 `#BRANCHSTART`、路线标记行、其余路线的整段和 `#BRANCHEND`，普通路线写的内容原地留下、成为各路线共用的部分，气球打数列表（`BALLOON`／`BALLOONEXP`／`BALLOONMAS`）随之改写；整个操作是一次修改，撤销一步恢复。这个分歧没写普通路线时拒绝。新建分歧：在各行（Select 工具）或 Commands 行右键「在这一小节新建分歧…」，先在分歧条件对话框里填条件，然后把指针所在的那个小节（从第一个音符到逗号，连同写在里面的指令）包成 `#BRANCHSTART`／`#N`／`#E`／`#M`／`#BRANCHEND`，三条路线都是这个小节的原样，所以各路线演奏不变；气球打数列表随之补写；已经在分歧里的小节不能再建，结果读回来与原来不一致（例如有连打跨过小节两端）时拒绝；一次修改，撤销一步恢复。分歧条件对话框（新建与编辑共用）：下拉选条件种类（`p`、`pp`、`jp`、`d`、`jg`、`jb`、`r`、`rb`、`s`，各带说明），玄人、达人阈值各一个输入框，「只算大音符／大连打／彩球」（写成大写）与「阈值为上界（`l`）」两个勾选，底部显示写出来的样子；阈值不是数字时不能确定。移动分歧在代码里做。
  - 点或拖标尺跳转播放头；滚轮横移，⌘/Ctrl + 滚轮缩放；空格播放。
  - 对齐歌曲：把播放头放到歌曲里第一小节该开始的地方（听着播放停下，或对着波形点标尺），然后「把第一小节放到播放头处」——在标尺上右键（播放头先跳到右键的位置），或在各行（Select 工具）、Commands 行的右键菜单最后一项。它改写 `OFFSET`（没有就写在 `BPM:` 下一行），使第一小节从播放头的时刻开始，整张谱连同共用这个 `OFFSET` 的其他难度一起平移；数值取到毫秒；一次修改，撤销一步恢复。从谱面图片导入的谱面 `OFFSET` 是 0，就用它来对。
- **代码辅助**（都来自 TJAParser）：悬停提示（含该处诊断）、写指令参数时的参数提示、块／分歧／路线折叠、行尾的小节号·时刻·BPM 提示、右键菜单或 ⌘/Ctrl+. 的快速修复与重构（拆分／合并小节、气球打数写成内联），顶栏的格式化按钮（Shift+Alt+F，保证每块哈希不变）；轨道下方显示所选块的音符数、最大连击、时长、峰值密度与哈希；诊断消息可选英／中／日文（默认与界面相同）。
- **播放**：读 `WAVE:` 指定的音频（OGG、MP3、WAV），后台解码，时间轴行背后画波形；播放头跟随歌曲位置（`OFFSET` 已含在音符时刻里，所以波形与音符直接对齐），播放头在 0 之前或歌曲之后用时钟走；可调音量，顶栏的 `Hits` 按下时音符经过判定点播放游戏的打击音（设置里可在游戏的 21 套音色中选，默认「太鼓」；文件缺失时退回合成音）；播放头跟的是**听到的位置**：歌曲比播放头提前一个输出延迟播放。输出延迟＝系统报告的设备延迟（macOS 上向 Core Audio 取：设备延迟＋安全偏移＋缓冲＋流延迟，本机约 27 ms；其他系统取不到，算 0）＋设置里的「音频延迟微调」（−200–500 ms，无线耳机之类系统不知道的延迟在这里补）。打击音按歌曲自己的时间走（`HitPlayback`）：歌曲播放时，每个音符的打击音提前 60 ms 交给播放器，由 OpenAL Soft 的 `alSourcePlayAtTimeSOFT` 在歌曲的那一个采样上开始，所以和歌曲一起被听到，也就是在播放头经过音符的时候（做法同 OpenSekai、OurTaikoPlay 的「自动演奏音符音提前一个输出延迟」）。没有这个扩展时，退回到在最接近的混音块上开始。没有音频设备时退回无声时钟。代码里正在演奏的字符会被框出并跟随滚动。
- 撤销覆盖时间轴编辑（一次放置是一次撤销）。撤销（⌘/Ctrl+Z）、重做（⇧⌘Z、Ctrl+Shift+Z 或 ⌘/Ctrl+Y）、查找（⌘/Ctrl+F，顶栏的放大镜按钮；AvaloniaEdit 自带的搜索面板，可切到替换）在整个编辑场景都有效，焦点在时间轴或轨道上时也一样。读取时按 BOM、严格 UTF-8、再 Shift-JIS 的顺序判断编码，状态栏点编码可改用 Shift-JIS、GB18030、Big5、EUC-KR、UTF-16 重新读取；**保存一律写 UTF-8（无 BOM）**，因为游戏与两站只认 UTF-8——用其他编码读入的文件打开时就标为未保存，状态栏显示「SHIFT_JIS → UTF-8」。

## 界面

- **版式**：编辑场景最上面一条顶栏——左边是回到 Projects、项目名（有未保存的更改时带红点）与保存／撤销／重做／查找／格式化，中间是回到开头与播放，右边是音频状态、音量、`Hits` 与设置。下面左半是代码：难度是代码上方的标签页（圆点是难度色），底部一条 Problems（点它展开或收起问题列表，右边选谱面提示的语言）；右半上面是轨道预览（左上角是时刻、BPM、小节，谱面有分歧时右上角是 Route），下面是时间轴，时间轴上方一条是音符工具（按时间轴画音符的样子画，旁边是快捷键，名称在提示与工具条末尾）和 Grid。最下面是状态栏（文件、消息、光标位置、编码、谱面类型）。
- **颜色**：沿用 OurTaiko 网站的纸墨配色的深色一侧——暖色的近黑底、米色字，咚红（`#F65040`，白字用较深的 `#D83A2C`）给会动作的东西，咔青（`#68D0D7`）给焦点与选区，难度色与网站相同。都定义在 `src/OurTaikoEdit.App/Theme/Ui.cs`：`App.axaml` 把它们命名成资源（`PanelBrush`、`MutedBrush`…）并据此改写 Fluent 主题的调色板与各控件的资源；自己画的控件（轨道、时间轴、代码区的标记）直接用 `Ui`。新增界面颜色先加到 `Ui.cs`，不在视图里写色值（音符、go-go、轨道本身的颜色除外，它们跟游戏走，在 `ChartPalette` 与 `LaneView`）。
- **样式**：`Theme/Styles.axaml`——文字（`muted`、`faint`、`label`、`caption`、`section`、`h1`）、按钮（默认、`accent`、无边框的 `ghost`、方形的 `icon`、状态栏里的 `status`）、列表（`tabs`、`palette`、`cards`、`rows`）、对话框的页脚（`Border.footer`）。对话框都是「内容＋页脚里靠右的按钮」，`DialogService` 里用代码建的那几个也一样。
- **图标**：`Theme/Icons.axaml` 是 16 格上的线条路径（为本应用画的），由 `Controls/Icon` 按所在文字的颜色描出来，所以跟着按钮的悬停、按下变色。`Controls/NoteGlyph` 是工具条上的音符，`Controls/Logo` 是应用的标记（一个咚、后面一个咔）；`Assets/app.ico` 是这个标记放在深色圆角方块上，各尺寸由 `Logo` 离屏渲染后打包。
- 只有深色；浅色主题要把 `Ui` 的颜色做成随主题取值，还没做。

## 界面语言与设置

- 界面有英文、简体中文、日文，默认跟随系统语言（其他语言用英文）。文字表是 `src/OurTaikoEdit.Core/Localization/Strings.csv`（UTF-8 带 BOM，可直接用 Excel／Numbers 打开；列 `key,en,zh,ja` 按表头认，键以 `#` 开头的行是分节标题、空行忽略；含逗号、引号或换行的格按 CSV 规则加引号），编进 Core 程序集、启动时读入。单元测试检查每条都有三种语言、占位符一致，以及 XAML 和代码里写死的键都在表里。XAML 里写 `{l:T 键}`，切换语言即时生效；代码里用 `Loc.T`／`Loc.F`／`Loc.Plural`。
- 诊断、悬停说明、补全说明、参数提示、修复与重构的标题、轨道下方的摘要来自 TJAParser（v0.10.0 起三种语言），语言由设置里的「谱面提示的语言」决定（默认与界面相同）。
- 中日文字体：先找 PingFang SC／微软雅黑／Noto Sans CJK 等（界面为日文时先找 Hiragino／Yu Gothic），避免同一行里字重不一；顺序在启动时按界面语言定，切换语言后重启才换。
- 设置（Projects 场景右上的「设置」，或编辑场景顶栏最右的按钮）存在数据目录的 `settings.json`：界面语言、诊断消息语言、新项目的默认位置、代码字号、行尾提示、自动换行、打开项目时的网格、歌曲音量、打击音开关与音量、音频延迟微调（−200–500 ms，加在系统报告的设备延迟上）、关闭的 Mod；另记窗口大小。工具栏上改的音量与打击音开关也会存下来。文件读不了或数值越界时退回默认值。

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

## Mod

Mod 是放在自己程序集里的功能，只通过 `OurTaikoEdit.Core` 认识应用：程序集里有一个实现 `IMod` 的公开类，应用把 `IModHost` 交给它，它登记自己加的东西——谱面类型（`AddLanguage`）、新建项目的起点（`AddImporter`，`IChartImporter`）、自己的界面文字（`AddStrings`，与 `Strings.csv` 同样的 CSV，键以 Mod 自己的名字开头）。

- 一个 Mod 是一个文件夹：`mod.json`（`id`、`name`、`version`、`assembly`）和它的程序集。应用启动时先读自己旁边的 `Mods/`（随应用提供的），再读数据目录的 `Mods/`（用户自己放的，macOS `~/Library/Application Support/OurTaikoEdit/Mods/`）；同一个 `id` 先到的算数。Mod 用应用自己的 Core、TJAParser 与框架程序集，其余依赖从 Mod 的文件夹里找。
- 设置的「Mod」一节列出找到的 Mod，取消勾选即关闭（`settings.json` 的 `disabledMods`），下次启动生效；加载失败的 Mod 列出原因，应用照常运行。
- 随应用提供的 Mod 不被应用引用：`OurTaikoEdit.App.csproj` 里的 `ShippedMod` 项目引用只负责一起构建，并把程序集与 `mod.json` 放到输出与发布目录的 `Mods/<ModFolder>/`。新加一个随应用提供的 Mod 就在那里加一行。

### 谱面图片导入（`src/OurTaikoEdit.Mods.ChartImage`）

把一张谱面图片读回谱面：新建项目时「起点」选「谱面图片」、选 PNG，转成 TJA。读的是 wiki 上那种谱面图（灰色轨道、红蓝音符、5 像素高的点阵字）。版式、标签与网格的读法来自 [TJAAnalyzer](https://github.com/jack9966qk/TJAAnalyzer) 的 `offline-analysis/chart-image-decoder.ts`（MIT，见该目录的 `PROVENANCE.md`）；音符与连打的识别是这里重写的：

- 形状是量出来的：音符是一个圆盘（小 12 像素、大 16 像素宽，`Small`／`Big` 两张逐行的像素表）外加一圈环（半径 9／12）；连打是起点一个圆盘、终点一个圆盘、中间一条带；气球是带数字的头加一条带。
- 画的顺序是后打的先画，所以**先打的盖在上面**：每样东西的右端总是露着，左边可能被前一个音符的环盖住。于是一律从轨道中线上的右端找起。
- 音符：右端确定后只有「小」和「大」两种可能的位置，取那个「整个圆盘减去前面的东西盖住的部分，恰好等于图上像素」的；两种都说得通（露得太少）或都说不通就拒绝。
- 连打的起点：圆盘每一行的左端本应在固定的位置；凡是「左边那个像素没有被任何环碰到」的行都直接给出起点，这些行必须一致。所有行都被盖住时才交给网格，在盖住它的音符与上限之间只有一个格时采用，否则拒绝。

- 图是逐像素精确的：颜色与位置都按渲染器的原值比对，不做阈值或猜测。所以**只能读原始 PNG**，截图、缩放过的、另存为 JPEG 的都读不了。PNG 由 Mod 自己的 `Png.cs` 读（各种颜色类型与位深，不支持隔行扫描）。
- 读得到：小节与拍号（`#MEASURE`，由小节宽度得出）、咚咔与大音符、连打与大连打、气球及打数、BPM 与 `#BPMCHANGE`、`#SCROLL`、go-go 区间。每个小节取能容纳其中所有内容的最粗网格；落不到网格上、被遮住而无法确定的一律拒绝并说明原因，不挪到最近的位置。
- 图里没有的：`OFFSET`、难度与星级（写成 `COURSE:Oni`、`LEVEL:0`）、分歧条件（有分歧的图没有试过，解码器是按一行一条轨道写的）、标题（用项目名）、图上没画出的 BPM／SCROLL 小数位。导入后弹出的说明会列出这些，以及每格不到 4 像素、需要核对的小节。
- 写出的谱面交给目标谱面类型读回一遍，逐小节与解码结果比对，一致才交给新项目。
- 现在只会写 TJA，所以谱面类型一栏只有 TJA；别的谱面类型要在 `ChartImageImporter` 里加它的写法（`CanWrite` 与写出）。

## 扩展新的谱面格式

实现 `IChartLanguage`（分析、补全、高亮、`IChartEditing`），在 `App.OnFrameworkInitializationCompleted` 里 `ChartLanguages.Register`（或者做成 Mod，在 `IMod.Load` 里 `host.AddLanguage`），`ourtaikoedit.json` 的 `chartType` 用它的 `Id`。预览与时间轴读的是语言无关的 `ChartAnalysis`（`TaikoChart` + 小节／字符格的源码位置）。

## 许可

GPL-3.0-or-later（与 TJAParser 相同）。
