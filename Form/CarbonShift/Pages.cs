using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace CarbonShift;

public static class UiHelper
{
    public static readonly Color Hijau = Color.FromArgb(27, 94, 32);
    public static readonly Color Surface = Color.FromArgb(246, 249, 246);
    public static Button Button(string text, EventHandler? click = null, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 34),
            Padding = new Padding(6), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(4) };
        if (primary) { b.BackColor = Hijau; b.ForeColor = Color.White; }
        if (click != null) b.Click += click;
        return b;
    }
    public static Label Label(string text, bool title = false) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(4, 8, 4, 4),
        ForeColor = title ? Hijau : Color.FromArgb(40, 52, 42),
        Font = new Font("Segoe UI", title ? 15 : 10, title ? FontStyle.Bold : FontStyle.Regular)
    };
    public static TextBox Detail() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        BackColor = Surface, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(4)
    };
    public static DataGridView Grid(params string[] titles)
    {
        var g = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
            BackgroundColor = Surface, BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false };
        g.ColumnHeadersDefaultCellStyle.BackColor = Hijau;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        for (int i = 0; i < titles.Length; i++) g.Columns.Add("col" + i, titles[i]);
        return g;
    }
    public static Color ScoreColor(double score) => score >= .7 ? Color.FromArgb(220, 242, 220) :
        score >= .4 ? Color.FromArgb(255, 248, 220) : Surface;
    public static DateTime LocalToday(AppSession session) =>
        (session.Location?.LocalNow(DateTimeOffset.UtcNow) ?? DateTimeOffset.Now).Date;
    public static void DateRange(DateTimePicker picker, AppSession session)
    {
        var today = LocalToday(session);
        if (picker.MinDate.Date == today && picker.MaxDate.Date == today.AddDays(2)) return;
        // Broaden first to allow both forward and backward changes of location/date.
        picker.MinDate = DateTimePicker.MinimumDateTime; picker.MaxDate = DateTimePicker.MaximumDateTime;
        picker.Value = picker.Value.Date < today ? today : picker.Value.Date > today.AddDays(2) ? today.AddDays(2) : picker.Value.Date;
        picker.MinDate = today; picker.MaxDate = today.AddDays(2);
    }
    public static IReadOnlyList<WeatherForecast> Day(AppSession session, DateTime date) =>
        session.Forecast?.Hours.Where(h => h.Start.Date == date.Date).OrderBy(h => h.Start).ToArray() ?? [];
}

public interface IAppPage { Task RefreshAsync(); }

public sealed class BarChartPanel : Panel
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<WeatherForecast> Data { get; set; } = [];
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TimeSlot? Highlight { get; set; }
    public BarChartPanel() { DoubleBuffered = true; ResizeRedraw = true; BackColor = UiHelper.Surface; Dock = DockStyle.Fill; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 9);
        g.DrawString("Potensi surya per jam (0–100)", font, Brushes.DarkSlateGray, 8, 5);
        if (Data.Count == 0) { g.DrawString("Pilih lokasi dan ambil forecast untuk melihat grafik.", font, Brushes.Gray, 12, 45); return; }
        int left = 40, top = 30, width = Width - 52, height = Height - 60;
        if (width < 24 || height < 20) return;
        using var pen = new Pen(Color.Gainsboro);
        for (int k = 0; k <= 2; k++)
        {
            float y = top + height - height * k / 2f;
            g.DrawLine(pen, left, y, left + width, y);
            g.DrawString((k * 50).ToString(), font, Brushes.Gray, 4, y - 8);
        }
        float bar = width / 24f;
        foreach (var item in Data)
        {
            float h = (float)item.SolarPotential * height;
            var color = Highlight != null && item.Start >= Highlight.Start && item.Start < Highlight.End
                ? Color.RoyalBlue : UiHelper.Hijau;
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, left + item.Start.Hour * bar + 1, top + height - h, Math.Max(1, bar - 2), h);
        }
        for (int i = 0; i < 24; i += 3)
            g.DrawString(i.ToString("00"), font, Brushes.Gray, left + i * bar, top + height + 5);
    }
}

