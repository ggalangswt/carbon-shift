# CarbonShift

**Aplikasi penjadwalan energi pintar yang merekomendasikan waktu terbaik untuk menggunakan listrik berdasarkan potensi energi terbarukan dan prakiraan cuaca.**

## Anggota Kelompok

**Ketua Kelompok** Nabila Putri barokah - 24/541890/TK/60132

**Anggota 1** Galang Swastika Ramadhan - 24/538251/TK/59692

**Anggota 2** Farand Hafiz - 24/540618/TK/60027

## MVP

- Daftar akun dan login PostgreSQL dengan password PBKDF2, serta riwayat terpisah per akun.
- Pencarian kota Indonesia. Lokasi terakhir disimpan per akun di perangkat.
- Forecast per jam untuk hari ini, besok, dan lusa, mengikuti WIB/WITA/WIT.
- Preset atau aktivitas custom, durasi 1–12 jam, rentang satu hari, dan estimasi total energi aktivitas (kWh).
- Green Score 0–100, sepuluh alternatif teratas, dan penjelasan komponen perhitungan.
- Simpan rekomendasi terbaik, riwayat lintas sesi, detail evidence, export CSV, dan hapus seluruh riwayat akun.
- Dashboard jumlah jadwal minggu berjalan (Senin–Minggu), rata-rata skor, jadwal berikutnya, dan grafik surya.

Green Score adalah **skor kesesuaian waktu dengan potensi surya**, bukan persentase listrik terbarukan, energi surya yang benar-benar dipakai, atau pengurangan emisi. Jadwal tersimpan belum berarti aktivitas sudah dilakukan.

## Menjalankan aplikasi

Untuk memakai paket submisi, ekstrak seluruh ZIP, pasang PostgreSQL lokal jika belum ada, lalu jalankan `CarbonShift.exe`. Paket self-contained menyertakan .NET runtime. Wizard membantu menguji koneksi, membuat DB/tabel jika belum ada/kosong, dan menyimpan konfigurasi pada profil Windows. Lihat [Panduan Pengguna](docs/PANDUAN_PENGGUNA.md).

### Menjalankan dari source (developer)

Penjelasan API, pengolahan forecast, rumus Green Score, contoh hitungan, dan penyimpanan hasil tersedia di [Alur Forecast, Green Score, dan Rekomendasi](docs/ALUR_REKOMENDASI.md). Alur daftar, login, dan pemisahan data pengguna dijelaskan di [Akun dan Autentikasi](docs/AKUN_DAN_AUTENTIKASI.md).

Butuh Windows, .NET SDK 10, dan PostgreSQL. Gunakan database kelompok yang sudah ada. Perintah berikut hanya untuk instalasi database baru.

```powershell
psql -U postgres -h localhost -d postgres -f database/schema.psql
psql -U postgres -h localhost -d carbonshift -f database/seed.sql
```

Wizard bisa dipakai saat menjalankan source. Untuk development, kamu juga bisa menyalin `Form/CarbonShift/database.config.example` ke `Form/CarbonShift/database.config` lalu mengisi kredensial lokal. Pilihan lain adalah mengatur `CARBONSHIFT_DB`. Aplikasi membaca konfigurasi dari environment variable, lalu konfigurasi wizard di `%LocalAppData%\CarbonShift\database.config`, lalu file development di samping EXE. Konfigurasi aktif tetap selama sesi. Ubah melalui Pengaturan database setelah logout. Konfigurasi pribadi tidak disertakan dalam publish. Skema lima tabel resmi tetap dipertahankan. `database/schema.sql` adalah DDL bersama untuk wizard dan script psql.

```powershell
dotnet run --project Form/CarbonShift/CarbonShift.csproj
```

