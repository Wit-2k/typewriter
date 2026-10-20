# typewriter

Windows 全局热键打字机，用 C# / .NET 10 重写自 Python 版：监听全局热键，按 F9 后把文本逐字符输入到当前焦点窗口。空格、制表符和换行按原样输入，中文通过 `SendInput` 的 Unicode 模式直接注入，不需要切换输入法。

## 构建与运行

需要 .NET 10 SDK。

```powershell
dotnet build
dotnet run -- -f text.txt -d 0.02 -w 3
```

也可以直接运行生成的可执行文件：

```powershell
.\bin\Debug\net10.0\remake.exe -f text.txt
```

## 发布

常规发布（目标机需要 .NET 运行时）：

```powershell
dotnet publish -c Release -r win-x64 -o publish
```

NativeAOT 发布（单文件、无需安装运行时，约 1.4 MB）：

```powershell
dotnet publish -c Release -r win-x64 -p:PublishAot=true -o publish
.\publish\remake.exe
```

NativeAOT 需要安装 Visual Studio 的 MSVC C++ 生成工具，并且只支持在 Windows x64 上构建 `win-x64` 包。

## 热键

| 按键 | 作用 |
| --- | --- |
| F9 | 开始输入 |
| F10 | 中止输入 |
| F7 | 退出程序（F7 被占用时自动改用 F8、F12、F6） |

## 参数

| 参数 | 说明 |
| --- | --- |
| `-f, --file` | 文本文件路径，默认 `text.txt`。文件不存在时使用内置示例文本。 |
| `-d, --delay` | 每个字符之间的间隔秒数，默认 `0.008`。 |
| `-w, --start-delay` | 按下 F9 到开始输入的等待秒数，默认 `0.3`。切换窗口时可以调大。 |
| `-h, --help` | 显示帮助。 |

示例：

```powershell
.\remake.exe                          # 使用当前工作目录的 text.txt
.\remake.exe -f 我的文稿.txt -d 0.02  # 指定文件和打字速度
.\remake.exe -f 文稿.txt -w 3         # 按 F9 后等 3 秒再开始
```

## 文本文件

- 默认读取**当前工作目录**下的 `text.txt`，与 exe 所在位置无关；启动时会打印实际读取的完整路径。
- 支持 UTF-8（带不带 BOM 都行）和 GB18030 编码。
- 每次按 F9 都会重新读取文件，改完内容直接再按 F9 即可，不需要重启程序。
- 文件为空时不会输入；文件不存在时改用内置示例文本。
- 文本按原样输入，文件末尾的换行也会被发送；用于聊天框时请自行去掉末尾换行。

## 限制

- 只支持 Windows。
- 目标程序以管理员权限运行时，本程序也需要以管理员权限运行。
- 部分游戏、带反作弊或自绘输入的程序会拦截模拟按键。
