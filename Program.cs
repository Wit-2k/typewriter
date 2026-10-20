using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Remake;

internal static class Program
{
    private const string DefaultText =
        "这是打字机的示例文本。\n" +
        "空格 和 制表符\t都会原样输入，换行也保留。\n" +
        "中文、English、符号 !@#$% 都支持。\n";

    private const string Usage = "用法：typewriter [-h] [-f FILE] [-d DELAY] [-w START_DELAY]";
    private const string HelpText = """
        用法：typewriter [-h] [-f FILE] [-d DELAY] [-w START_DELAY]

        全局热键打字机：按 F9 把文本逐字符输入到当前窗口。

        选项:
          -h, --help            显示帮助信息
          -f, --file FILE       文本文件路径（默认 text.txt）
          -d, --delay DELAY     每个字符之间的间隔秒数（默认 0.008）
          -w, --start-delay START_DELAY
                                按下 F9 后等待多久开始输入（默认 0.3 秒）
        """;

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint WmQuit = 0x0012;
    private const uint WmHotkey = 0x0312;
    private const uint Gb18030CodePage = 54936;

    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0D;
    private const ushort VkF9 = 0x78;
    private const ushort VkF10 = 0x79;

    private const int StartHotkeyId = 1;
    private const int StopHotkeyId = 2;
    private const int QuitHotkeyIdBase = 3;

