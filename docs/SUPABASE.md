# Memindahkan CarbonShift ke Supabase

Supabase digunakan sebagai server PostgreSQL. Login CarbonShift tetap memakai tabel `public."user"` dan hash password yang sudah ada. Supabase Auth tidak dipakai. Lima tabel, ID, relasi, akun, dan riwayat dipertahankan.

## Membuat project

1. Masuk ke [dashboard Supabase](https://supabase.com/dashboard), lalu pilih **New project**.
2. Pilih organisasi dan beri nama project, misalnya `carbonshift`.
3. Buat password database yang kuat dan simpan di password manager. Ini bukan password akun CarbonShift.
4. Pilih region Singapore jika tersedia, lalu buat project. Tunggu sampai database siap.
5. Buka pengaturan integrasi **Data API**, lalu matikan **Enable Data API**. Aplikasi saat ini tidak memakai REST, GraphQL, atau SDK Supabase. Jangan mengekspos tabel akun beserta hash password melalui Data API.
6. Buka **Connect**, lalu pilih **Session pooler**. Salin host, port, database, dan username dari panel tersebut. Jangan menebak host berdasarkan region.
7. Gunakan database `postgres` dan port dari panel, biasanya `5432`. Username pooler biasanya berbentuk `postgres.<project-ref>`. Jangan pilih Transaction pooler untuk migrasi ini.
8. Jangan jalankan `schema.psql` atau `seed.sql` di project target. Database Supabase sudah tersedia dan akun lama akan dibawa melalui migrasi.

Centang **Enable RLS on new tables automatically** jika pilihan itu tersedia. Saat migrasi, tetap periksa RLS pada setiap tabel dan hak akses role database yang dipakai.

Session pooler mendukung koneksi dari jaringan IPv4. Koneksi direct bergantung dukungan IPv6 atau add-on IPv4. Lihat [dokumentasi koneksi Supabase](https://supabase.com/docs/guides/database/connecting-to-postgres).

## Menyimpan koneksi secara lokal

Simpan koneksi target di `Form/CarbonShift/supabase.config`. File ini diabaikan Git dan tidak dibundel ke aplikasi. Isi format Npgsql dengan nilai project sendiri.

```text
Host=<host-session-pooler>;Port=5432;Database=postgres;Username=postgres.<project-ref>;Password=<password-database>;SSL Mode=VerifyFull;Root Certificate=<path-sertifikat>;Timeout=15;Command Timeout=30
```

Gunakan tanda kutip ganda pada nilai password yang mengandung titik koma. Jangan menempelkan connection string lengkap ke chat, screenshot, commit, atau GitHub Releases. Publishable key, anon key, dan service role key bukan password PostgreSQL.

Untuk menjalankan aplikasi dari PowerShell setelah migrasi berhasil, gunakan konfigurasi berikut.

```powershell
$env:CARBONSHIFT_DB = (Get-Content -Raw Form/CarbonShift/supabase.config).Trim()
dotnet run --project Form/CarbonShift/CarbonShift.csproj
```

Alternatifnya, isi koneksi target melalui Pengaturan database di halaman Masuk. Dukungan ini memerlukan build aplikasi yang sudah memuat perubahan Supabase. ZIP v1.0.0 yang diterbitkan sebelum perubahan ini belum mendukung nama database Supabase.

Koneksi ke host Supabase memakai TLS dengan verifikasi sertifikat dan hostname. Jika koneksi gagal karena sertifikat, periksa sertifikat CA dan konfigurasi dari Supabase. Jangan mengatasi masalah dengan mematikan verifikasi.

Buka **Database → Settings → SSL Configuration**, aktifkan **Enforce SSL on incoming connections**, lalu pilih **Download certificate**. Sertifikat CA yang dipakai project ini disimpan sebagai `Form/CarbonShift/prod-ca-2021.crt` dan ikut disalin saat build atau publish. Ini sertifikat publik, bukan private key atau password. Jika path sertifikat tidak ditentukan dalam konfigurasi, aplikasi mencari file tersebut di samping executable.

Path absolut pada `Root Certificate` hanya berlaku pada komputer yang memiliki file tersebut. Saat pindah komputer, sesuaikan path atau gunakan sertifikat bawaan di folder aplikasi melalui wizard. Jangan membundel konfigurasi pribadi.

## Urutan migrasi

1. Tutup semua aplikasi CarbonShift yang menulis ke DB sumber.
2. Pastikan DB sumber yang dipilih benar. Cocokkan jumlah akun, aktivitas, forecast, slot, dan rekomendasi.
3. Buat backup lokal dengan `pg_dump` dalam format custom. Simpan di `artifacts/` agar tidak masuk Git. Backup berisi data pengguna dan hash password, sehingga tidak boleh diunggah sebagai aset release.
4. Periksa versi PostgreSQL sumber dan target. Gunakan alat migrasi yang sesuai. Sumber lokal saat persiapan menggunakan PostgreSQL 18. Jangan langsung menganggap dump dari versi lebih baru dapat dipulihkan ke server versi lebih lama.
5. Periksa bahwa kelima tabel CarbonShift belum ada di target. Jika sudah ada, hentikan migrasi dan periksa isinya. Jangan memakai opsi `--clean`, menghapus schema `public`, atau menimpa data secara otomatis.
6. Impor hanya schema dan data CarbonShift. Jangan memindahkan role lokal, kepemilikan lokal, atau schema internal Supabase seperti `auth`, `storage`, dan `extensions`.
7. Pastikan seluruh impor berhasil sebelum memakai target. Pertahankan ID dan nilai identity sequence agar pendaftaran serta penyimpanan berikutnya tidak bertabrakan.
8. Lindungi tabel dengan mencabut akses Data API dan mengaktifkan RLS. Periksa bahwa akun koneksi aplikasi memiliki akses SQL yang memang dibutuhkan. RLS tanpa kebijakan tidak membatasi role pemilik tabel atau role BYPASSRLS.
9. Cocokkan jumlah baris setiap tabel serta relasi dan isi data. Uji login akun lama, daftar akun sementara, simpan jadwal, restart, dan pemisahan riwayat melalui aplikasi.
10. Alihkan konfigurasi aplikasi hanya setelah verifikasi berhasil. Simpan DB lokal sebagai cadangan. Jika ada penulisan baru ke sumber setelah backup dibuat, ambil backup baru sebelum cutover.

Jangan menjalankan seluruh suite integrasi setup database pada project Supabase produksi. Beberapa pengujian membuat dan menghapus database atau role sementara. Gunakan pengujian terbatas pada akun dan data uji khusus.

Panduan resmi tersedia pada [migrasi PostgreSQL ke Supabase](https://supabase.com/kb/guides/migrate-from-postgres) dan [pengamanan Data API](https://supabase.com/docs/guides/api/securing-your-api).

## Batas penggunaan koneksi langsung

Koneksi PostgreSQL langsung masih menggunakan kredensial DB di perangkat pengguna. Pemisahan riwayat oleh query aplikasi bukan pembatasan hak akses antar pengguna pada tingkat database. Jangan membagikan password `postgres` dalam ZIP atau menjadikan konfigurasi ini akses publik.

Untuk distribusi publik, gunakan backend API atau integrasi Supabase Auth dengan kebijakan RLS per pengguna. Perpindahan hosting database saja belum menyediakan perlindungan tersebut.

## Status persiapan

Migrasi dilaksanakan pada 22 November 2026 dari PostgreSQL 18 lokal ke PostgreSQL 17 di Supabase melalui Session pooler dan TLS dengan sertifikat terverifikasi. Backup custom disimpan secara lokal di `artifacts/migration-final-d8dbbf7c151c4204aad1278335ee9e10/`. File backup dan konfigurasi sumber tidak boleh masuk Git atau release.

Schema dibuat dari definisi resmi. Seluruh nilai disalin lewat Npgsql dalam transaksi, termasuk ID, hash password, timestamp, dan relasi. Identity sequence diselaraskan dengan ID yang sudah ada. Metode ini tidak melakukan restore dump PostgreSQL 18 langsung ke server PostgreSQL 17.

Jumlah data setelah migrasi dan pembersihan akun uji sesuai sumber.

| Tabel | Jumlah baris |
| --- | --- |
| user | 2 |
| activity | 1 |
| weather_forecast | 1 |
| time_slot | 4 |
| recommendation | 1 |

Seluruh kolom dibandingkan ulang melalui koneksi baru. Dua pengujian integrasi remote lolos, mencakup register, login, email duplikat, pemisahan riwayat, serta penyimpanan dan pembacaan jadwal besok dan lusa. Akun serta jadwal sementara pengujian dibersihkan. RLS diaktifkan pada lima tabel dan akses tabel serta sequence untuk `anon`, `authenticated`, dan `service_role` dicabut.

Login akun demo hasil migrasi juga diverifikasi. Konfigurasi development dan pengaturan pada profil Windows pengembang sudah diarahkan ke Supabase. DB lokal tidak dihapus. Konfigurasi sumber lokal dicadangkan di folder backup tersebut sebagai `local-source.config`. Jika `CARBONSHIFT_DB` dipasang terpisah di komputer lain, variabel itu tetap memiliki prioritas dan harus diarahkan ke koneksi yang dimaksud.

Pengujian default sesudah perubahan lolos dengan 65 berhasil dan 8 pengujian integrasi dilewati. Script packaging menyertakan sertifikat CA publik, contoh koneksi tanpa kredensial asli, serta panduan pengguna dan pengelola untuk Supabase. Kredensial DB tetap dibagikan secara privat oleh pengelola.