public sealed class PageForecast : UserControl, IAppPage
{
    private readonly AppSession session;
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dddd, dd MMM yyyy", Width = 245 };
    private readonly BarChartPanel chart = new();
    private readonly DataGridView grid = UiHelper.Grid("Jam", "Suhu (°C)", "Awan (%)", "Radiasi interval (W/m²)", "Potensi surya (0–100)", "Kondisi");
    public PageForecast(AppSession session)
    {
        this.session = session; Dock = DockStyle.Fill; Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Absolute, 205)); root.RowStyles.Add(new(SizeType.Percent, 100));
        root.Controls.Add(UiHelper.Label("Weather & Solar Forecast", true), 0, 0);
        root.Controls.Add(date, 0, 1); root.Controls.Add(chart, 0, 2); root.Controls.Add(grid, 0, 3); Controls.Add(root);
        date.ValueChanged += (_, _) => Render();
    }
    private void Render()
    {
        var day = UiHelper.Day(session, date.Value); grid.Rows.Clear();
        foreach (var h in day)
            grid.Rows.Add(h.Start.ToString("HH:mm"), h.Temperature.ToString("0.0"), h.TutupanAwan.ToString("0"),
                h.Radiasi.ToString("0"), (h.SolarPotential * 100).ToString("0.0"), h.Kondisi);
        chart.Data = day; chart.Invalidate();
    }
    public Task RefreshAsync() { UiHelper.DateRange(date, session); Render(); return Task.CompletedTask; }
}

