# Panduan Pengelola CarbonShift

## Arsitektur

WinForms memakai Supabase Auth, PostgREST/RPC, dan Edge Functions. Supabase tetap menyimpan lima tabel aplikasi. Tidak ada server ASP.NET Core, Docker, atau hosting lain yang perlu disiapkan untuk aplikasi ini.

Desktop memuat Project URL dan publishable key. Desktop tidak memuat password PostgreSQL, connection string, secret key, atau service-role key. Koneksi Npgsql lama tidak lagi dipakai oleh aplikasi.

`CarbonShift.Domain` berisi model, engine untuk demo, statistik, dan aturan skor. `CarbonShift.Contracts` berisi bentuk response dan interface client. `SupabaseAppClient` mengakses Auth melalui Edge Function dan riwayat melalui RPC. Prakiraan tampilan memakai Open-Meteo dan cache perangkat. Saat menghitung rekomendasi yang bisa disimpan, Edge Function mengambil forecast terpercaya dan menghitung hasilnya sendiri.

## Status perubahan

Project Supabase sudah dihubungkan dan fungsi sudah diaktifkan. Autentikasi sekarang memakai signup/password Supabase Auth biasa. Dukungan migrasi password lama dihentikan. Migrasi cleanup hanya menghapus akun uji PBKDF2 yang tidak terhubung dan tidak memiliki email yang cocok dengan akun Auth. Akun Auth yang sudah ada beserta profil dan riwayatnya dipertahankan. Release lama di GitHub belum otomatis mengikuti perubahan kode lokal.

## 1. Persiapan

1. Gunakan project Supabase yang sudah berisi akun dan riwayat CarbonShift.
2. Buat backup DB sebelum menjalankan migrasi. Simpan backup di tempat privat, bukan Git atau release.
3. Pasang Node.js untuk menjalankan Supabase CLI melalui `npx`.
4. Dari root repo, jalankan `npx supabase login`. Selesaikan login melalui browser. Jangan kirim access token, password DB, atau service-role key ke chat.
5. Hubungkan project melalui perintah berikut. Ganti project ref sesuai dashboard.

```powershell
npx supabase link --project-ref YOUR_PROJECT_REF
```

CLI dapat meminta password PostgreSQL. Isi pada prompt privat. Password ini tidak dipakai oleh pengguna aplikasi.

## 2. Terapkan migrasi

Periksa SQL pada `supabase/migrations/20261122000100_auth_rpc_rls.sql`. Migrasi mengasumsikan kelima tabel resmi sudah ada. Untuk project kosong, siapkan `database/schema.sql` terlebih dahulu. `schema.psql` khusus server PostgreSQL lokal dan tidak dipakai untuk project Supabase.

```powershell
npx supabase db push --remote --dry-run
npx supabase db push --remote
```

Migrasi pertama menambah kolom `public.user.auth_user_id` sebagai penghubung identitas Auth serta schema internal `carbonshift_private` untuk snapshot rekomendasi, cache server, dan pembatasan percobaan. Migrasi kedua menghapus jalur migrasi password lama serta akun uji yang belum terhubung. Akun Auth beserta ID profil dan riwayatnya tetap dipertahankan. Schema internal tidak boleh ditambahkan ke exposed schemas.

Migrasi menolak email lama yang bertabrakan setelah normalisasi huruf dan spasi. Periksa konflik sebelum melanjutkan, jangan menghapus akun atau mengganti kepemilikan riwayat secara otomatis.

Setelah migrasi, periksa jumlah baris kelima tabel terhadap backup. Migrasi ini mematikan login role `carbonshift_api` jika role tersebut dibuat saat eksperimen ASP.NET. Tidak ada aplikasi baru yang menggunakan role itu.

## 3. Pengaturan Auth dan Data API

