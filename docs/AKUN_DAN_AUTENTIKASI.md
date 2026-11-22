# Akun dan Autentikasi CarbonShift

## Alur pengguna

1. Buka aplikasi, lalu pilih **Belum punya akun? Daftar**.
2. Isi nama, email, password, dan konfirmasi password.
3. Klik **Buat akun**. Jika valid, akun disimpan ke PostgreSQL dan langsung masuk Dashboard.
4. Akun baru memiliki riwayat kosong. Pilih lokasi dan buat jadwal seperti biasa.
5. Klik Logout untuk kembali ke mode Masuk. Email tetap terisi; kedua field password dikosongkan.
6. Masuk kembali menggunakan email dan password yang didaftarkan.

Mode Masuk dan Daftar memakai satu jendela. Perpindahan mode mengosongkan password dan pesan sebelumnya. Password tersembunyi secara default; opsi Tampilkan password berlaku untuk password dan konfirmasinya. Saat permintaan diproses, field, tombol kirim, dan tautan pergantian mode dinonaktifkan untuk mencegah pengiriman ganda.

## Validasi

| Field | Aturan |
| --- | --- |
| Nama | Wajib setelah trim, maksimal 100 karakter. |
| Email | Wajib, format email sederhana, maksimal 100 karakter. Spasi di tepi dibuang. Email baru disimpan dalam huruf kecil. |
| Password baru | 8–128 karakter, tidak boleh seluruhnya whitespace. Tidak diwajibkan memiliki simbol atau jenis karakter tertentu. |
| Konfirmasi | Harus sama persis dengan password. |

Password tidak di-trim: spasi yang sengaja dimasukkan merupakan bagian dari password. Validasi panjang password baru tidak diterapkan ulang saat login, sehingga akun lama dengan password lebih pendek tetap bisa digunakan.

Email dipakai sebagai identitas login tanpa verifikasi kepemilikan. Pencarian akun tidak membedakan kapital dan mengabaikan spasi di tepi, termasuk pada data akun lama. Validasi format tidak memeriksa apakah alamat email benar-benar ada atau dapat menerima pesan.

## Alur kode dan database

```text
FormLogin (mode Daftar)
    → AuthService.RegisterAsync
    → validasi dan normalisasi input
    → UserRepository.FindByEmailAsync: periksa email terdaftar
    → PasswordHasher.Hash di background
    → UserRepository.CreateAsync: INSERT berparameter
    → UserAccount hasil RETURNING, termasuk user_id
    → FormDashboard dengan AppSession baru
```

`RegisterAsync(name, email, password, confirmation, cancellationToken)` mengembalikan akun hanya setelah INSERT berhasil. `CreateAsync(name, email, passwordHash, cancellationToken)` menerima hash, bukan password plaintext.

Tabel resmi `"user"` tetap digunakan, tanpa migrasi: `user_id`, `name`, `email`, `password_hash`, dan `created_at`. Query tidak menyusun SQL dari input pengguna. Hash memakai PBKDF2-SHA256, 100.000 iterasi, salt acak 16 byte, dan hasil hash 32 byte. Format tersimpan sama dengan akun demo yang sudah ada.

Jika email terdaftar, form menampilkan **Email sudah terdaftar. Silakan masuk.** Dua registrasi serentak dinormalisasi ke email yang sama dan dilindungi constraint email unik PostgreSQL. Pelanggaran constraint email diterjemahkan ke pesan yang sama. Jika database lama sudah memiliki dua baris yang hanya berbeda kapital/spasi email, pencarian tidak memilih salah satu secara acak; data tersebut perlu diperbaiki oleh pengelola database.

Validasi ditampilkan di form dengan fokus pada field terkait. Kegagalan koneksi menggunakan pesan umum agar SQL, connection string, dan detail password tidak ditampilkan. Operasi database berjalan async dan hashing dilakukan di background. Menutup form membatalkan operasi yang masih dapat dibatalkan; bila akun telanjur tersimpan sebelum koneksi terputus, pengguna dapat mencoba Masuk.

## Pemisahan data akun

Dashboard menerima akun dari hasil autentikasi, lalu membuat `AppSession` baru. Halaman sesi sebelumnya tidak dipakai ulang setelah logout.

| Data/aksi | Cara pemisahan |
| --- | --- |
| Menyimpan jadwal | `activity.user_id` diisi dari akun sesi aktif. |
| Riwayat dan statistik | Query hanya mengambil aktivitas dengan `user_id` akun aktif. |
| Export CSV | Menggunakan baris riwayat akun yang sedang ditampilkan. |
| Hapus semua riwayat | Menghapus aktivitas dan relasi milik akun aktif; forecast yang masih direferensikan tidak dihapus. |
| Lokasi terakhir | Berkas lokal memakai key `location_user_<user_id>`. |

Cache cuaca tetap berdasarkan lokasi dan boleh dipakai bersama karena tidak berisi riwayat pribadi. Akses data aplikasi dipisahkan oleh sesi dan filter query; ini bukan kebijakan PostgreSQL Row-Level Security. Skema dan kredensial koneksi database tetap mengikuti instalasi desktop yang ada.

## Pengujian

- Pengujian default memeriksa field kosong, format dan batas panjang, konfirmasi berbeda, salt hash, serta pemeliharaan whitespace password.
- Pengujian DB menguji registrasi, login ulang, password salah, email kapital/duplikat, kompatibilitas password lama, dan registrasi bersamaan.
- Dua akun uji menyimpan aktivitas sendiri. Riwayat, CSV, statistik, dan penghapusan satu akun diperiksa agar tidak memengaruhi akun lainnya.
- Pengujian UI menjalankan mode Daftar, validasi inline, langsung masuk Dashboard, logout, dan login kembali. Password harus kosong setelah logout.
- Akun integrasi menggunakan alamat acak sementara dan dibersihkan setelah pengujian. Lihat [README](../README.md) untuk mengaktifkan uji DB dengan `CARBONSHIFT_TEST_DB=1`.

## Batas fitur

Belum ada verifikasi email, OTP, reset password, edit profil, penghapusan akun, atau perbedaan hak akses admin. Akun bernama Admin pada seed menggunakan alur sesi dan pemisahan riwayat yang sama dengan akun lainnya.