public sealed class PageScheduler : UserControl, IAppPage
{
    private readonly AppSession session;
    private readonly RecommendationState state = new();
    private Guid? previewId;
    private DateTimeOffset previewExpires;
    private readonly CancellationTokenSource lifetime = new();
    private int revision;
    private readonly ComboBox activity = new() { DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 100 };
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dddd, dd MMM yyyy" };
    private readonly NumericUpDown energy = new() { Minimum = .01m, Maximum = 9999.99m, DecimalPlaces = 2, Increment = .05m, Value = .06m };
    private readonly NumericUpDown duration = new() { Minimum = 1, Maximum = 12, Value = 2 };
    private readonly NumericUpDown earliest = new() { Minimum = 0, Maximum = 23, Value = 6 };
    private readonly NumericUpDown latest = new() { Minimum = 1, Maximum = 24, Value = 18 };
    private readonly ComboBox method = new() { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Nama" };
    private readonly DataGridView grid = UiHelper.Grid("Peringkat", "Rentang", "Green Score (0–100)", "Kategori");
    private readonly BarChartPanel chart = new();
    private readonly TextBox explanation = UiHelper.Detail();
    private readonly Button find, save;
    private readonly TableLayoutPanel input;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private bool busy;
    private readonly Dictionary<string, decimal> presets = new()
    {
        ["Mengisi daya laptop"] = .06m, ["Mencuci pakaian (mesin cuci)"] = .50m,
        ["Menyetrika"] = 1m, ["Mengisi daya motor listrik"] = 2m
    };
    public PageScheduler(AppSession session, Action saved)
    {
        this.session = session; Dock = DockStyle.Fill; Padding = new Padding(8);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 305)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiHelper.Surface };
        input = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
        input.ColumnStyles.Add(new(SizeType.Percent, 100));
        input.Controls.Add(UiHelper.Label("Buat jadwal", true));
        void Field(string label, Control control)
        {
            input.Controls.Add(UiHelper.Label(label)); control.Dock = DockStyle.Top; control.Margin = new Padding(4, 0, 4, 6); input.Controls.Add(control);
        }
        foreach (var name in presets.Keys) activity.Items.Add(name);
        activity.SelectedIndex = 0;
        method.Items.Add(new WeightedCalculator()); method.Items.Add(new SolarOnlyCalculator()); method.SelectedIndex = 0;
        Field("Aktivitas (pilih atau ketik sendiri)", activity); Field("Tanggal pelaksanaan", date);
        Field("Estimasi total energi aktivitas (kWh)", energy); Field("Durasi (jam)", duration);
        Field("Boleh mulai paling awal (jam)", earliest); Field("Harus selesai paling lambat (jam)", latest);
        Field("Metode Green Score", method);
        find = UiHelper.Button("Cari rekomendasi", async (_, _) => await GenerateAsync(), true); find.Dock = DockStyle.Top;
        save = UiHelper.Button("Simpan ke riwayat", async (_, _) => await SaveAsync(saved)); save.Dock = DockStyle.Top; save.Enabled = false;
        input.Controls.Add(find); input.Controls.Add(save); scroll.Controls.Add(input); root.Controls.Add(scroll, 0, 0);
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        right.RowStyles.Add(new(SizeType.Absolute, 185)); right.RowStyles.Add(new(SizeType.Percent, 48));
        right.RowStyles.Add(new(SizeType.Percent, 52));
        right.Controls.Add(chart, 0, 0); right.Controls.Add(grid, 0, 1); right.Controls.Add(explanation, 0, 2);
        root.Controls.Add(right, 1, 0); Controls.Add(root);
        activity.TextChanged += (_, _) => InvalidateResult();
        activity.SelectedIndexChanged += (_, _) => { if (presets.TryGetValue(activity.Text, out var value)) energy.Value = value; };
        foreach (var n in new[] { energy, duration, earliest, latest }) n.ValueChanged += (_, _) => InvalidateResult();
        date.ValueChanged += (_, _) => { InvalidateResult(); RenderChart(); };
        method.SelectedIndexChanged += (_, _) => InvalidateResult();
        session.Changed += SessionChanged;
        timer.Tick += (_, _) =>
        {
            UiHelper.DateRange(date, session);
            if (state.Result is not { } result || busy) return;
            try { if (!result.Forecast.IsDemo && DateTimeOffset.UtcNow >= previewExpires) throw new InvalidOperationException(); RecommendationPolicy.EnsureSaveable(result, DateTimeOffset.UtcNow); }
            catch (InvalidOperationException) when (!result.Forecast.IsDemo) { InvalidateResult("Waktu atau forecast sudah kedaluwarsa. Hitung ulang rekomendasi."); }
        };
        timer.Start();
        InvalidateResult();
    }
    private void SessionChanged(object? sender, EventArgs e) { InvalidateResult(); _ = RefreshAsync(); }
    private void InvalidateResult(string? message = null)
    {
        revision++; previewId = null; state.Invalidate(); save.Enabled = false; grid.Rows.Clear(); chart.Highlight = null; chart.Invalidate();
        explanation.Text = message ?? "Isi aktivitas, pilih tanggal dan rentang waktu, lalu cari rekomendasi.\r\n\r\n" + RecommendationResult.ScoreMeaning;
    }
    private void RenderChart() { chart.Data = UiHelper.Day(session, date.Value); chart.Invalidate(); }
    private async Task GenerateAsync()
    {
        if (busy) return;
        InvalidateResult();
        int current = revision;
        busy = true; input.Enabled = false;
        explanation.Text = "Menghitung rekomendasi...";
        try
        {
            if (session.Forecast == null) throw new InvalidOperationException("Pilih lokasi dan ambil forecast terlebih dahulu.");
            if (!session.Forecast.IsDemo && DateTimeOffset.UtcNow - session.Forecast.FetchedAt > TimeSpan.FromHours(6))
                throw new InvalidOperationException("Forecast kedaluwarsa. Ambil ulang terlebih dahulu.");
            var request = new ScheduleRequest(activity.Text.Trim(), (int)duration.Value, energy.Value,
                DateOnly.FromDateTime(date.Value), (int)earliest.Value, (int)latest.Value);
            var calculator = (GreenScoreCalculator)method.SelectedItem!;
            RecommendationResult result;
            string? serverExplanation = null;
            if (session.Forecast.IsDemo)
                result = RecommendationEngine.Generate(request, session.Forecast, DateTimeOffset.UtcNow, calculator);
            else
            {
                if (session.ForecastId is not Guid forecastId) throw new InvalidOperationException("Ambil ulang forecast sebelum menghitung rekomendasi.");
                var response = await session.Client.PreviewAsync(forecastId, request, calculator is SolarOnlyCalculator ? "solar" : "weighted", lifetime.Token);
                if (IsDisposed || current != revision) return;
                result = response.ToResult(); previewId = response.PreviewId; previewExpires = response.ExpiresAt; serverExplanation = response.Explanation;
            }
            state.Set(result);
            foreach (var (slot, index) in result.Slots.Take(10).Select((s, i) => (s, i)))
            {
                int row = grid.Rows.Add(index + 1, slot.Rentang, (slot.GreenScore * 100).ToString("0.0"), slot.Label);
                grid.Rows[row].DefaultCellStyle.BackColor = UiHelper.ScoreColor(slot.GreenScore);
            }
            chart.Data = result.Forecast.Hours.Where(h => DateOnly.FromDateTime(h.Start.DateTime) == result.Request.Date).ToArray();
            chart.Highlight = result.Best; chart.Invalidate(); explanation.Text = serverExplanation ?? result.Explanation;
            save.Enabled = state.CanSave;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed && current == revision) explanation.Text = ex is ArgumentException or InvalidOperationException or CarbonShiftApiException ? ex.Message : "Rekomendasi belum dapat dimuat. Coba lagi."; }
        finally { busy = false; if (!IsDisposed) { input.Enabled = true; save.Enabled = state.CanSave && previewId != null; } }
    }
    private async Task SaveAsync(Action saved)
    {
        if (busy || !state.CanSave || state.Result is not { } result) return;
        busy = true; input.Enabled = false;
        try
        {
            if (previewId is not Guid id) return;
            await session.Client.SaveAsync(id, lifetime.Token);
            if (IsDisposed) return;
            bool stillCurrent = ReferenceEquals(state.Result, result);
            revision++; previewId = null; state.Invalidate(); save.Enabled = false;
            if (stillCurrent) explanation.Text = "Jadwal berhasil disimpan ke riwayat.\r\n\r\n" + result.Explanation;
            saved();
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                if (ex is CarbonShiftApiException { Status: System.Net.HttpStatusCode.Conflict }) InvalidateResult(ex.Message);
                else explanation.Text = "Jadwal belum dapat disimpan. Coba lagi.\r\n" + ex.Message;
            }
        }
        finally
        {
            busy = false;
            if (!IsDisposed) { input.Enabled = true; save.Enabled = state.CanSave; }
        }
    }
    public Task RefreshAsync() { UiHelper.DateRange(date, session); RenderChart(); return Task.CompletedTask; }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { lifetime.Cancel(); lifetime.Dispose(); session.Changed -= SessionChanged; timer.Dispose(); }
        base.Dispose(disposing);
    }
}

