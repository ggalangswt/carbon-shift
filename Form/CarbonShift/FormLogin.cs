using System;
using System.Drawing;
using System.Windows.Forms;

namespace CarbonShift
{
    public class FormLogin : Form
    {
        private TextBox tbEmail;
        private TextBox tbPassword;
        private CheckBox chkShow;
        private Label lblError;
        private Button btnLogin;
        private Button btnClose;

        public FormLogin()
        {
            Text = "CarbonShift - Login";
            ClientSize = new Size(700, 400);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            BackColor = Color.White;
            Font = new Font("Segoe UI", 10);

            // ---------- Panel kiri (branding) ----------
            Panel pnlLeft = new Panel();
            pnlLeft.Dock = DockStyle.Left;
            pnlLeft.Width = 280;
            pnlLeft.BackColor = Color.FromArgb(27, 94, 32);

            Label lblBrand = new Label();
            lblBrand.Text = "CarbonShift";
            lblBrand.ForeColor = Color.White;
            lblBrand.Font = new Font("Segoe UI", 22, FontStyle.Bold);
            lblBrand.AutoSize = true;
            lblBrand.Location = new Point(24, 130);

            Label lblTag = new Label();
            lblTag.Text = "Jadwalkan penggunaan listrik\ndi waktu paling hijau.";
            lblTag.ForeColor = Color.Honeydew;
            lblTag.AutoSize = true;
            lblTag.Location = new Point(28, 185);

            pnlLeft.Controls.Add(lblBrand);
            pnlLeft.Controls.Add(lblTag);

            // ---------- Form kanan ----------
            Label lblHeader = new Label();
            lblHeader.Text = "Masuk";
            lblHeader.Font = new Font("Segoe UI", 18, FontStyle.Bold);
            lblHeader.ForeColor = Color.FromArgb(27, 94, 32);
            lblHeader.AutoSize = true;
            lblHeader.Location = new Point(330, 50);

            Label lblEmail = new Label();
            lblEmail.Text = "Email";
            lblEmail.AutoSize = true;
            lblEmail.Location = new Point(330, 105);

            tbEmail = new TextBox();
            tbEmail.Location = new Point(330, 130);
            tbEmail.Width = 320;

            Label lblPassword = new Label();
            lblPassword.Text = "Password";
            lblPassword.AutoSize = true;
            lblPassword.Location = new Point(330, 170);

            tbPassword = new TextBox();
            tbPassword.Location = new Point(330, 195);
            tbPassword.Width = 320;
            tbPassword.UseSystemPasswordChar = true;

            chkShow = new CheckBox();
            chkShow.Text = "Tampilkan password";
            chkShow.AutoSize = true;
            chkShow.Location = new Point(330, 230);
            chkShow.CheckedChanged += delegate { tbPassword.UseSystemPasswordChar = !chkShow.Checked; };

            lblError = new Label();
            lblError.ForeColor = Color.Firebrick;
            lblError.AutoSize = true;
            lblError.Location = new Point(330, 262);

            btnLogin = new Button();
            btnLogin.Text = "Login";
            btnLogin.Location = new Point(330, 295);
            btnLogin.Size = new Size(150, 38);
            btnLogin.BackColor = Color.ForestGreen;
            btnLogin.ForeColor = Color.White;
            btnLogin.FlatStyle = FlatStyle.Flat;
            btnLogin.Click += btnLogin_Click;

            btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.Location = new Point(495, 295);
            btnClose.Size = new Size(155, 38);
            btnClose.Click += btnClose_Click;

            Label lblHint = new Label();
            lblHint.Text = "Demo: admin@carbonshift.com / 12345";
            lblHint.ForeColor = Color.Gray;
            lblHint.Font = new Font("Segoe UI", 8);
            lblHint.AutoSize = true;
            lblHint.Location = new Point(330, 350);

            Controls.Add(pnlLeft);
            Controls.Add(lblHeader);
            Controls.Add(lblEmail);
            Controls.Add(tbEmail);
            Controls.Add(lblPassword);
            Controls.Add(tbPassword);
            Controls.Add(chkShow);
            Controls.Add(lblError);
            Controls.Add(btnLogin);
            Controls.Add(btnClose);
            Controls.Add(lblHint);

            AcceptButton = btnLogin;   // Enter = Login
            CancelButton = btnClose;   // Esc = Close
        }

        private void btnLogin_Click(object sender, EventArgs e)
        {
            string pesan;
            if (AuthService.Login(tbEmail.Text.Trim(), tbPassword.Text, out pesan))
            {
                lblError.Text = "";
                FormDashboard dashboard = new FormDashboard();
                this.Hide();
                dashboard.ShowDialog();   // tunggu sampai dashboard ditutup
                dashboard.Dispose();
                tbPassword.Clear();
                this.Show();              // kembali ke login
            }
            else
            {
                lblError.Text = pesan.Length > 45 ? pesan.Substring(0, 42) + "..." : pesan;
                if (pesan.Length > 45)
                {
                    MessageBox.Show(pesan, "Login Gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                tbPassword.Clear();
                tbPassword.Focus();
            }
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            this.Close();
        }
    }
}