1. Pada pengaturan Authentication, aktifkan pendaftaran email/password dan nonaktifkan Confirm email agar pendaftaran langsung menghasilkan sesi.
2. Tetapkan panjang minimum password 8 karakter. Akun baru memakai password Supabase Auth.
3. Aktifkan Data API setelah migrasi RLS dan grant selesai diterapkan. Gunakan hanya schema yang memang diperlukan. Jangan mengekspos `carbonshift_private` atau tabel internal Auth.
4. Pertahankan RLS aktif. Jangan memberikan grant tulis tabel langsung kepada `anon` atau `authenticated`.

URL konfigurasi Auth bisa diisi dengan alamat project yang sesuai. Aplikasi desktop tidak memakai alur redirect atau verifikasi email. Endpoint Auth tetap membutuhkan HTTPS.

## 4. Deploy Edge Functions

```powershell
npx supabase functions deploy carbonshift-auth --use-api
npx supabase functions deploy carbonshift --use-api
```

Konfigurasi `verify_jwt=false` berada pada `supabase/config.toml`. Fungsi login memang harus dapat diakses sebelum autentikasi. Fungsi rekomendasi memverifikasi token memakai `auth.getUser`, bukan sekadar membaca isi JWT.

Supabase menyediakan `SUPABASE_URL`, `SUPABASE_ANON_KEY`, dan `SUPABASE_SERVICE_ROLE_KEY` pada lingkungan Edge Functions. Service-role key hanya digunakan di sana. Jangan menyalinnya ke aplikasi desktop, log, build argument, PDF, atau ZIP.

Endpoint aplikasi berada pada Project URL yang sama.

| Endpoint | Tugas |
| --- | --- |
| functions/v1/carbonshift-auth | Register dan login Supabase Auth |
| functions/v1/carbonshift/recommendations/preview | Hitung dan buat snapshot terpercaya |
| rest/v1/rpc/carbonshift_profile | Profil akun aktif |
| rest/v1/rpc/carbonshift_history | Riwayat akun aktif |
| rest/v1/rpc/carbonshift_save_preview | Simpan snapshot secara atomik |
| rest/v1/rpc/carbonshift_delete_history | Hapus riwayat akun aktif |

## 5. Signup dan login native

Fungsi carbonshift-auth hanya memakai signUp dan signInWithPassword Supabase Auth. Tidak ada pembacaan hash PBKDF2, pembuatan identitas admin saat login, generateLink, atau verifyOtp pada jalur login.

Trigger membuat profil aplikasi dengan UUID Auth yang sama. Metadata nama yang diisi Auth setelah INSERT disinkronkan melalui trigger UPDATE yang hanya menyentuh profil dengan UUID pemilik tersebut. Metadata pengguna tidak pernah dipakai untuk memindahkan kepemilikan akun atau riwayat.

Migrasi `20261122000200_remove_legacy_test_accounts.sql` menyeleksi akun uji dengan auth_user_id kosong, hash PBKDF2, dan tanpa pasangan email di auth.users. Hanya data milik akun terpilih yang dibersihkan. Forecast yang masih dipakai akun lain dipertahankan. Migrasi tidak menghapus baris auth.users atau mereset identity sequence.

Sebelum cleanup hosted, backup schema public, auth, dan carbonshift_private dibuat secara privat. Sidik akun Auth, profil, serta riwayat terlindungi dibandingkan sebelum dan sesudah cleanup. Jangan mengubah migrasi pertama yang sudah diterapkan, gunakan migrasi lanjutan untuk perubahan schema.

## 6. RLS dan transaksi

Identitas diambil dari `auth.uid()`. Fungsi `current_user_id` memetakan UUID Auth ke ID integer aplikasi. Riwayat, profil, penghapusan, dan penyimpanan tidak menerima `user_id` dari desktop.

RLS membatasi pembacaan tabel menurut pemilik. Password hash tidak memiliki grant baca untuk pengguna. Penulisan dilakukan melalui RPC security definer dengan `search_path` kosong, referensi schema eksplisit, pemeriksaan identitas, dan pembatasan EXECUTE.