public static class HistoryCsv
{
    public static string Export(IEnumerable<ActivityRecord> rows)
    {
        static string Cell(string value)
        {
            // Quoting alone does not prevent spreadsheet applications from evaluating a formula.
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var text = new StringBuilder("Dibuat,Aktivitas,TanggalPelaksanaan,Lokasi,DurasiJam,EstimasiKWh,Mulai,Selesai,GreenScore,Penjelasan\r\n");
        foreach (var row in rows)
            text.AppendLine(string.Join(",", Cell(row.Dibuat.ToString("yyyy-MM-dd HH:mm")), Cell(row.Nama),
                Cell(row.Start.ToString("yyyy-MM-dd")), Cell(row.Lokasi),
                (row.DurationMinutes / 60m).ToString("0.##", CultureInfo.InvariantCulture),
                row.KWh.ToString("0.00", CultureInfo.InvariantCulture), Cell(row.Start.ToString("yyyy-MM-dd HH:mm")),
                Cell(row.End.ToString("yyyy-MM-dd HH:mm")), (row.GreenScore * 100).ToString("0.00", CultureInfo.InvariantCulture), Cell(row.Explanation)));
        return text.ToString();
    }
}

public sealed class PageHistory : UserControl, IAppPage
{
    private readonly AppSession session;
    private readonly DataGridView grid = UiHelper.Grid("Pelaksanaan", "Aktivitas", "Lokasi", "Durasi (jam)", "Estimasi kWh", "Waktu", "Skor (0–100)");
    private readonly TextBox detail = UiHelper.Detail();
    private readonly Label summary = UiHelper.Label("");
    private readonly Button export, delete, refresh;
    private IReadOnlyList<ActivityRecord> rows = [];
    private bool busy;
    public PageHistory(AppSession session, Action changed)
    {
        this.session = session; Dock = DockStyle.Fill; Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 60)); root.RowStyles.Add(new(SizeType.Percent, 40)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(UiHelper.Label("Riwayat jadwal tersimpan", true), 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        refresh = UiHelper.Button("Muat ulang", async (_, _) => await RefreshAsync());
        export = UiHelper.Button("Export CSV", async (_, _) => await ExportAsync());
        delete = UiHelper.Button("Hapus semua", async (_, _) =>
        {
            if (busy || rows.Count == 0 || MessageBox.Show("Hapus semua jadwal tersimpan milik akun ini?", "Hapus riwayat",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            SetBusy(true);
            try { await session.Client.DeleteHistoryAsync(); changed(); }
            catch (Exception ex) { detail.Text = "Gagal menghapus riwayat: " + ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
            if (!IsDisposed) await RefreshAsync();
        });
        actions.Controls.AddRange([refresh, export, delete]);
        root.Controls.Add(actions, 0, 1); root.Controls.Add(grid, 0, 2); root.Controls.Add(detail, 0, 3); root.Controls.Add(summary, 0, 4);
        Controls.Add(root);
        grid.SelectionChanged += (_, _) => ShowDetail();
        SetBusy(false);
    }
    private void SetBusy(bool value)
    {
        busy = value; refresh.Enabled = !value; export.Enabled = delete.Enabled = !value && rows.Count > 0;
    }
    private void ShowDetail()
    {
        if (grid.CurrentRow?.Tag is not ActivityRecord row) return;
        detail.Text = row.Explanation + (row.Explanation.Contains("Sumber:") ? "" :
            "\r\n\r\nData lama: metode, evidence forecast, dan waktu pengambilan lengkap tidak tersedia.");
    }
    public async Task RefreshAsync()
    {
        if (busy) return;
        SetBusy(true); summary.Text = "Memuat riwayat...";
        rows = []; grid.Rows.Clear(); detail.Clear();
        try
        {
            var loaded = await session.Client.HistoryAsync();
            if (IsDisposed) return;
            rows = loaded;
            foreach (var row in rows)
            {
                int index = grid.Rows.Add(row.Start.ToString("dd MMM yyyy"), row.Nama, row.Lokasi, row.DurationMinutes / 60m,
                    row.KWh.ToString("0.00"), row.Rentang, (row.GreenScore * 100).ToString("0.0"));
                grid.Rows[index].Tag = row;
            }
            summary.Text = rows.Count == 0 ? "Belum ada jadwal. Buat rekomendasi lalu simpan dari Scheduler." :
                $"{rows.Count} jadwal tersimpan | Rata-rata skor {rows.Average(h => h.GreenScore) * 100:0.0}/100";
            ShowDetail();
        }
        catch (Exception ex) { if (!IsDisposed) { summary.Text = "Riwayat gagal dimuat. Gunakan Muat ulang."; detail.Text = ex.Message; } }
        finally { if (!IsDisposed) SetBusy(false); }
    }
    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "riwayat_carbonshift.csv" };
        if (rows.Count == 0 || dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            var displayed = grid.Rows.Cast<DataGridViewRow>().Select(r => (ActivityRecord)r.Tag!).ToArray();
            await File.WriteAllTextAsync(dialog.FileName, HistoryCsv.Export(displayed), new UTF8Encoding(true));
            if (!IsDisposed) summary.Text = "CSV berhasil diekspor.";
        }
        catch (Exception ex) { if (!IsDisposed) summary.Text = "Export gagal: " + ex.Message; }
    }
}

public sealed class PageOverview : UserControl, IAppPage
{
    private readonly AppSession session;
    private readonly Label stats = UiHelper.Label(""), next = UiHelper.Label("");
    private readonly DateTimePicker date = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "dddd, dd MMM yyyy", Width = 240 };
    private readonly BarChartPanel chart = new();
    private bool busy;
    public PageOverview(AppSession session, Action schedule)
    {
        this.session = session; Dock = DockStyle.Fill; Padding = new Padding(16);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(UiHelper.Label("Dashboard", true), 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(UiHelper.Button("Buat jadwal", (_, _) => schedule(), true));
        actions.Controls.Add(UiHelper.Button("Muat ulang statistik", async (_, _) => await RefreshAsync()));
        root.Controls.Add(actions, 0, 1); root.Controls.Add(stats, 0, 2); root.Controls.Add(next, 0, 3); root.Controls.Add(date, 0, 4);
        root.Controls.Add(chart, 0, 5);
        var note = UiHelper.Label(RecommendationResult.ScoreMeaning);
        note.MaximumSize = new Size(700, 0); root.Controls.Add(note, 0, 6); Controls.Add(root);
        next.MaximumSize = new Size(700, 0); stats.MaximumSize = new Size(700, 0);
        date.ValueChanged += (_, _) => RenderChart();
    }
    private void RenderChart() { chart.Data = UiHelper.Day(session, date.Value); chart.Invalidate(); }
    public async Task RefreshAsync()
    {
        UiHelper.DateRange(date, session); RenderChart();
        if (busy) return;
        busy = true; stats.Text = "Memuat statistik..."; next.Text = "";
        try
        {
            var response = await session.Client.DashboardAsync(session.Location?.TimeZoneId ?? "Asia/Jakarta");
            if (IsDisposed) return;
            var result = new DashboardStatistics(response.WeeklyCount, response.AverageScore / 100, response.Next?.ToRecord());
            stats.Text = $"Minggu berjalan (Senin–Minggu): {result.WeeklyCount} jadwal tersimpan\r\nRata-rata Green Score: " +
                (result.AverageScore is { } avg ? $"{avg * 100:0.0}/100" : "Belum ada data");
            next.Text = result.Next is { } n ? $"Jadwal berikutnya: {n.Nama}\r\n{n.Rentang} | {n.Lokasi}" :
                "Belum ada jadwal mendatang dengan zona waktu yang diketahui.";
        }
        catch (Exception) { if (!IsDisposed) stats.Text = "Statistik belum dapat dimuat. Coba lagi."; }
        finally { busy = false; }
    }
}
