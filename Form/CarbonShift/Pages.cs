using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace CarbonShift
{
    // ===================== HELPER UI =====================

    public static class UiHelper
    {
        public static readonly Color Hijau = Color.FromArgb(27, 94, 32);

        public static Color WarnaSkor(double skor)
        {
            if (skor >= 0.7) return Color.FromArgb(220, 242, 220);
            if (skor >= 0.4) return Color.FromArgb(255, 248, 220);
            return Color.White;
        }

        public static void StyleGrid(DataGridView g)
        {
            g.ReadOnly = true;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersVisible = false;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Hijau;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9, FontStyle.Bold);
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = 32;
        }
    }

    // ===================== GRAFIK BATANG =====================

    public class BarChartPanel : Panel
    {
        public List<WeatherForecast> Data = new List<WeatherForecast>();
        public int HighlightStart = -1;
        public int HighlightEnd = -1;

        public BarChartPanel()
        {
            this.DoubleBuffered = true;
            this.ResizeRedraw = true;
            this.BackColor = Color.White;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Data == null || Data.Count == 0) return;

            int left = 40, right = 10, top = 28, bottom = 26;
            int w = Width - left - right;
            int h = Height - top - bottom;
            if (w <= 0 || h <= 0) return;

            using (Font small = new Font("Segoe UI", 8))
            using (Font bold = new Font("Segoe UI", 9, FontStyle.Bold))
            using (Pen grid = new Pen(Color.Gainsboro))
            {
                g.DrawString("Potensi Surya per Jam (0 - 1)", bold, Brushes.DimGray, 8, 4);

                if (HighlightStart >= 0)
                {
                    g.DrawString("Biru = jendela rekomendasi", small, Brushes.RoyalBlue, left + w - 150, 6);
                }

                // garis bantu 0, 0.5, 1.0
                for (int k = 0; k <= 2; k++)
                {
                    float y = top + h - (h * k / 2f);
                    g.DrawLine(grid, left, y, left + w, y);
                    g.DrawString((k / 2f).ToString("0.0", CultureInfo.InvariantCulture), small, Brushes.Gray, 8, y - 7);
                }

                float barW = (float)w / Data.Count;
                for (int i = 0; i < Data.Count; i++)
                {
                    WeatherForecast d = Data[i];
                    float bh = (float)(d.SolarPotential * h);
                    RectangleF r = new RectangleF(left + i * barW + 2, top + h - bh, barW - 4, bh);

                    Color c;
                    if (HighlightStart >= 0 && d.Jam >= HighlightStart && d.Jam < HighlightEnd)
                        c = Color.DodgerBlue;
                    else if (d.SolarPotential >= 0.7)
                        c = Color.ForestGreen;
                    else if (d.SolarPotential >= 0.4)
                        c = Color.Goldenrod;
                    else
                        c = Color.Silver;

                    using (SolidBrush b = new SolidBrush(c))
                    {
                        g.FillRectangle(b, r);
                    }

                    if (i % 2 == 0)
                    {
                        g.DrawString(d.Jam.ToString("00"), small, Brushes.Gray, left + i * barW, top + h + 5);
                    }
                }
            }
        }
    }

    // ===================== HALAMAN 1: FORECAST =====================

    public class PageForecast : UserControl
    {
        private BarChartPanel chart;
        private DataGridView dgv;
        private Button btnLive;
        private Label lblStatus;

        public PageForecast()
        {
            this.BackColor = Color.White;

            dgv = new DataGridView();
            dgv.Dock = DockStyle.Fill;
            UiHelper.StyleGrid(dgv);
            dgv.Columns.Add("jam", "Jam");
            dgv.Columns.Add("awan", "Tutupan Awan (%)");
            dgv.Columns.Add("rad", "Radiasi (W/m2)");
            dgv.Columns.Add("surya", "Potensi Surya (0-1)");
            dgv.Columns.Add("kondisi", "Kondisi");

            chart = new BarChartPanel();
            chart.Dock = DockStyle.Top;
            chart.Height = 210;

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 70;

            Label title = new Label();
            title.Text = "Weather & Solar Forecast";
            title.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            title.ForeColor = UiHelper.Hijau;
            title.AutoSize = true;
            title.Location = new Point(16, 10);

            lblStatus = new Label();
            lblStatus.AutoSize = true;
            lblStatus.ForeColor = Color.Gray;
            lblStatus.Location = new Point(18, 44);

            btnLive = new Button();
            btnLive.Text = "Ambil Data Live";
            btnLive.Size = new Size(150, 32);
            btnLive.Location = new Point(330, 14);
            btnLive.BackColor = Color.ForestGreen;
            btnLive.ForeColor = Color.White;
            btnLive.FlatStyle = FlatStyle.Flat;
            btnLive.Click += btnLive_Click;

            top.Controls.Add(title);
            top.Controls.Add(lblStatus);
            top.Controls.Add(btnLive);

            // Urutan: Fill dulu, baru Top (yang terakhir ditambah berada paling atas)
            Controls.Add(dgv);
            Controls.Add(chart);
            Controls.Add(top);
        }

        public void Render()
        {
            dgv.Rows.Clear();
            foreach (WeatherForecast f in AppState.Forecast)
            {
                int idx = dgv.Rows.Add(
                    string.Format("{0:00}:00", f.Jam),
                    f.TutupanAwan.ToString("0"),
                    f.Radiasi.ToString("0"),
                    f.SolarPotential.ToString("0.00"),
                    f.Kondisi);
                dgv.Rows[idx].DefaultCellStyle.BackColor = UiHelper.WarnaSkor(f.SolarPotential);
            }

            chart.Data = AppState.Forecast;
            chart.HighlightStart = -1;
            chart.Invalidate();
            lblStatus.Text = "Sumber data: " + AppState.SumberData;
        }

        private async void btnLive_Click(object sender, EventArgs e)
        {
            btnLive.Enabled = false;
            lblStatus.Text = "Mengambil data dari Open-Meteo...";

            try
            {
                AppState.Forecast = await WeatherService.GetLiveAsync();
                AppState.SumberData = "Open-Meteo (live)";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal mengambil data live. Data sebelumnya tetap dipakai.\n\n" + ex.Message,
                    "Koneksi Gagal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            btnLive.Enabled = true;
            Render();
        }
    }

    // ===================== HALAMAN 2: SCHEDULER =====================

    public class PageScheduler : UserControl
    {
        private ComboBox cbActivity, cbMetode;
        private NumericUpDown nudKwh, nudDurasi, nudMulai, nudSelesai;
        private Button btnCari, btnSimpan;
        private DataGridView dgv;
        private BarChartPanel chart;
        private Label lblBest;
        private TimeSlot terbaik;
        private List<TimeSlot> hasilSlot = new List<TimeSlot>();

        private readonly Dictionary<string, double> preset = new Dictionary<string, double>
        {
            { "Mengisi daya laptop", 0.06 },
            { "Mencuci pakaian (mesin cuci)", 0.50 },
            { "Menyetrika", 1.00 },
            { "Mengisi daya motor listrik", 2.00 }
        };

        public PageScheduler()
        {
            this.BackColor = Color.White;

            // ---------- Panel kiri: input ----------
            Panel pnlInput = new Panel();
            pnlInput.Dock = DockStyle.Left;
            pnlInput.Width = 290;
            pnlInput.BackColor = Color.FromArgb(245, 248, 245);

            Label title = new Label();
            title.Text = "Smart Activity Scheduler";
            title.Font = new Font("Segoe UI", 13, FontStyle.Bold);
            title.ForeColor = UiHelper.Hijau;
            title.AutoSize = true;
            title.Location = new Point(16, 14);

            cbActivity = new ComboBox();
            cbActivity.DropDownStyle = ComboBoxStyle.DropDownList;
            cbActivity.Location = new Point(20, 84);
            cbActivity.Width = 250;
            foreach (string nama in preset.Keys) cbActivity.Items.Add(nama);
            cbActivity.SelectedIndexChanged += cbActivity_SelectedIndexChanged;

            nudKwh = new NumericUpDown();
            nudKwh.Location = new Point(20, 146);
            nudKwh.Width = 250;
            nudKwh.DecimalPlaces = 2;
            nudKwh.Increment = 0.05M;
            nudKwh.Minimum = 0.01M;
            nudKwh.Maximum = 50M;
            nudKwh.Value = 0.06M;

            nudDurasi = new NumericUpDown();
            nudDurasi.Location = new Point(20, 208);
            nudDurasi.Width = 250;
            nudDurasi.Minimum = 1;
            nudDurasi.Maximum = 12;
            nudDurasi.Value = 2;

            nudMulai = new NumericUpDown();
            nudMulai.Location = new Point(20, 270);
            nudMulai.Width = 250;
            nudMulai.Minimum = 0;
            nudMulai.Maximum = 23;
            nudMulai.Value = 6;

            nudSelesai = new NumericUpDown();
            nudSelesai.Location = new Point(20, 332);
            nudSelesai.Width = 250;
            nudSelesai.Minimum = 1;
            nudSelesai.Maximum = 24;
            nudSelesai.Value = 18;

            cbMetode = new ComboBox();
            cbMetode.DropDownStyle = ComboBoxStyle.DropDownList;
            cbMetode.Location = new Point(20, 394);
            cbMetode.Width = 250;
            cbMetode.DisplayMember = "Nama";
            cbMetode.Items.Add(new WeightedCalculator());
            cbMetode.Items.Add(new SolarOnlyCalculator());
            cbMetode.SelectedIndex = 0;

            btnCari = new Button();
            btnCari.Text = "Cari Rekomendasi";
            btnCari.Location = new Point(20, 440);
            btnCari.Size = new Size(250, 38);
            btnCari.BackColor = Color.ForestGreen;
            btnCari.ForeColor = Color.White;
            btnCari.FlatStyle = FlatStyle.Flat;
            btnCari.Click += btnCari_Click;

            btnSimpan = new Button();
            btnSimpan.Text = "Simpan ke Riwayat";
            btnSimpan.Location = new Point(20, 485);
            btnSimpan.Size = new Size(250, 34);
            btnSimpan.Enabled = false;
            btnSimpan.Click += btnSimpan_Click;

            pnlInput.Controls.Add(title);
            pnlInput.Controls.Add(BuatLabel("Aktivitas", 62));
            pnlInput.Controls.Add(cbActivity);
            pnlInput.Controls.Add(BuatLabel("Perkiraan energi (kWh, boleh diubah)", 124));
            pnlInput.Controls.Add(nudKwh);
            pnlInput.Controls.Add(BuatLabel("Durasi (jam)", 186));
            pnlInput.Controls.Add(nudDurasi);
            pnlInput.Controls.Add(BuatLabel("Boleh mulai paling awal (jam)", 248));
            pnlInput.Controls.Add(nudMulai);
            pnlInput.Controls.Add(BuatLabel("Harus selesai paling lambat (jam)", 310));
            pnlInput.Controls.Add(nudSelesai);
            pnlInput.Controls.Add(BuatLabel("Metode Green Score", 372));
            pnlInput.Controls.Add(cbMetode);
            pnlInput.Controls.Add(btnCari);
            pnlInput.Controls.Add(btnSimpan);

            // ---------- Panel kanan: hasil ----------
            Panel pnlRight = new Panel();
            pnlRight.Dock = DockStyle.Fill;

            dgv = new DataGridView();
            dgv.Dock = DockStyle.Fill;
            UiHelper.StyleGrid(dgv);
            dgv.Columns.Add("rank", "Peringkat");
            dgv.Columns.Add("rentang", "Rentang Waktu");
            dgv.Columns.Add("skor", "Green Score");
            dgv.Columns.Add("label", "Kategori");

            Panel pnlBest = new Panel();
            pnlBest.Dock = DockStyle.Bottom;
            pnlBest.Height = 135;
            pnlBest.BackColor = Color.FromArgb(232, 245, 233);
            pnlBest.Padding = new Padding(12);

            lblBest = new Label();
            lblBest.Dock = DockStyle.Fill;
            lblBest.Font = new Font("Segoe UI", 10);
            lblBest.Text = "Isi form di kiri lalu klik \"Cari Rekomendasi\".";
            pnlBest.Controls.Add(lblBest);

            chart = new BarChartPanel();
            chart.Dock = DockStyle.Top;
            chart.Height = 200;

            pnlRight.Controls.Add(dgv);
            pnlRight.Controls.Add(pnlBest);
            pnlRight.Controls.Add(chart);

            Controls.Add(pnlRight);
            Controls.Add(pnlInput);

            cbActivity.SelectedIndex = 0;
        }

        private Label BuatLabel(string teks, int y)
        {
            Label l = new Label();
            l.Text = teks;
            l.AutoSize = true;
            l.Location = new Point(20, y);
            return l;
        }

        public void RefreshChart()
        {
            chart.Data = AppState.Forecast;
            chart.HighlightStart = -1;
            chart.Invalidate();
        }

        private void cbActivity_SelectedIndexChanged(object sender, EventArgs e)
        {
            double kwh;
            if (cbActivity.SelectedItem != null && preset.TryGetValue(cbActivity.SelectedItem.ToString(), out kwh))
            {
                nudKwh.Value = (decimal)kwh;
            }
        }

        private void btnCari_Click(object sender, EventArgs e)
        {
            int durasi = (int)nudDurasi.Value;
            int mulai = (int)nudMulai.Value;
            int selesai = (int)nudSelesai.Value;

            if (selesai - mulai < durasi)
            {
                MessageBox.Show("Rentang waktu lebih pendek dari durasi aktivitas.\nPerlebar rentang atau kurangi durasi.",
                    "Input Tidak Valid", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            GreenScoreCalculator calculator = (GreenScoreCalculator)cbMetode.SelectedItem;
            List<TimeSlot> slots = RecommendationEngine.Generate(AppState.Forecast, durasi, mulai, selesai, calculator);
            if (slots.Count == 0)
            {
                MessageBox.Show("Tidak ada slot yang cocok pada data prakiraan.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            dgv.Rows.Clear();
            for (int i = 0; i < slots.Count && i < 10; i++)
            {
                int idx = dgv.Rows.Add(i + 1, slots[i].Rentang, slots[i].GreenScore.ToString("0.00"), slots[i].Label);
                dgv.Rows[idx].DefaultCellStyle.BackColor = UiHelper.WarnaSkor(slots[i].GreenScore);
            }

            terbaik = slots[0];
            hasilSlot = slots;

            chart.Data = AppState.Forecast;
            chart.HighlightStart = terbaik.StartHour;
            chart.HighlightEnd = terbaik.EndHour;
            chart.Invalidate();

            lblBest.Text = string.Format("Waktu terbaik: {0}   |   Green Score: {1:0.00} ({2})\nMetode: {3}   |   {4}\n{5}\n{6}",
                terbaik.Rentang, terbaik.GreenScore, terbaik.Label, calculator.Nama, calculator.Rumus, terbaik.Bukti, terbaik.Explanation);

            btnSimpan.Enabled = true;
        }

        private void btnSimpan_Click(object sender, EventArgs e)
        {
            if (terbaik == null) return;

            try
            {
                int id = RecommendationRepository.Save(
                    AuthService.UserId,
                    cbActivity.Text,
                    (int)nudDurasi.Value,
                    (double)nudKwh.Value,
                    hasilSlot,
                    terbaik,
                    AppState.Forecast,
                    WeatherService.Lokasi);

                btnSimpan.Enabled = false;
                MessageBox.Show("Rekomendasi disimpan ke database (recommendation_id = " + id + ").", "Tersimpan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Gagal menyimpan ke database:\n" + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ===================== HALAMAN 3: RIWAYAT =====================

    public class PageHistory : UserControl
    {
        private DataGridView dgv;
        private Label lblSummary;

        public PageHistory()
        {
            this.BackColor = Color.White;

            dgv = new DataGridView();
            dgv.Dock = DockStyle.Fill;
            UiHelper.StyleGrid(dgv);
            dgv.Columns.Add("dibuat", "Dibuat");
            dgv.Columns.Add("nama", "Aktivitas");
            dgv.Columns.Add("durasi", "Durasi (jam)");
            dgv.Columns.Add("kwh", "Energi (kWh)");
            dgv.Columns.Add("rentang", "Waktu Terbaik");
            dgv.Columns.Add("skor", "Green Score");
            dgv.Columns.Add("surya", "Perkiraan Porsi Surya (kWh)");

            lblSummary = new Label();
            lblSummary.Dock = DockStyle.Bottom;
            lblSummary.Height = 36;
            lblSummary.TextAlign = ContentAlignment.MiddleLeft;
            lblSummary.Padding = new Padding(12, 0, 0, 0);
            lblSummary.BackColor = Color.FromArgb(232, 245, 233);

            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 60;

            Label title = new Label();
            title.Text = "Riwayat Rekomendasi";
            title.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            title.ForeColor = UiHelper.Hijau;
            title.AutoSize = true;
            title.Location = new Point(16, 14);

            Button btnExport = new Button();
            btnExport.Text = "Export CSV";
            btnExport.Size = new Size(110, 32);
            btnExport.Location = new Point(330, 14);
            btnExport.BackColor = Color.ForestGreen;
            btnExport.ForeColor = Color.White;
            btnExport.FlatStyle = FlatStyle.Flat;
            btnExport.Click += btnExport_Click;

            Button btnHapus = new Button();
            btnHapus.Text = "Hapus Semua";
            btnHapus.Size = new Size(110, 32);
            btnHapus.Location = new Point(450, 14);
            btnHapus.Click += btnHapus_Click;

            top.Controls.Add(title);
            top.Controls.Add(btnExport);
            top.Controls.Add(btnHapus);

            Controls.Add(dgv);
            Controls.Add(lblSummary);
            Controls.Add(top);
        }

        public void Render()
        {
            dgv.Rows.Clear();
            double totalSkor = 0, totalSurya = 0;

            try
            {
                AppState.History = RecommendationRepository.GetHistory(AuthService.UserId);
            }
            catch (Exception ex)
            {
                AppState.History = new List<ActivityRecord>();
                lblSummary.Text = "Gagal memuat riwayat dari database: " + ex.Message;
                return;
            }

            foreach (ActivityRecord h in AppState.History)
            {
                dgv.Rows.Add(
                    h.Dibuat.ToString("dd/MM/yyyy HH:mm"),
                    h.Nama,
                    h.DurasiJam,
                    h.KWh.ToString("0.00"),
                    h.Rentang,
                    h.GreenScore.ToString("0.00"),
                    h.PorsiSurya.ToString("0.00"));
                totalSkor += h.GreenScore;
                totalSurya += h.PorsiSurya;
            }

            int n = AppState.History.Count;
            if (n == 0)
            {
                lblSummary.Text = "Belum ada riwayat. Simpan rekomendasi dari halaman Scheduler.";
            }
            else
            {
                lblSummary.Text = string.Format("Total aktivitas: {0}   |   Rata-rata Green Score: {1:0.00}   |   Perkiraan energi surya terpakai: {2:0.00} kWh",
                    n, totalSkor / n, totalSurya);
            }
        }

        private void btnExport_Click(object sender, EventArgs e)
        {
            if (AppState.History.Count == 0)
            {
                MessageBox.Show("Belum ada data untuk diexport.", "Info",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "CSV (*.csv)|*.csv";
                dlg.FileName = "riwayat_carbonshift.csv";

                if (dlg.ShowDialog() != DialogResult.OK) return;

                using (StreamWriter sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
                {
                    sw.WriteLine("Dibuat,Aktivitas,DurasiJam,kWh,WaktuTerbaik,GreenScore");
                    foreach (ActivityRecord h in AppState.History)
                    {
                        sw.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "{0:yyyy-MM-dd HH:mm},\"{1}\",{2},{3:0.00},{4},{5:0.00}",
                            h.Dibuat, h.Nama, h.DurasiJam, h.KWh, h.Rentang, h.GreenScore));
                    }
                }

                MessageBox.Show("Berhasil diexport.", "Export CSV",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void btnHapus_Click(object sender, EventArgs e)
        {
            if (AppState.History.Count == 0) return;

            DialogResult r = MessageBox.Show("Hapus seluruh riwayat?", "Konfirmasi",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                try
                {
                    RecommendationRepository.DeleteHistory(AuthService.UserId);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Gagal menghapus riwayat:\n" + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Render();
            }
        }
    }
}