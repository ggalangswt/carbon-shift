namespace CarbonShift;

public sealed class FormLogin : Form
{
    private readonly TextBox tbName = new() { MaxLength = 100 };
    private readonly TextBox tbEmail = new() { MaxLength = 100 };
    private readonly TextBox tbPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox tbConfirmation = new() { UseSystemPasswordChar = true, MaxLength = 128 };
    private readonly CheckBox chkShow = new() { Text = "Tampilkan password", AutoSize = true };
    private readonly Label lblError = UiHelper.Label("");
    private readonly Label title = UiHelper.Label("Masuk", true);
    private readonly Label hint = UiHelper.Label("Akun contoh: admin@carbonshift.com / 12345");
    private readonly Button btnSubmit;
    private readonly LinkLabel switchMode = new() { AutoSize = true, LinkColor = UiHelper.Hijau, Margin = new Padding(4, 12, 4, 4) };
    private readonly TableLayoutPanel nameField, confirmationField;
    private readonly CancellationTokenSource lifetime = new();
    private bool registering, busy, resourcesDisposed;

    public FormLogin()
    {
        Text = "CarbonShift - Masuk"; Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = UiHelper.Surface; MinimumSize = new Size(740, 500); ClientSize = new Size(800, 500);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        root.ColumnStyles.Add(new(SizeType.Absolute, 260)); root.ColumnStyles.Add(new(SizeType.Percent, 100));
        var branding = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = UiHelper.Hijau, Padding = new Padding(24, 48, 24, 24) };
        var brand = UiHelper.Label("CarbonShift", true); brand.ForeColor = Color.White;
        var tagline = UiHelper.Label("Jadwalkan aktivitas listrik sesuai potensi energi surya.");
        tagline.ForeColor = Color.Honeydew; tagline.MaximumSize = new Size(200, 0);
        branding.Controls.Add(brand); branding.Controls.Add(tagline); root.Controls.Add(branding, 0, 0);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1 };
        fields.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Add(Control control)
        {
            fields.RowStyles.Add(new(SizeType.AutoSize));
            fields.Controls.Add(control, 0, fields.RowCount++);
        }
        TableLayoutPanel Field(string label, TextBox input)
        {
            var group = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 6) };
            group.ColumnStyles.Add(new(SizeType.Percent, 100));
            group.RowStyles.Add(new(SizeType.AutoSize)); group.RowStyles.Add(new(SizeType.AutoSize));
            group.Controls.Add(UiHelper.Label(label), 0, 0); input.Dock = DockStyle.Top; group.Controls.Add(input, 0, 1);
            Add(group); return group;
        }
        Add(title);
        nameField = Field("Nama", tbName);
        Field("Email", tbEmail);
        Field("Password", tbPassword);
        confirmationField = Field("Konfirmasi password", tbConfirmation);
        chkShow.Margin = new Padding(4, 8, 4, 4);
        chkShow.CheckedChanged += (_, _) => tbPassword.UseSystemPasswordChar = tbConfirmation.UseSystemPasswordChar = !chkShow.Checked;
        Add(chkShow);
        lblError.ForeColor = Color.Firebrick; lblError.MaximumSize = new Size(410, 0); Add(lblError);
        btnSubmit = UiHelper.Button("Masuk", async (_, _) => await SubmitAsync(), true);
        btnSubmit.Dock = DockStyle.Top; Add(btnSubmit);
        switchMode.LinkClicked += (_, _) => { if (!busy) SetMode(!registering); };
        Add(switchMode);
        hint.Font = new Font(Font.FontFamily, 8); hint.ForeColor = Color.DimGray; Add(hint);
        scroll.Controls.Add(fields); root.Controls.Add(scroll, 1, 0); Controls.Add(root);
        AcceptButton = btnSubmit;
        FormClosing += (_, _) => lifetime.Cancel();
        SetMode(false);
    }

    private void SetMode(bool register)
    {
        registering = register; Text = register ? "CarbonShift - Daftar" : "CarbonShift - Masuk";
        title.Text = register ? "Buat akun" : "Masuk";
        nameField.Visible = confirmationField.Visible = register;
        hint.Visible = !register; tbPassword.MaxLength = register ? 128 : 32767;
        btnSubmit.Text = register ? "Buat akun" : "Masuk";
        switchMode.Text = register ? "Sudah punya akun? Masuk" : "Belum punya akun? Daftar";
        tbPassword.Clear(); tbConfirmation.Clear(); chkShow.Checked = false; lblError.Text = "";
        ClientSize = new Size(ClientSize.Width, register ? 650 : 500);
        if (register) tbName.Focus(); else tbEmail.Focus();
    }

    private async Task SubmitAsync()
    {
        if (busy) return;
        busy = true; SetBusy(true); lblError.ForeColor = UiHelper.Hijau;
        lblError.Text = registering ? "Membuat akun..." : "Memeriksa akun...";
        try
        {
            var user = registering
                ? await AuthService.RegisterAsync(tbName.Text, tbEmail.Text, tbPassword.Text, tbConfirmation.Text, lifetime.Token)
                : await AuthService.LoginAsync(tbEmail.Text, tbPassword.Text, lifetime.Token);
            if (IsDisposed) return;
            tbEmail.Text = user.Email; tbPassword.Clear(); tbConfirmation.Clear(); lblError.Text = "";
            using var dashboard = new FormDashboard(user);
            Hide();
            dashboard.ShowDialog(this);
            if (IsDisposed) return;
            SetBusy(false);
            SetMode(false);
            Show(); tbPassword.Focus();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetBusy(false);
            lblError.ForeColor = Color.Firebrick;
            lblError.Text = ex is ArgumentException ? ex.Message :
                registering ? "Akun belum dapat dibuat. Periksa koneksi database dan coba lagi. Jika email sudah terdaftar, gunakan Masuk." :
                "Tidak dapat masuk. Periksa koneksi dan konfigurasi database.";
            if (ex is AuthValidationException validation)
            {
                Control field = validation.Field switch
                {
                    "name" => tbName, "email" => tbEmail, "confirmation" => tbConfirmation, _ => tbPassword
                };
                field.Focus();
            }
            else tbPassword.Focus();
        }
        finally
        {
            busy = false;
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        btnSubmit.Enabled = switchMode.Enabled = !value;
        tbName.Enabled = tbEmail.Enabled = tbPassword.Enabled = tbConfirmation.Enabled = chkShow.Enabled = !value;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed)
        {
            resourcesDisposed = true; lifetime.Cancel(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}
