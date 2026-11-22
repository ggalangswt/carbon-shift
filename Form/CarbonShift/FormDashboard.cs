namespace CarbonShift;

public sealed class FormDashboard : Form
{
    private readonly AppSession session;
    private readonly ILocalStore store = new LocalStore();
    private readonly ICarbonShiftClient api;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Panel content = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel header = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5, Padding = new Padding(16, 12, 16, 0), Margin = Padding.Empty };
    private readonly ComboBox query = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.None, Margin = new Padding(0, 8, 8, 0), AccessibleName = "Cari dan pilih kota Indonesia", MaxDropDownItems = 8, IntegralHeight = true };
    private readonly TextBox status = UiHelper.Detail();
    private readonly Label feedback = UiHelper.Label("");
    private readonly PageOverview overview;
    private readonly PageForecast forecast;
    private readonly PageScheduler scheduler;
    private readonly PageHistory history;
    private readonly List<Button> navButtons = [];
    private bool working;
    private bool resourcesDisposed;
    private Control? active;

    public FormDashboard(UserAccount user, ILocalStore? localStore = null, ICarbonShiftClient? apiClient = null)
    {
        store = localStore ?? new LocalStore();
        api = apiClient ?? new SupabaseAppClient();
        session = new(user, api);
        api.SessionExpired += SessionExpired;
        Text = "CarbonShift"; ClientSize = new Size(1200, 820); MinimumSize = new Size(980, 680);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        BackColor = UiHelper.Surface; AutoScaleMode = AutoScaleMode.Dpi;
        forecast = new(session);
        scheduler = new(session, () => { feedback.Text = "Jadwal tersimpan. Lihat Riwayat atau Dashboard."; });
        overview = new(session, () => _ = NavigateAsync(scheduler));
        history = new(session, () => { feedback.Text = "Riwayat diperbarui."; });

        var brand = UiHelper.Label("CarbonShift", true);
        var brandSize = TextRenderer.MeasureText(brand.Text, brand.Font);
        var side = new TableLayoutPanel { Dock = DockStyle.Left, Width = Math.Max(208, brandSize.Width + 40),
            ColumnCount = 1, RowCount = 5, BackColor = UiHelper.Hijau, Padding = new Padding(16) };
        side.ColumnStyles.Add(new(SizeType.Percent, 100));
        side.RowStyles.Add(new(SizeType.Absolute, brandSize.Height + 16));
        side.RowStyles.Add(new(SizeType.AutoSize)); side.RowStyles.Add(new(SizeType.AutoSize));
        side.RowStyles.Add(new(SizeType.Percent, 100)); side.RowStyles.Add(new(SizeType.Absolute, 52));
        brand.AutoSize = false; brand.Dock = DockStyle.Fill; brand.Margin = Padding.Empty;
        brand.ForeColor = Color.White; side.Controls.Add(brand, 0, 0);
        var hello = UiHelper.Label("Halo, " + user.Name); hello.ForeColor = Color.Honeydew;
        hello.Dock = DockStyle.Fill; hello.Margin = new Padding(0, 4, 0, 20); side.Controls.Add(hello, 0, 1);
        var navigation = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        navigation.ColumnStyles.Add(new(SizeType.Percent, 100)); side.Controls.Add(navigation, 0, 2);
        void Nav(string text, Control page)
        {
            var button = UiHelper.Button(text, async (_, _) => await NavigateAsync(page));
            button.Dock = DockStyle.Fill; button.MinimumSize = new Size(0, 44); button.Margin = new Padding(0, 4, 0, 4);
            button.ForeColor = Color.White; button.BackColor = UiHelper.Hijau; button.Tag = page;
            button.FlatAppearance.BorderSize = 0; button.TextAlign = ContentAlignment.MiddleLeft;
            button.Padding = new Padding(12, 6, 12, 6);
            navButtons.Add(button); navigation.Controls.Add(button);
        }
        Nav("Dashboard", overview); Nav("Forecast", forecast); Nav("Scheduler", scheduler); Nav("Riwayat", history);
        var logout = UiHelper.Button("Logout", (_, _) => Close());
        logout.ForeColor = Color.White; logout.BackColor = Color.FromArgb(180, 40, 40);
        logout.FlatAppearance.BorderSize = 0; logout.Dock = DockStyle.Fill;
        logout.MinimumSize = new Size(0, 44); logout.Margin = new Padding(0, 4, 0, 4);
        side.Controls.Add(logout, 0, 4);

        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int row = 0; row < 5; row++) header.RowStyles.Add(new(SizeType.AutoSize));
        var locationTitle = UiHelper.Label("Lokasi prakiraan");
        locationTitle.Font = new Font(Font, FontStyle.Bold); locationTitle.Margin = new Padding(0, 0, 0, 4);
        header.Controls.Add(locationTitle);
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        searchRow.RowStyles.Add(new(SizeType.AutoSize));
        searchRow.ColumnStyles.Add(new(SizeType.Percent, 100)); searchRow.ColumnStyles.Add(new(SizeType.AutoSize));
        query.Text = ""; searchRow.Controls.Add(query, 0, 0);
        var searchButton = UiHelper.Button("Cari kota", async (_, _) => await SearchAsync(), true);
        searchButton.Margin = Padding.Empty; searchRow.Controls.Add(searchButton, 1, 0); header.Controls.Add(searchRow);
        query.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !query.DroppedDown)
            { e.SuppressKeyPress = true; await SearchAsync(); }
        };
        query.SelectionChangeCommitted += async (_, _) => await SelectLocationAsync();
        var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0) };
        actions.Controls.Add(UiHelper.Button("Ambil ulang forecast", async (_, _) => await FetchAsync(true)));
        actions.Controls.Add(UiHelper.Button("Coba data contoh", (_, _) =>
        {
            if (working) return;
            if (session.Location == null) session.SetLocation(new("Yogyakarta", "DI Yogyakarta", -7.7956, 110.3695, "Asia/Jakarta"));
            session.SetForecast(DemoForecast.Create(session.Location!, DateTimeOffset.UtcNow));
            feedback.Text = "Mode demo aktif. Hasil tidak dapat disimpan. Ambil ulang forecast untuk kembali ke data nyata.";
        }));
        header.Controls.Add(actions);
        status.BorderStyle = BorderStyle.None; status.ScrollBars = ScrollBars.None;
        status.Dock = DockStyle.Top;
        status.MinimumSize = new Size(0, 46); status.TabStop = false; status.Margin = new Padding(0, 6, 0, 0);
        header.Controls.Add(status);
        feedback.Margin = new Padding(0, 0, 0, 4); header.Controls.Add(feedback);
        header.AutoSize = false; header.Dock = DockStyle.Fill;
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        main.ColumnStyles.Add(new(SizeType.Percent, 100));
        main.RowStyles.Add(new(SizeType.Absolute, 220)); main.RowStyles.Add(new(SizeType.Percent, 100));
        main.Controls.Add(header, 0, 0); main.Controls.Add(content, 0, 1);
        main.Layout += (_, _) =>
        {
            int width = Math.Max(1, main.ClientSize.Width - header.Padding.Horizontal);
            int height = header.Visible ? header.Padding.Vertical + header.Controls.Cast<Control>().Sum(c =>
                c.Margin.Vertical + c.GetPreferredSize(new Size(Math.Max(1, width - c.Margin.Horizontal), 0)).Height) : 0;
            if (main.RowStyles[0].Height != height) main.RowStyles[0].Height = height;
        };
        feedback.MaximumSize = new Size(850, 0);
        Controls.Add(main); Controls.Add(side);
        session.Changed += SessionChanged;
        FormClosing += (_, _) => lifetime.Cancel();
        Shown += async (_, _) =>
        {
            await NavigateAsync(overview);
            try
            {
                var location = await store.ReadAsync<Location>("location_" + ServiceKey() + "_user_" + user.UserId, lifetime.Token);
                if (IsDisposed) return;
                if (location is { IsValid: true })
                {
                    query.Text = location.DisplayName;
                    session.SetLocation(location);
                    await FetchAsync(false);
                }
                else { status.Text = "Belum ada lokasi. Ketik nama kota, cari, lalu pilih hasil pada dropdown."; }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!IsDisposed) feedback.Text = "Pengaturan lokasi gagal dibaca. Pilih kota kembali."; }
        };
        status.Text = "Cari kota dan pilih lokasi untuk memulai.";
    }

    private static string ServiceKey() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(SupabaseAppClient.DefaultAddress.ToString())))[..12];
    private void SessionExpired(object? sender, EventArgs e)
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) Close(); });
    }
    private async Task NavigateAsync(Control page)
    {
        if (IsDisposed) return;
        if (active != page) { content.Controls.Clear(); page.Dock = DockStyle.Fill; content.Controls.Add(page); active = page; }
        header.Visible = page == overview;
        foreach (var button in navButtons)
            button.BackColor = ReferenceEquals(button.Tag, page) ? Color.FromArgb(56, 142, 60) : UiHelper.Hijau;
        await ((IAppPage)page).RefreshAsync();
    }
    private void SessionChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        status.Text = session.Forecast is { } snapshot ? snapshot.Status + "\r\n" + (snapshot.Warning ?? "Forecast siap digunakan untuk menyusun jadwal.") :
            session.Location is { } location ? $"{location.DisplayName} | {location.TimeZoneId}\r\nForecast belum tersedia." : "Pilih lokasi untuk memulai.";
        if (active is IAppPage page) _ = page.RefreshAsync();
    }
    private void Busy(bool value, string message)
    {
        working = value; header.Enabled = !value; feedback.Text = message;
    }
    private async Task SearchAsync()
    {
        if (working) return;
        Busy(true, "Mencari kota...");
        try
        {
            var locations = await api.SearchAsync(query.Text, lifetime.Token);
            if (IsDisposed) return;
            string typed = query.Text;
            query.Items.Clear(); foreach (var location in locations) query.Items.Add(location);
            query.SelectedIndex = -1; query.Text = typed;
            feedback.Text = locations.Count == 0 ? "Kota tidak ditemukan. Coba nama lain." : "Pilih kota dan provinsi dari dropdown untuk memakai lokasi.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!IsDisposed) feedback.Text = "Pencarian kota gagal. Periksa koneksi dan coba lagi (minimal dua karakter)."; }
        finally { if (!IsDisposed) { working = false; header.Enabled = true; } }
        if (!IsDisposed && header.Visible && query.Items.Count > 0) { query.Focus(); query.DroppedDown = true; }
    }
    private async Task SelectLocationAsync()
    {
        if (working) return;
        if (query.SelectedItem is not Location location) { feedback.Text = "Cari dan pilih kota terlebih dahulu."; return; }
        query.DroppedDown = false;
        Busy(true, "Mengatur lokasi...");
        session.SetLocation(location);
        string? warning = null;
        try { await store.WriteAsync("location_" + ServiceKey() + "_user_" + session.User.UserId, location, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception) { warning = "Lokasi aktif, tetapi pengaturan perangkat tidak dapat disimpan."; }
        finally { if (!IsDisposed) { working = false; header.Enabled = true; } }
        if (IsDisposed || lifetime.IsCancellationRequested) return;
        await FetchAsync(false);
        if (!IsDisposed && warning != null) feedback.Text += " " + warning;
    }
    private async Task FetchAsync(bool force)
    {
        if (working) return;
        if (session.Location is not { } location) { feedback.Text = "Pilih lokasi terlebih dahulu."; return; }
        Busy(true, "Mengambil forecast...");
        session.SetForecast(null);
        try
        {
            var response = await api.ForecastAsync(location, force, lifetime.Token);
            var snapshot = response.Forecast;
            if (IsDisposed) return;
            session.SetForecast(snapshot); session.ForecastId = response.ForecastId;
            feedback.Text = snapshot.Warning ?? "Forecast siap. Buka Scheduler untuk membuat jadwal.";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = "Forecast gagal dimuat. " + ex.Message; }
        finally { if (!IsDisposed) { working = false; header.Enabled = true; } }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true;
            lifetime.Cancel(); api.SessionExpired -= SessionExpired; api.Logout(); session.Changed -= SessionChanged;
            overview.Dispose(); forecast.Dispose(); scheduler.Dispose(); history.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
