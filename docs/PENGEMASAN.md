# Pengemasan CarbonShift

## Prasyarat pengelola

- Windows x64, .NET SDK 10, dan Git.
- Python dengan ReportLab serta Pillow untuk membuat PDF.
- Project Supabase yang sudah melewati aktivasi dan pengujian pada [Panduan Pengelola](PENGELOLA.md).
- Project URL dan publishable key yang benar. Secret atau service-role key tidak boleh dipakai untuk build desktop.

## Membuat paket

Isi `Form/CarbonShift/supabase.public.json` dari template publik, lalu jalankan dari root repo.

```powershell
./scripts/package.ps1 -Version 1.0.0 -PythonExecutable python
```

Alternatifnya, isi parameter publik saat build.

```powershell
./scripts/package.ps1 -SupabaseUrl 'https://YOUR_PROJECT_REF.supabase.co/' -SupabasePublishableKey 'sb_publishable_YOUR_KEY' -PythonExecutable python
```

Ganti nilai contoh. Script menolak URL kosong, placeholder, dan key rahasia. Pengguna akhir tidak menjalankan perintah ini dan tidak mengisi konfigurasi project.

Script menjalankan pengujian default, publish self-contained Windows x64, pemeriksaan berkas, pembuatan PDF, ZIP, dan checksum. Flag pengujian integrasi harus dimatikan saat packaging. URL serta publishable key dimasukkan dalam metadata assembly. Tidak ada password DB atau private key dalam argumen build.

Susunan paket pengguna berikut.

```text
CarbonShift-Submission-win-x64-1.0.0/
  MULAI-DI-SINI.txt
  Panduan-Pengguna.pdf
  INFO-VERSI.txt
  Aplikasi/
    CarbonShift.exe
    CarbonShift.Domain.dll
    CarbonShift.Contracts.dll
    runtime dan dependensi aplikasi
  Dokumentasi/
    Panduan-Pengguna.md
    images/
```

SQL, Npgsql, sertifikat PostgreSQL, template konfigurasi DB, dan panduan server tidak masuk paket pengguna. Materi pengelola tetap berada di repo. Backup dan konfigurasi privat berada di luar Git. Laporan testing Modul 10 kelompok ditambahkan terpisah bila dibutuhkan submisi.

## Pemeriksaan sebelum distribusi

1. Jalankan type check dan pengujian Edge Functions sesuai panduan pengelola.
2. Pastikan migrasi hosted dan dua Edge Functions sudah aktif.
3. Uji akun lama dan akun baru pada project hosted, termasuk pemisahan riwayat.
4. Ekstrak ZIP ke folder baru. Halaman awal harus langsung menampilkan Masuk dan Daftar tanpa pengaturan database.
5. Uji simpan, buka ulang aplikasi, baca riwayat, export CSV, serta logout.
6. Pastikan paket memakai URL project yang benar dan tidak membawa kredensial DB atau service-role key.
7. Render dan periksa seluruh halaman PDF. Panduan pengguna hanya memuat cara memakai aplikasi.
8. Periksa checksum dan uji pada komputer Windows bersih sebelum submisi.

## PDF saja

```powershell
python scripts/build_guide.py --source docs/PANDUAN_PENGGUNA.md --output output/pdf/Panduan-Pengguna.pdf --version 1.0.0
pdftoppm -png output/pdf/Panduan-Pengguna.pdf tmp/pdfs/panduan
```

Jangan menandai paket sebagai siap pakai sebelum konfigurasi publik, aktivasi project, dan pengujian hosted selesai. Build lokal yang belum dikonfigurasi hanya dipakai untuk pengembangan dan pemeriksaan tampilan.
