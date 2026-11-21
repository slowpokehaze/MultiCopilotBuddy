using System.IO;

namespace MCB.Agent;

/// <summary>
/// Окно лога бота (запрос 2026-11-21): живой поток строк лога + в заголовке —
/// персонаж, бот, уровень, HP/MP. Агент headless, поэтому окно живёт на
/// выделенном STA-потоке и не мешает движку. Строки приходят из
/// Logging.OnLogMessage (те же, что пишутся в MCBAgent_*.log).
/// </summary>
public sealed class LogWindow : System.Windows.Forms.Form
{
    private readonly System.Windows.Forms.TextBox _text;
    private readonly Queue<string> _pending = new();
    private readonly System.Windows.Forms.Timer _flushTimer = new() { Interval = 250 };

    public LogWindow()
    {
        Text = "MCB.Agent — лог бота";
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = true;
        Font = new System.Drawing.Font("Consolas", 9F);

        // Правая половина рабочего стола — рядом с клиентом.
        var wa = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        var width = Math.Min(720, wa.Width / 2);
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        Bounds = new System.Drawing.Rectangle(wa.Right - width, wa.Top, width, Math.Min(560, wa.Height));

        _text = new System.Windows.Forms.TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill,
            WordWrap = false,
            BackColor = System.Drawing.Color.FromArgb(24, 24, 28),
            ForeColor = System.Drawing.Color.Gainsboro,
        };
        Controls.Add(_text);

        // Строки приходят из движковых потоков — копим в очереди, льём в UI-потоке.
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
    }

    /// <summary>Добавить строку лога. Потокобезопасно (из любого потока движка).</summary>
    public void AppendLine(string line)
    {
        lock (_pending)
        {
            _pending.Enqueue(line);
            while (_pending.Count > 2000)
            {
                _pending.Dequeue(); // окно — наблюдение, не архив
            }
        }
    }

    /// <summary>Заголовок окна: персонаж/бот/уровень/HP/MP. Потокобезопасно.</summary>
    public void UpdateTitle(string title)
    {
        try
        {
            if (IsHandleCreated)
            {
                BeginInvoke(() => Text = title);
            }
        }
        catch (ObjectDisposedException)
        {
            // окно закрыли — не страшно
        }
    }

    protected override void OnFormClosed(System.Windows.Forms.FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _flushTimer.Stop();
    }

    private void FlushPending()
    {
        string[] lines;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            lines = _pending.ToArray();
            _pending.Clear();
        }

        try
        {
            var atEnd = _text.TextLength == 0
                        || _text.GetLineFromCharIndex(_text.TextLength - 1) == _text.Lines.Length - 1;

            _text.AppendText(string.Join(Environment.NewLine, lines) + Environment.NewLine);

            // Автоскролл вниз, только если пользователь и так внизу.
            if (atEnd)
            {
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
        }
        catch (ObjectDisposedException)
        {
            // форма закрывается — строка потеряется, не страшно
        }
    }
}
