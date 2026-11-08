using System;
using System.Runtime.InteropServices;

namespace OpusMutatum.Utils;

internal class WindowsConsoleFixer {
    [DllImport("kernel32.dll")]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    [DllImport("kernel32.dll")]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint dwMode);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [Flags]
    private enum ConsoleModes : uint {
        ENABLE_PROCESSED_INPUT = 0x0001,
        ENABLE_LINE_INPUT = 0x0002,
        ENABLE_ECHO_INPUT = 0x0004,
        ENABLE_WINDOW_INPUT = 0x0008,
        ENABLE_MOUSE_INPUT = 0x0010,
        ENABLE_INSERT_MODE = 0x0020,
        ENABLE_QUICK_EDIT_MODE = 0x0040,
        ENABLE_EXTENDED_FLAGS = 0x0080,
        ENABLE_AUTO_POSITION = 0x0100,
        ENABLE_PROCESSED_OUTPUT = 0x0001,
        ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002,
    }
    [Flags]
    private enum ConsoleInputModes : uint {
        ENABLE_PROCESSED_INPUT = 0x0001,
        ENABLE_LINE_INPUT = 0x0002,
        ENABLE_ECHO_INPUT = 0x0004,
        ENABLE_WINDOW_INPUT = 0x0008,
        ENABLE_MOUSE_INPUT = 0x0010,
        ENABLE_INSERT_MODE = 0x0020,
        ENABLE_QUICK_EDIT_MODE = 0x0040,
        ENABLE_EXTENDED_FLAGS = 0x0080,
        ENABLE_AUTO_POSITION = 0x0100,
    }

    [Flags]
    private enum ConsoleOutputModes : uint {
        ENABLE_PROCESSED_OUTPUT = 0x0001,
        ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002,
        ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004,
        DISABLE_NEWLINE_AUTO_RETURN = 0x0008,
        ENABLE_LVB_GRID_WORLDWIDE = 0x0010,
    }
    const int STD_OUTPUT_HANDLE = -11;
    const int STD_INPUT_HANDLE = -10;
    const int STD_ERROR_HANDLE = -12;
    const int INVALID_HANDLE_VALUE = -1;
    public static void MakeMyWindowsConsoleBehave() {
        // Just enable ansi colour codes.
        // Stupid cmd terminals can't handle that wihout this black magic.
        EnableAnsiForConsole(STD_OUTPUT_HANDLE);
        EnableAnsiForConsole(STD_ERROR_HANDLE);
    }

    private static void EnableAnsiForConsole(int consoleHandle) {
        IntPtr hOut = GetStdHandle(consoleHandle);
        if (hOut != INVALID_HANDLE_VALUE) {
            if (GetConsoleMode(hOut, out uint mode)) {
                mode |= (uint) ConsoleOutputModes.ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                SetConsoleMode(hOut, mode);
            }
        }
    }
}
