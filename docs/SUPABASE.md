# Supabase CarbonShift

Arsitektur saat ini menggunakan Supabase Auth, REST/RPC, RLS, dan Edge Functions. WinForms tidak memakai koneksi Npgsql atau wizard database. Langkah penyiapan, penerapan schema, deployment, dan pemeriksaan hak akses tersedia dalam [Panduan Pengelola](PENGELOLA.md).

Database hosted sudah berisi data hasil perpindahan dari PostgreSQL lokal. Lima tabel dan ID lama dipertahankan. Migrasi Auth pada folder `supabase/migrations` menambah hubungan UUID Auth ke akun aplikasi, beserta fungsi dan schema internal yang diperlukan. Migrasi Auth dan Edge Functions sudah diaktifkan. Migrasi cleanup lanjutan membersihkan akun uji lama yang tidak terhubung tanpa menghapus akun Supabase Auth.

Panduan bagi pengguna aplikasi tersedia dalam [Panduan Pengguna](PANDUAN_PENGGUNA.md).
