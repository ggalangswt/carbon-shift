# Akun dan Autentikasi CarbonShift

Desktop mengirim register dan login ke Edge Function `carbonshift-auth`. Fungsi menghasilkan sesi Supabase Auth asli. Aplikasi menyimpan access token dalam memori dan mengirimkannya saat memanggil REST/RPC. Password dan refresh token tidak disimpan pada perangkat.

Register dan login memakai signup/password Supabase Auth. Tidak ada jalur migrasi PBKDF2 pada login. Akun uji lama yang tidak terhubung sudah dibersihkan melalui migrasi terpisah. Akun yang sudah dibuat melalui Supabase Auth beserta riwayatnya tetap dipertahankan.

`public.user.auth_user_id` memetakan UUID Auth ke ID integer aplikasi. RLS dan RPC mengambil identitas dari `auth.uid()`. Desktop tidak mengirim `user_id` untuk mengakses atau mengubah riwayat. Kolom password hash tidak dapat dibaca pengguna melalui Data API.

Logout menghapus token dan sesi tampilan. Token yang sudah diterbitkan mengikuti masa berlaku Supabase Auth, sehingga logout lokal tidak dianggap sebagai pencabutan seluruh token di server. Ketika sesi berakhir, pengguna kembali ke halaman Masuk.

Validasi daftar mencakup nama, format email, panjang password, dan konfirmasi. Email dinormalisasi. Password mempertahankan whitespace yang disengaja. API membatasi percobaan dan tidak mengungkapkan hash password dalam response.

Detail aktivasi dan pengujian tersedia pada [Panduan Pengelola](PENGELOLA.md). Belum ada reset password, edit profil, atau penghapusan akun melalui aplikasi.
