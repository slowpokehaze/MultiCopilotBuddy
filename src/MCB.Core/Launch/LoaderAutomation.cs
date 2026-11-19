using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MCB.Core.Launch;

/// <summary>
/// Win32-автоматизация внешнего лоадера (порт _orchestrate_loader из launcher.py):
/// 1) ждать окно лоадера до 30 с; 2) искать дочерние контролы до 10 с (могут
/// создаваться лениво): кнопка — по тексту, поле пути — класс содержит EDIT и
/// текст кончается на .exe; 3) WM_SETTEXT пути к Wow.exe; 4) клик тремя
/// способами (BM_CLICK → WM_LBUTTONDOWN/UP → SPACE), т.к. GUI-фреймворки
/// реагируют по-разному. Окно лоадера НЕ прячется — многие лоадеры сами
/// закрываются при запуске и прятать их опасно.
/// </summary>
public static class LoaderAutomation
{
    private const int WM_SETTEXT = 0x000C;
    private const int BM_CLICK = 0x00F5;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int VK_SPACE = 0x20;
    private const int MK_LBUTTON = 0x0001;

    private const int WindowTimeoutMs = 30_000;
    private const int ChildPollMs = 10_000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string lpWindowName);

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, [Out] StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, [Out] StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr PostMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    /// <summary>Порт _norm: убрать '&amp;' и все пробелы, нижний регистр (сравнение подписей кнопок).</summary>
    public static string Normalize(string? text)
        => new string((text ?? string.Empty).Replace("&", string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray())
            .ToLowerInvariant();

    /// <summary>
    /// Прогоняет лоадер: клик по кнопке запуска с подставленным путём.
    /// Возвращает true, если кнопка найдена и «нажата». Отчёт — для лога.
    /// </summary>
    public static bool Orchestrate(string wowExe, string windowTitle, string buttonText, out string report)
    {
        var log = new StringBuilder();
        var target = Normalize(buttonText);

        // 1. Ждём окно лоадера.
        var parent = IntPtr.Zero;
        var deadline = Environment.TickCount64 + WindowTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            parent = FindWindowW(null, windowTitle);
            if (parent != IntPtr.Zero)
            {
                break;
            }

            Thread.Sleep(50);
        }

        if (parent == IntPtr.Zero)
        {
            report = $"Окно лоадера «{windowTitle}» не появилось за {WindowTimeoutMs / 1000} с.";
            return false;
        }

        log.AppendLine($"Окно лоадера найдено (hwnd {parent}).");

        // 2. Ищем кнопку и поле пути (контролы могут появляться лениво).
        var button = IntPtr.Zero;
        var pathBox = IntPtr.Zero;
        var pollEnd = Environment.TickCount64 + ChildPollMs;
        while (Environment.TickCount64 < pollEnd && button == IntPtr.Zero)
        {
            EnumChildWindows(parent, (hwnd, _) =>
            {
                var className = new StringBuilder(128);
                _ = GetClassNameW(hwnd, className, 128);
                var text = new StringBuilder(520);
                _ = GetWindowTextW(hwnd, text, 520);

                if (button == IntPtr.Zero && Normalize(text.ToString()) == target)
                {
                    button = hwnd;
                }
                else if (pathBox == IntPtr.Zero
                         && className.ToString().ToUpperInvariant().Contains("EDIT")
                         && text.ToString().Trim().ToLowerInvariant().EndsWith(".exe"))
                {
                    pathBox = hwnd;
                }

                return true;
            }, IntPtr.Zero);

            if (button != IntPtr.Zero)
            {
                break;
            }

            Thread.Sleep(200);
        }

        // 3. Путь к Wow.exe в поле (если нашли).
        if (pathBox != IntPtr.Zero && !string.IsNullOrEmpty(wowExe))
        {
            _ = SendMessageW(pathBox, WM_SETTEXT, IntPtr.Zero, wowExe);
            Thread.Sleep(100);
            log.AppendLine($"Путь записан в edit (hwnd {pathBox}): {wowExe}");
        }

        if (button == IntPtr.Zero)
        {
            // Снимок всех контролов — чтобы понять, какие подписи реально есть.
            EnumChildWindows(parent, (hwnd, _) =>
            {
                var className = new StringBuilder(128);
                _ = GetClassNameW(hwnd, className, 128);
                var text = new StringBuilder(520);
                _ = GetWindowTextW(hwnd, text, 520);
                log.AppendLine($"  контрол: class='{className}' text='{text}'");
                return true;
            }, IntPtr.Zero);

            report = $"Кнопка «{buttonText}» не найдена в окне лоадера. {log}";
            return false;
        }

        // 4. Клик тремя способами.
        var lParam = (IntPtr)((1 << 16) | 1);
        try
        {
            _ = SetFocus(button);
        }
        catch
        {
            // фокус не критичен
        }

        _ = PostMessageW(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(100);

        _ = PostMessageW(button, WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
        Thread.Sleep(50);
        _ = PostMessageW(button, WM_LBUTTONUP, IntPtr.Zero, lParam);
        Thread.Sleep(100);

        _ = PostMessageW(button, WM_KEYDOWN, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(50);
        _ = PostMessageW(button, WM_KEYUP, IntPtr.Zero, IntPtr.Zero);

        report = $"Кнопка «{buttonText}» нажата (hwnd {button}). {log}";
        return true;
    }
}
