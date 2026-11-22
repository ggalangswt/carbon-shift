# CarbonShift

**Aplikasi penjadwalan energi pintar yang merekomendasikan waktu terbaik untuk menggunakan listrik berdasarkan potensi energi terbarukan dan prakiraan cuaca.**

## Anggota Kelompok

**Ketua Kelompok** Nabila Putri barokah - 24/541890/TK/60132

**Anggota 1** Galang Swastika Ramadhan - 24/538251/TK/59692

**Anggota 2** Farand Hafiz - 24/540618/TK/60027

## Aplikasi

CarbonShift membantu memilih waktu aktivitas berdasarkan potensi surya. Pengguna membuka aplikasi, daftar atau login, memilih lokasi, membuat rekomendasi, lalu menyimpan dan membaca riwayatnya. Lihat [Panduan Pengguna](docs/PANDUAN_PENGGUNA.md).

Green Score 0-100 menunjukkan kesesuaian waktu dengan potensi surya, bukan persentase energi terbarukan atau pengurangan emisi. Jadwal tersimpan belum berarti aktivitas sudah dilakukan.

## Arsitektur

WinForms → Supabase Auth, REST/RPC, dan Edge Functions → PostgreSQL Supabase.

- WinForms .NET 10 memuat URL project dan publishable key.
- Domain dan kontrak berada di `src/CarbonShift.Domain` serta `src/CarbonShift.Contracts`.
- SQL migrasi dan RLS berada di `supabase/migrations`. Edge Functions berada di `supabase/functions`.
- Tidak ada server ASP.NET Core atau password PostgreSQL pada aplikasi pengguna.
- Lima tabel aplikasi memakai hubungan UUID Supabase Auth ke ID akun integer. Akun uji lama yang belum terhubung dibersihkan melalui migrasi terpisah tanpa menghapus akun Auth atau riwayatnya.

## Development dan aktivasi

Gunakan Windows dan .NET SDK 10 untuk WinForms. Siapkan konfigurasi publik lokal dan aktifkan project sesuai [Panduan Pengelola](docs/PENGELOLA.md). Migrasi dan Edge Functions sudah diaktifkan di project hosted. Login tidak lagi memakai jalur migrasi password lama. GitHub Release sebelumnya belum otomatis memakai kode lokal terbaru.

```powershell
dotnet build Form/CarbonShift.slnx
dotnet test Form/CarbonShift.slnx
npx deno test supabase/functions/_shared/engine_test.ts
dotnet run --project Form/CarbonShift/CarbonShift.csproj
```

Pengujian default memakai data deterministik dan HTTP palsu. Pengujian migrasi/RLS lokal hanya berjalan saat opt-in dan menolak host remote. Lihat panduan pengelola untuk fixture database sementara dan checklist hosted.

## Dokumentasi

- [Pengelolaan, migrasi, dan deployment](docs/PENGELOLA.md)
- [Akun dan autentikasi](docs/AKUN_DAN_AUTENTIKASI.md)
- [Forecast, Green Score, dan rekomendasi](docs/ALUR_REKOMENDASI.md)
- [Pengemasan](docs/PENGEMASAN.md)

Paket pengguna memuat aplikasi dan panduan pemakaian. SQL, konfigurasi DB, backup, serta instruksi server tetap berada di repo untuk pengelola. Artefak build di `artifacts/` dan `output/` tidak di-commit.