Edge Function menghitung rekomendasi dari data cuaca yang diambil server, bukan skor kiriman client. Snapshot disimpan pada schema internal dan terikat pada pemilik, berlaku maksimal 15 menit, serta diperiksa ulang terhadap waktu mulai dan umur forecast. Simpan hanya menerima ID snapshot. Pengiriman berulang menghasilkan ID rekomendasi yang sama. Advisory lock dan row lock mencegah penulisan ganda serta konflik penghapusan. Transaksi menyimpan aktivitas, ringkasan cuaca, slot terbaik, dan rekomendasi sekaligus.

Cache server berlaku 60 menit. Saat API gagal, cache maksimal 6 jam dengan cakupan tanggal yang sesuai boleh dipakai. Semua interval memakai radiasi bertimestamp jam berikutnya. Skor berbobot tetap 70% surya, 20% awan, dan 10% siang dengan referensi 900 W/m².

## 7. Konfigurasi publik desktop

Salin `Form/CarbonShift/supabase.public.example.json` ke `Form/CarbonShift/supabase.public.json`. Isi Project URL HTTPS dan publishable key dari dashboard. File lokal diabaikan Git dan tidak perlu dikirim lewat chat.

```json
{
  "url": "https://YOUR_PROJECT_REF.supabase.co/",
  "publishableKey": "sb_publishable_YOUR_PROJECT_KEY"
}
```

Publishable key memang dapat disertakan di desktop. Secret key dan service-role key ditolak oleh validasi client dan script packaging. URL serta publishable key dimasukkan pengelola saat membangun rilis. Pengguna akhir tidak mengisi konfigurasi apa pun.

```powershell
dotnet run --project Form/CarbonShift/CarbonShift.csproj
```

Token sesi hanya disimpan dalam memori. Aplikasi tidak menyimpan refresh token atau password. Logout membersihkan token, data forecast sesi, dan halaman akun. Setelah sesi berakhir atau aplikasi ditutup, pengguna login kembali.

## 8. Pengujian

```powershell
dotnet test Form/CarbonShift.slnx
npx deno check supabase/functions/carbonshift-auth/index.ts supabase/functions/carbonshift/index.ts
npx deno test supabase/functions/_shared/engine_test.ts
```

Pengujian DB lokal bersifat opt-in dan menolak host remote. Gunakan file konfigurasi administrator PostgreSQL lokal khusus pengujian. Fixture membuat database sementara dengan stub identitas Auth untuk memeriksa migrasi SQL dan RLS. Stub ini tidak menggantikan pengujian hosted Auth.

```powershell
$env:CARBONSHIFT_TEST_LOCAL_DB = '1'
$env:CARBONSHIFT_TEST_LOCAL_CONFIG_FILE = 'C:/path/ke/local-test.config'
dotnet test tests/CarbonShift.Database.Tests/CarbonShift.Database.Tests.csproj
```

Setelah aktivasi hosted, uji signup baru, login ulang, token tidak valid, dua akun dengan riwayat terpisah, penolakan pembacaan hash, simpan dua kali, kedaluwarsa snapshot, dan hapus riwayat satu akun. Bandingkan lagi data asli dan bersihkan akun uji. Uji signup dan signInWithPassword hosted wajib dilakukan sebelum distribusi rilis.

## 9. Packaging dan cutover

Jalankan `scripts/package.ps1` setelah fungsi dan RLS aktif serta konfigurasi publik valid. Script memasukkan URL dan publishable key melalui metadata build. Paket pengguna tidak berisi SQL, template DB, sertifikat PostgreSQL, Npgsql, atau dokumentasi server.

Setelah EXE baru diuji, rotasi password DB yang sebelumnya dibagikan kepada desktop. Simpan backup konfigurasi lama secara privat. Release lama tidak otomatis berubah ketika kode lokal diperbarui. Jangan membagikan paket baru sebagai paket siap pakai sebelum aktivasi project dan pengujian hosted selesai.

Rujukan tersedia di [API keys Supabase](https://supabase.com/docs/guides/getting-started/api-keys), [RLS](https://supabase.com/docs/guides/database/postgres/row-level-security), dan [deployment Edge Functions](https://supabase.com/docs/guides/functions/deploy).
