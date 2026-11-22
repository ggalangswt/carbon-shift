# CarbonShift UI

## Setting

Pengguna menyusun jadwal di laptop pada ruang kelas atau rumah dengan pencahayaan biasa. Pertahankan permukaan terang dan keterbacaan kontrol standar Windows.

## Typography and color

- Segoe UI 10 pt untuk teks. 15 pt bold untuk judul halaman.
- Hijau utama RGB(27, 94, 32), mengikuti identitas aplikasi yang sudah ada.
- Permukaan RGB(246, 249, 246). Teks gelap. Teks putih pada navigasi hijau.
- Hijau muda/kuning muda untuk kategori skor, dengan label teks. Biru untuk interval rekomendasi pada grafik.

## Layout and behavior

- Sidebar tetap untuk Dashboard, Forecast, Scheduler, Riwayat, Logout.
- Pengaturan lokasi dan status forecast hanya tampil di Dashboard. Satu field pencarian kota menampilkan hasil sebagai dropdown. Memilih hasil langsung menerapkan lokasi. Aksi pencarian berwarna hijau.
- Sidebar menyediakan ruang untuk judul satu baris, navigasi selebar kolom, dan Logout merah yang tetap berada di bawah.
- Scheduler menggunakan input yang dapat digulir di kiri, grafik, ranking, dan penjelasan di kanan.
- Gunakan Dock, TableLayoutPanel, dan FlowLayoutPanel untuk menghindari koordinat tetap pada halaman utama.
- Kontrol data berstatus loading, kosong, error, atau siap. Tombol simpan mengikuti snapshot hasil yang valid.
- Utamakan hasil waktu terbaik. Evidence tetap tersedia melalui teks yang dapat digulir dan disalin.