    // 退出键允许冲突后换一个：F8、F12 等常被截图或系统工具占用。
    private static readonly (ushort VirtualKey, string Label)[] QuitCandidates =
    [
        (0x76, "F7"),
        (0x77, "F8"),
        (0x7B, "F12"),
        (0x75, "F6"),
    ];

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("这个打字机依赖 Windows 的 SendInput API，只能在 Windows 上运行。");
            return 1;
        }

        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (IOException)
        {
            // 输出被重定向时保留默认编码即可。
        }

        Options options;
        try
        {
            options = ParseArgs(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(HelpText);
            return 0;
        }

        List<int> registeredHotkeys;
        Dictionary<int, string> actions;
        string? quitLabel;
        try
        {
            (registeredHotkeys, actions, quitLabel) = RegisterHotkeys();
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"文本来源：{Path.GetFullPath(options.File)}（每次按 F9 重新读取）");
        Console.WriteLine(quitLabel is null
            ? "F9 开始输入，F10 中止；没有可用的退出热键，关闭这个窗口即可退出。"
            : $"F9 开始输入，F10 中止，{quitLabel} 退出。");
        Console.WriteLine("提示：按 F9 前先把光标放到目标程序里；需要切换窗口时用 -w/--start-delay 加长等待。");

        uint mainThreadId = GetCurrentThreadId();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            PostThreadMessage(mainThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        };
        Console.CancelKeyPress += onCancel;

        Thread? worker = null;
        CancellationTokenSource? workerStop = null;
        bool running = true;

        try
        {
            while (running && GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.Message != WmHotkey)
                {
                    continue;
                }

                if (!actions.TryGetValue((int)msg.WParam, out string? action))
                {
                    continue;
                }

                switch (action)
                {
                    case "start":
                        if (worker is { IsAlive: true })
                        {
                            Console.WriteLine("上一次输入还没结束，忽略本次 F9。");
                            continue;
                        }

                        string text = LoadText(options.File);
                        if (text.Length == 0)
                        {
                            Console.WriteLine($"{options.File} 是空的，没有可输入的内容。");
                            continue;
                        }

                        var stop = new CancellationTokenSource();
                        workerStop = stop;
                        worker = new Thread(() => RunTyping(text, options.Delay, options.StartDelay, stop.Token))
                        {
                            IsBackground = true,
                            Name = "typewriter",
                        };
                        worker.Start();
                        break;

                    case "stop":
                        if (worker is { IsAlive: true })
                        {
                            Console.WriteLine("收到中止请求……");
                            workerStop?.Cancel();
                        }
                        break;

                    case "quit":
                        Console.WriteLine("退出。");
                        running = false;
                        break;
                }
            }
        }
        finally
        {
            workerStop?.Cancel();
            if (worker is { IsAlive: true })
            {
                worker.Join(TimeSpan.FromSeconds(1));
            }

            UnregisterHotkeys(registeredHotkeys);
            workerStop?.Dispose();
            Console.CancelKeyPress -= onCancel;
        }

        return 0;
    }

    private static void RunTyping(string text, double delay, double startDelay, CancellationToken token)
    {
        Wait(startDelay, token);
        if (token.IsCancellationRequested)
        {
            Console.WriteLine("输入已中止。");
            return;
        }

        Console.WriteLine($"开始输入 {text.EnumerateRunes().Count()} 个字符……");
        try
        {
            bool completed = TypeText(text, delay, token);
            Console.WriteLine(completed ? "输入完成。" : "输入已中止。");
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine($"输入失败：{ex.Message}");
        }
    }

    private static bool TypeText(string text, double delay, CancellationToken token)
    {
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (char unit in normalized)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            if (unit == '\n')
            {
                SendVirtualKey(VkReturn);
            }
            else if (unit == '\t')
            {
                SendVirtualKey(VkTab);
            }
            else
            {
                SendUnicode(unit);
            }

            Wait(delay, token);
        }

        return true;
    }

    private static void SendUnicode(char unit)
    {
        SendInputEvents(
        [
            MakeKeyInput(0, unit, KeyEventUnicode),
            MakeKeyInput(0, unit, KeyEventUnicode | KeyEventKeyUp),
        ]);
    }

    private static void SendVirtualKey(ushort virtualKey)
    {
        SendInputEvents(
        [
            MakeKeyInput(virtualKey, 0, 0),
            MakeKeyInput(virtualKey, 0, KeyEventKeyUp),
        ]);
    }

    private static Input MakeKeyInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                ScanCode = scanCode,
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero,
            },
        },
    };

    private static void SendInputEvents(Input[] events)
    {
        uint sent = SendInput((uint)events.Length, events, Marshal.SizeOf<Input>());
        if (sent != events.Length)
        {
            int code = Marshal.GetLastWin32Error();
            throw new Win32Exception(
                code,
                $"SendInput 调用失败（目标程序以管理员权限运行时，请以管理员身份运行本程序；错误码 {code}）");
        }
    }

    private static void Wait(double seconds, CancellationToken token)
    {
        if (seconds <= 0)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        double target = seconds * Stopwatch.Frequency;
        double spinThreshold = Stopwatch.Frequency * 0.001;

        while (true)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            double remaining = target - (Stopwatch.GetTimestamp() - start);
            if (remaining <= 0)
            {
                return;
            }

            if (remaining > spinThreshold)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(200);
            }
        }
    }

    private static string LoadText(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"没有找到 {path}，改用内置示例文本。");
            return DefaultText;
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"读取 {path} 失败：{ex.Message}，改用内置示例文本。");
            return DefaultText;
        }

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            string utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(raw);
            return utf8.StartsWith('\uFEFF') ? utf8[1..] : utf8;
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8，继续尝试中文 Windows 常用的 GB18030。
        }

        if (TryDecodeCodePage(raw, Gb18030CodePage, out string gb18030))
        {
            return gb18030;
        }

        return Encoding.UTF8.GetString(raw);
    }

    private static bool TryDecodeCodePage(byte[] bytes, uint codePage, out string text)
    {
        text = string.Empty;
        int length = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, null, 0);
        if (length <= 0)
        {
            return false;
        }

        char[] buffer = new char[length];
        int written = MultiByteToWideChar(codePage, 0, bytes, bytes.Length, buffer, buffer.Length);
        if (written <= 0)
        {
            return false;
        }

        text = new string(buffer, 0, written);
        return true;
    }

    private static (List<int> Registered, Dictionary<int, string> Actions, string? QuitLabel) RegisterHotkeys()
    {
        var registered = new List<int>();
        var actions = new Dictionary<int, string>();

        bool TryRegister(int id, uint virtualKey)
        {
            if (!RegisterHotKey(IntPtr.Zero, id, ModNoRepeat, virtualKey))
            {
                return false;
            }

            registered.Add(id);
            return true;
        }

        try
        {
            if (!TryRegister(StartHotkeyId, VkF9))
            {
                throw HotkeyError("F9");
            }
            actions[StartHotkeyId] = "start";

            if (!TryRegister(StopHotkeyId, VkF10))
            {
                throw HotkeyError("F10");
            }
            actions[StopHotkeyId] = "stop";

            string? quitLabel = null;
            for (int i = 0; i < QuitCandidates.Length; i++)
            {
                if (TryRegister(QuitHotkeyIdBase + i, QuitCandidates[i].VirtualKey))
                {
                    actions[QuitHotkeyIdBase + i] = "quit";
                    quitLabel = QuitCandidates[i].Label;
                    break;
                }
            }

            return (registered, actions, quitLabel);
        }
        catch
        {
            UnregisterHotkeys(registered);
            throw;
        }
    }

    private static Win32Exception HotkeyError(string label)
    {
        int code = Marshal.GetLastWin32Error();
        return new Win32Exception(code, $"注册热键 {label} 失败，可能被其他程序或另一个实例占用（错误码 {code}）");
    }

    private static void UnregisterHotkeys(IEnumerable<int> ids)
    {
        foreach (int id in ids)
        {
            UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    private static Options ParseArgs(string[] args)
    {
        string file = "text.txt";
        double delay = 0.008;
        double startDelay = 0.3;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    return new Options { ShowHelp = true };

                case "-f":
                case "--file":
                    file = NextValue(args, ref i, arg);
                    break;

                case "-d":
                case "--delay":
                    delay = ParseDouble(NextValue(args, ref i, arg), arg);
                    break;

                case "-w":
                case "--start-delay":
                    startDelay = ParseDouble(NextValue(args, ref i, arg), arg);
                    break;

                default:
                    if (TrySplitInlineValue(arg, out string option, out string value))
                    {
                        switch (option)
                        {
                            case "-f":
                            case "--file":
                                file = value;
                                break;
                            case "-d":
                            case "--delay":
                                delay = ParseDouble(value, option);
                                break;
                            case "-w":
                            case "--start-delay":
                                startDelay = ParseDouble(value, option);
                                break;
                        }

                        break;
                    }

                    throw new ArgumentException($"无法识别的参数：{arg}");
            }
        }

        return new Options { File = file, Delay = delay, StartDelay = startDelay };
    }

    private static string NextValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"参数 {option} 需要一个值");
        }

        return args[++index];
    }

    private static bool TrySplitInlineValue(string arg, out string option, out string value)
    {
        option = string.Empty;
        value = string.Empty;

        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            int separator = arg.IndexOf('=');
            if (separator < 0)
            {
                return false;
            }

            option = arg[..separator];
            value = arg[(separator + 1)..];
            return option is "--file" or "--delay" or "--start-delay";
        }

        if (arg.Length > 2 && arg[0] == '-' && arg[1] is 'f' or 'd' or 'w')
        {
            option = arg[..2];
            value = arg[2..];
            if (value.StartsWith('='))
            {
                value = value[1..];
            }

            return true;
        }

        return false;
    }

    private static double ParseDouble(string raw, string option)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            throw new ArgumentException($"参数 {option} 需要一个数字，收到“{raw}”");
        }

        return value;
    }

    private sealed class Options
    {
        public string File { get; init; } = "text.txt";

        public double Delay { get; init; } = 0.008;

        public double StartDelay { get; init; } = 0.3;

        public bool ShowHelp { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "MultiByteToWideChar")]
    private static extern int MultiByteToWideChar(
        uint codePage,
        uint dwFlags,
        [In] byte[] lpMultiByteStr,
        int cbMultiByte,
        [Out] char[]? lpWideCharStr,
        int cchWideChar);
}
