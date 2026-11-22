# Membuat Paket Submisi CarbonShift

## Prasyarat developer

- Windows x64, .NET SDK 10, dan Git.
- Python dengan `reportlab` dan `Pillow` untuk membangun PDF. Pasang dependensi dengan `python -m pip install reportlab Pillow`.
- Screenshot panduan berada pada `docs/images/`. Perbarui jika UI berubah.
- PostgreSQL hanya diperlukan jika mengaktifkan pengujian integrasi. Akun penguji integrasi setup membutuhkan CREATE DATABASE dan CREATE ROLE untuk fixture sementara. Pengguna aplikasi tidak perlu hak CREATE ROLE.

## Satu perintah

Jalankan perintah berikut di PowerShell dari root repo.

```powershell
./scripts/package.ps1 -Version 1.0.0 -PythonExecutable python
```

Jika `python` hanya merupakan alias Microsoft Store, isi `-PythonExecutable` dengan path interpreter Python yang nyata. Kebijakan eksekusi PowerShell mengikuti pengaturan komputer. Script tidak mengubah pengaturan keamanan Windows.

Script menghentikan proses jika pengujian, publish, validasi runtime, atau pembuatan PDF gagal. Script menjalankan pengujian default. Pengujian DB/API mengikuti flag opt-in pada README. Jika sudah memiliki `CARBONSHIFT_DB`, gunakan akun/database pengujian yang sesuai sebelum mengaktifkan flag DB.

## Tahapan script

1. Jalankan `dotnet test` dalam konfigurasi Release.
2. Publish proyek aplikasi dengan profile `Submission` yang memakai `win-x64`, self-contained, `PublishSingleFile=false`, `PublishTrimmed=false`.
3. Pastikan executable, runtime Windows Forms, `hostfxr.dll`, `coreclr.dll`, dan Npgsql tersedia. Runtime config harus bertipe self-contained.
4. Pastikan sertifikat CA publik `prod-ca-2021.crt` tersedia. Tolak konfigurasi pribadi, dump database, private key, PDB, dan assembly test. `database.config` dan `supabase.config` tidak ikut publish.
5. Salin panduan pengguna, panduan Supabase, konfigurasi contoh tanpa kredensial asli, dan gambar. Letakkan SQL lokal di `Database/Lokal-opsional`. Bangun PDF dari sumber panduan yang sama.
6. Tambahkan instruksi mulai dan informasi versi/commit/status workspace. Workspace modified dicatat jika perubahan belum di-commit. File sumber yang sedang ada tetap dibangun.
7. Buat ZIP dan checksum SHA-256 di `artifacts/`. Setiap build memakai staging baru agar file build sebelumnya tidak ikut.

Berikut susunan output yang dihasilkan.

```text
artifacts/
  CarbonShift-Submission-win-x64-1.0.0.zip
  CarbonShift-Submission-win-x64-1.0.0.zip.sha256
  build-<id>/CarbonShift-Submission-win-x64-1.0.0/
    MULAI-DI-SINI.txt
    Panduan-Pengguna.pdf
    INFO-VERSI.txt
    Aplikasi/
      CarbonShift.exe
      prod-ca-2021.crt
      seluruh runtime/dependensi
    Database/supabase.config.example
    Database/Lokal-opsional/schema.psql, schema.sql, seed.sql, database.config.example
    Dokumentasi/Panduan-Pengguna.md
    Dokumentasi/Supabase.md
    Dokumentasi/images/
```

PostgreSQL, konfigurasi pribadi, data pengguna, laporan Modul 10, dan seluruh source repo tidak dibundel. Pengumpul dapat menambahkan laporan Modul 10 sendiri sebelum mengunggah. Bila ZIP diedit manual, hitung ulang checksum.

Paket ini memakai Supabase yang sudah disiapkan pengelola. Pengguna memerlukan internet dan detail koneksi Session pooler yang dibagikan secara privat, bukan instalasi PostgreSQL lokal. Sertifikat CA publik boleh disertakan. Password DB, backup migrasi, dan konfigurasi aktif tidak boleh dimasukkan ke ZIP atau PDF.

Script menolak flag integrasi aktif saat packaging agar tidak menjalankan pengujian setup database pada Supabase. Dua pengujian akun dan penyimpanan remote telah diverifikasi terpisah. Jangan menjalankan seluruh suite setup DB di project Supabase.

## PDF saja

```powershell
python scripts/build_guide.py --source docs/PANDUAN_PENGGUNA.md --output output/pdf/Panduan-Pengguna.pdf --version 1.0.0
pdftoppm -png output/pdf/Panduan-Pengguna.pdf tmp/pdfs/panduan
```

Periksa seluruh halaman hasil render. Screenshot mode demo dan data uji diberi keterangan sebagai ilustrasi, tidak dianggap forecast live.

## Checklist sebelum unggah

- Ekstrak ZIP ke folder baru, buka folder `Aplikasi`, lalu jalankan `CarbonShift.exe`.
- Pastikan dependensi dan sertifikat CA tetap berada di samping EXE. Periksa ZIP tidak membawa konfigurasi pribadi, password, atau dump migrasi.
- Uji koneksi Session pooler dan penyimpanan pengaturan pada profil Windows. Pengguna baru perlu meminta detail koneksi secara privat.
- Uji setup database lokal hanya pada DB sementara lokal. Jangan menjalankannya pada Supabase.
- Daftar/login, ambil forecast, simpan, tutup, buka lagi, dan baca riwayat.
- Sertakan laporan testing Modul 10 milik kelompok bila diminta dalam submisi.
- Coba di Windows bersih tanpa .NET/Visual Studio. Pemeriksaan runtime dan pengujian pada laptop developer tidak menggantikan uji lingkungan bersih tersebut.