Akun contoh yang tersedia adalah `admin@carbonshift.com` / `12345`. Di Dashboard, ketik nama kota, klik **Cari kota** (atau Enter), lalu pilih hasil dari dropdown pada field yang sama. Lokasi langsung diterapkan dan forecast diambil. Setelah itu buka **Scheduler**. Kontrol lokasi hanya tampil di Dashboard. Rentang waktu menggunakan jam lokal kota terpilih, bukan jam komputer. Kandidat hari ini hanya mencakup jam yang belum lewat. Jam selesai 24 berarti tengah malam berikutnya.

## Membuat akun

Untuk membuat akun sendiri, klik **Belum punya akun? Daftar** pada halaman Masuk. Isi nama, email, password 8–128 karakter, dan konfirmasi password, lalu klik **Buat akun**. Akun tersimpan di PostgreSQL dan langsung masuk Dashboard tanpa verifikasi email. Setelah logout, akun dapat dipakai kembali melalui mode Masuk. Akun baru dimulai dengan riwayat kosong. Setiap akun hanya mengakses jadwalnya sendiri.

## Forecast dan penyimpanan lokal

Data diambil dari [Open-Meteo](https://open-meteo.com/en/docs) menggunakan `HttpClient` dan `System.Text.Json`. Radiasi bertimestamp 12.00 merupakan rata-rata interval 11.00–12.00. Adapter memetakannya ke interval tersebut. Aplikasi meminta empat hari API untuk memperoleh titik penutup terakhir, lalu menampilkan tiga hari kalender.

Lokasi terakhir dan cache JSON disimpan di `%LocalAppData%\CarbonShift`. Cache normal berlaku 60 menit. Jika API gagal setelah satu retry (timeout 10 detik per percobaan), cache maksimal 6 jam dengan cakupan hari yang masih sesuai dapat dipakai dengan peringatan. Interval yang tidak lengkap diabaikan, bukan diisi nol.

**Coba data contoh** adalah mode eksplisit untuk mencoba aplikasi tanpa forecast live. Hasil demo tidak bisa disimpan. Pilih **Ambil ulang forecast** untuk kembali ke data nyata. Pengubahan input atau forecast membatalkan rekomendasi lama.

Evidence lengkap disimpan sebagai teks pada `recommendation.explanation`. Timestamp jadwal adalah waktu lokal kota. Kolom `location` mencantumkan zona waktu. Data lama tetap ditampilkan. Zona waktu/evidence yang belum tersedia tidak ditebak. Dashboard tidak memakai baris tanpa zona waktu untuk menentukan jadwal mendatang.

## Build dan pengujian

```powershell
dotnet build Form/CarbonShift.slnx
dotnet test Form/CarbonShift.slnx
```

Pengujian default mencakup engine, parsing API, cache, CSV, statistik, dan invalidasi input UI menggunakan data deterministik. Aktifkan pengujian DB dan API nyata dengan perintah berikut.

```powershell
$env:CARBONSHIFT_DB = (Get-Content -Raw Form/CarbonShift/database.config).Trim()
$env:CARBONSHIFT_TEST_DB = '1'
$env:CARBONSHIFT_TEST_LIVE = '1'
dotnet test Form/CarbonShift.slnx
```

Uji integrasi membuat akun sementara, menguji simpan/baca tanggal besok-lusa dan pemisahan akun, menjalankan alur UI sampai membuka ulang aplikasi, lalu membersihkan data akun uji. Tidak ada migrasi atau perubahan data pengguna yang sudah ada. Screenshot uji UI ditaruh di `%TEMP%\CarbonShift-preview`.

Fitur yang belum tersedia meliputi recurring schedule, notifikasi, reset password, edit profil/aktivitas, pemilihan alternatif untuk disimpan, IoT, dan ML.

## Paket submisi

Publish profile `Submission` menargetkan Release, Windows x64, self-contained, tanpa trimming atau pemaksaan single-file. [Panduan pengemasan](docs/PENGEMASAN.md) menjelaskan script, prasyarat developer, struktur ZIP, dan batas pengujian rilis. Hasil ZIP/PDF di `artifacts/` dan `output/` tidak di-commit. Source panduan, screenshot, SQL, profile, dan script tersedia di repo.

