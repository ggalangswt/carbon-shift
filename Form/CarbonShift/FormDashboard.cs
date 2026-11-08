using System;
using System.Drawing;
using System.Windows.Forms;

namespace CarbonShift
{
    public class FormDashboard : Form
    {
        private readonly Color warnaSidebar = Color.FromArgb(27, 94, 32);
        private readonly Color warnaAktif = Color.FromArgb(56, 142, 60);

        private Panel pnlContent;
        private Button btnForecast, btnScheduler, btnHistory, btnLogout;
        private ToolStripStatusLabel lblStatus;

        private PageForecast pageForecast = new PageForecast();
        private PageScheduler pageScheduler = new PageScheduler();
        private PageHistory pageHistory = new PageHistory();

        public FormDashboard()
        {
            Text = "CarbonShift - Dashboard";
            ClientSize = new Size(1000, 620);
            MinimumSize = new Size(900, 560);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 10);

            ToolTip tip = new ToolTip();

            // ---------- Sidebar ----------
            Panel pnlSide = new Panel();
            pnlSide.Dock = DockStyle.Left;
            pnlSide.Width = 190;
            pnlSide.BackColor = warnaSidebar;

            Label lblBrand = new Label();
            lblBrand.Text = "CarbonShift";
            lblBrand.ForeColor = Color.White;
            lblBrand.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            lblBrand.AutoSize = true;
            lblBrand.Location = new Point(16, 20);

            Label lblUser = new Label();
            lblUser.Text = "Halo, " + AuthService.NamaUser;
            lblUser.ForeColor = Color.Honeydew;
            lblUser.AutoSize = true;
            lblUser.Location = new Point(18, 56);

            btnForecast = BuatNavButton("Forecast", 100, "Lihat prakiraan cuaca dan potensi surya", tip);
            btnScheduler = BuatNavButton("Scheduler", 146, "Cari waktu terbaik untuk aktivitas listrik", tip);
            btnHistory = BuatNavButton("Riwayat", 192, "Lihat rekomendasi yang pernah disimpan", tip);

            btnForecast.Click += delegate { ShowPage(pageForecast, btnForecast); };
            btnScheduler.Click += delegate { ShowPage(pageScheduler, btnScheduler); };
            btnHistory.Click += delegate { ShowPage(pageHistory, btnHistory); };

            btnLogout = BuatNavButton("Logout", 0, "Kembali ke halaman login", tip);
            btnLogout.Dock = DockStyle.Bottom;
            btnLogout.BackColor = Color.FromArgb(183, 28, 28);
            btnLogout.Click += btnLogout_Click;

            pnlSide.Controls.Add(lblBrand);
            pnlSide.Controls.Add(lblUser);
            pnlSide.Controls.Add(btnForecast);
            pnlSide.Controls.Add(btnScheduler);
            pnlSide.Controls.Add(btnHistory);
            pnlSide.Controls.Add(btnLogout);

            // ---------- Konten ----------
            pnlContent = new Panel();
            pnlContent.Dock = DockStyle.Fill;

            // ---------- Status bar ----------
            StatusStrip ss = new StatusStrip();
            lblStatus = new ToolStripStatusLabel();
            ss.Items.Add(lblStatus);

            // Urutan: Fill dulu, baru Left dan Bottom
            Controls.Add(pnlContent);
            Controls.Add(pnlSide);
            Controls.Add(ss);

            ShowPage(pageForecast, btnForecast);
        }

        private Button BuatNavButton(string teks, int y, string tooltip, ToolTip tip)
        {
            Button b = new Button();
            b.Text = "   " + teks;
            b.TextAlign = ContentAlignment.MiddleLeft;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.ForeColor = Color.White;
            b.BackColor = warnaSidebar;
            b.Size = new Size(190, 46);
            b.Location = new Point(0, y);
            b.Cursor = Cursors.Hand;
            tip.SetToolTip(b, tooltip);
            return b;
        }

        private void ShowPage(Control page, Button aktif)
        {
            pnlContent.Controls.Clear();
            page.Dock = DockStyle.Fill;
            pnlContent.Controls.Add(page);

            foreach (Button b in new Button[] { btnForecast, btnScheduler, btnHistory })
            {
                b.BackColor = (b == aktif) ? warnaAktif : warnaSidebar;
            }

            PageForecast pf = page as PageForecast;
            if (pf != null) pf.Render();

            PageScheduler ps = page as PageScheduler;
            if (ps != null) ps.RefreshChart();

            PageHistory ph = page as PageHistory;
            if (ph != null) ph.Render();

            lblStatus.Text = "Pengguna: " + AuthService.NamaUser +
                             "   |   Sumber data: " + AppState.SumberData +
                             "   |   " + DateTime.Now.ToString("dd MMM yyyy");
        }

        private void btnLogout_Click(object sender, EventArgs e)
        {
            DialogResult r = MessageBox.Show("Yakin ingin logout?", "Logout",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes)
            {
                this.Close();   // kembali ke FormLogin
            }
        }
    }
}