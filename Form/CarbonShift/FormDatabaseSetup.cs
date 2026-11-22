namespace CarbonShift;

public sealed class FormDatabaseSetup : Form
{
    private readonly DatabaseConfigurationStore store;
    private readonly DatabaseSetupService service = new();
    private readonly DatabaseConfiguration? candidate;
    private readonly TextBox host = new(), databaseName = new(), username = new(), password = new() { UseSystemPasswordChar = true };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 5432 };
    private readonly TextBox message = UiHelper.Detail();
    private readonly Button test, save, cancel;
    private readonly TableLayoutPanel fields;
    private readonly CancellationTokenSource lifetime = new();
    private bool working, resourcesDisposed;
    private readonly DatabaseSettings initial;
    public FormDatabaseSetup(DatabaseConfigurationStore store, DatabaseConfiguration? candidate = null)
    {
        this.store = store; this.candidate = candidate;
        Text = "CarbonShift - Pengaturan database"; ClientSize = new Size(650, 610); MinimumSize = new Size(620, 600);
        StartPosition = FormStartPosition.CenterParent; Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi; BackColor = UiHelper.Surface;
        try { initial = candidate == null ? DatabaseSettings.Default : DatabaseSettings.Parse(candidate.ConnectionString); }
        catch (ArgumentException) { initial = DatabaseSettings.Default; }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(22) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.AutoSize));
        root.Controls.Add(UiHelper.Label("Hubungkan database", true), 0, 0);
        var note = UiHelper.Label("Gunakan akun PostgreSQL, bukan akun login CarbonShift. Untuk Supabase, isi koneksi Session pooler. PostgreSQL lokal tidak diperlukan jika memakai Supabase.");
        note.MaximumSize = new Size(570, 0); root.Controls.Add(note, 0, 1);
        fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 10, 0, 10) };
        fields.ColumnStyles.Add(new(SizeType.Absolute, 155)); fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Field(string label, Control control)
        {
            int row = fields.RowCount++; fields.RowStyles.Add(new(SizeType.AutoSize));
            fields.Controls.Add(UiHelper.Label(label), 0, row);
            control.Dock = DockStyle.Top; control.Margin = new Padding(4, 8, 4, 6); fields.Controls.Add(control, 1, row);
        }
        Field("Host", host); Field("Port", port); Field("Nama database", databaseName);
        Field("Username", username); Field("Password", password);
        host.Text = initial.Host; port.Value = Math.Clamp(initial.Port, 1, 65535);
        databaseName.Text = initial.DatabaseName; username.Text = initial.Username; password.Text = initial.Password;
        root.Controls.Add(fields, 0, 2);
        message.Text = candidate?.ManagedByEnvironment == true
            ? "Konfigurasi dikelola CARBONSHIFT_DB. Field tidak dapat diubah. Ubah/hapus environment variable tersebut untuk memakai wizard; lalu mulai ulang aplikasi."
            : "Uji koneksi tidak mengubah database. Siapkan dan simpan membuat database/tabel hanya jika belum ada atau kosong. Data lama tidak dihapus.";
        message.TabStop = false; root.Controls.Add(message, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        test = UiHelper.Button("Uji koneksi", async (_, _) => await RunAsync(false));
        save = UiHelper.Button(candidate?.ManagedByEnvironment == true ? "Gunakan koneksi" : "Siapkan dan simpan", async (_, _) => await RunAsync(true), true);
        cancel = UiHelper.Button("Batal", (_, _) => Close());
        actions.Controls.AddRange([test, save, cancel]); root.Controls.Add(actions, 0, 4); Controls.Add(root);
        fields.Enabled = candidate?.ManagedByEnvironment != true;
        AcceptButton = test; CancelButton = cancel;
        FormClosing += (_, _) => lifetime.Cancel();
    }
    private DatabaseSettings ReadSettings() => initial with
    {
        Host = host.Text.Trim(), Port = (int)port.Value, DatabaseName = databaseName.Text.Trim(),
        Username = username.Text.Trim(), Password = password.Text
    };
    private async Task RunAsync(bool prepare)
    {
        if (working) return;
        working = true; fields.Enabled = test.Enabled = save.Enabled = false;
        message.Text = prepare ? "Menyiapkan database..." : "Memeriksa koneksi dan schema...";
        try
        {
            bool managed = candidate?.ManagedByEnvironment == true;
            var settings = managed ? DatabaseSettings.Parse(candidate!.ConnectionString) : ReadSettings();
            var result = prepare && !managed
                ? await service.PrepareAsync(settings, lifetime.Token)
                : await service.InspectAsync(settings, lifetime.Token);
            if (IsDisposed) return;
            message.Text = result.Message;
            if (prepare && result.State == DatabaseReadiness.Ready)
            {
                string value = managed ? candidate!.ConnectionString : settings.ToConnectionString();
                if (!managed) await store.SaveAsync(value, lifetime.Token);
                if (IsDisposed) return;
                Database.Activate(value);
                DialogResult = DialogResult.OK; Close();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) message.Text = DatabaseSetupService.FriendlyError(ex); }
        finally
        {
            working = false;
            if (!IsDisposed) { fields.Enabled = candidate?.ManagedByEnvironment != true; test.Enabled = save.Enabled = true; }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
}
