using Serilog.Events;

namespace TrafficVista.UiLogging;

/// <summary>Virtual rows; only visible items are materialized. UI work per refresh is bounded.</summary>
public sealed class LiveLogView : UserControl
{
    internal const int RowCapacity = 2000;
    internal const int BatchSize = 128;
    private readonly LiveLogBuffer _buffer;
    private readonly LiveLogEntry?[] _rows = new LiveLogEntry[RowCapacity];
    private readonly VirtualLogList _list = new();
    private readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
    private readonly ToolStripButton _pause = new("暂停显示") { CheckOnClick = true };
    private readonly ToolStripButton _follow = new("跟随最新") { CheckOnClick = true, Checked = true };
    private readonly ToolStripLabel _summary = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private int _start;
    private int _count;
    private long _cursor;
    private long _skipped;
    internal int RowCount => _count;
    internal bool Paused { get => _pause.Checked; set => _pause.Checked = value; }
    internal bool Following { get => _follow.Checked; set => _follow.Checked = value; }

    public LiveLogView() : this(AppLogging.LiveLogs) { }
    internal LiveLogView(LiveLogBuffer buffer)
    {
        _buffer = buffer;
        _list.Name = "listView_Logs";
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.VirtualMode = true;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.ShowItemToolTips = true;
        _list.Columns.Add("时间", 105);
        _list.Columns.Add("级别", 60);
        _list.Columns.Add("消息（双击查看详情）", 800);
        _list.RetrieveVirtualItem += (_, e) => e.Item = CreateItem(e.ItemIndex);
        _list.DoubleClick += (_, _) => ShowDetails();
        _list.Resize += (_, _) => _list.Columns[2].Width = Math.Max(250, _list.ClientSize.Width - 190);
        var clear = new ToolStripButton("清空显示");
        clear.Click += (_, _) => ClearDisplay();
        _tools.Items.AddRange(new ToolStripItem[] { new ToolStripLabel("实时日志"), new ToolStripSeparator(), _pause, _follow, clear, new ToolStripSeparator(), _summary });
        Controls.Add(_list);
        Controls.Add(_tools);
        _timer.Tick += (_, _) => FlushPending();
        // A hidden/disposed form has no UI refresh work or background event subscriptions.
        VisibleChanged += (_, _) => { if (Visible && !IsDisposed) _timer.Start(); else _timer.Stop(); };
        UpdateSummary();
    }

    internal void FlushPending()
    {
        if (IsDisposed || _pause.Checked) return;
        var entries = _buffer.ReadAfter(_cursor, BatchSize, out var skipped);
        _skipped += skipped;
        if (entries.Length == 0) { UpdateSummary(); return; }
        var top = _count > 0 && _list.IsHandleCreated ? _list.TopItem?.Index ?? 0 : 0;
        var selected = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : -1;
        var evicted = Math.Max(0, _count + entries.Length - RowCapacity);
        _list.BeginUpdate();
        try
        {
            foreach (var entry in entries)
            {
                if (_count == RowCapacity) { _rows[_start] = entry; _start = (_start + 1) % RowCapacity; }
                else { _rows[(_start + _count) % RowCapacity] = entry; _count++; }
            }
            _cursor = entries[^1].Sequence;
            if (evicted > 0) _list.SelectedIndices.Clear();
            _list.VirtualListSize = _count;
            if (evicted > 0 && selected >= evicted) _list.SelectedIndices.Add(selected - evicted);
            _list.Invalidate();
            if (_follow.Checked) _list.EnsureVisible(_count - 1);
            else if (_list.IsHandleCreated) _list.TopItem = _list.Items[Math.Max(0, top - evicted)];
        }
        finally { _list.EndUpdate(); }
        UpdateSummary();
    }

    internal void ClearDisplay()
    {
        _timer.Stop();
        _list.VirtualListSize = 0;
        Array.Clear(_rows);
        _start = _count = 0;
        _cursor = _buffer.LatestSequence;
        _skipped = 0;
        UpdateSummary();
        if (Visible) _timer.Start();
    }

    private LiveLogEntry Entry(int index) => _rows[(_start + index) % RowCapacity]!;
    private ListViewItem CreateItem(int index)
    {
        if (index < 0 || index >= _count) return new ListViewItem("");
        var entry = Entry(index);
        var level = entry.Level switch { LogEventLevel.Verbose => "跟踪", LogEventLevel.Debug => "调试",
            LogEventLevel.Information => "信息", LogEventLevel.Warning => "警告", LogEventLevel.Error => "错误", _ => "严重" };
        return new ListViewItem(new[] { entry.Time, level, entry.Text.Replace("\r", "").Replace("\n", " ↵ ") })
        { ToolTipText = entry.Text, ForeColor = entry.Level >= LogEventLevel.Error ? Color.Firebrick
            : entry.Level == LogEventLevel.Warning ? Color.DarkOrange : entry.Level <= LogEventLevel.Debug ? Color.Gray : SystemColors.WindowText };
    }

    private void UpdateSummary() => _summary.Text = $"最近 {_count}/{RowCapacity} 条 · 待显示 {Math.Max(0, _buffer.LatestSequence - _cursor)} · 缓冲丢弃 {_buffer.Dropped + _skipped}";
    private void ShowDetails()
    {
        if (_list.SelectedIndices.Count == 0) return;
        var entry = Entry(_list.SelectedIndices[0]);
        var dialog = new Form { Text = $"日志详情 · {entry.Time} · {entry.Level}", Size = new(850, 450), StartPosition = FormStartPosition.CenterParent };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Dock = DockStyle.Fill, Text = entry.Text });
        dialog.Show(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Stop(); _timer.Dispose(); }
        base.Dispose(disposing);
    }
    private sealed class VirtualLogList : ListView { internal VirtualLogList() { DoubleBuffered = true; } }
}
