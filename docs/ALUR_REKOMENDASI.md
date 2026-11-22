# Forecast dan Rekomendasi CarbonShift

## Alur aktif setelah aktivasi Auth/RPC

WinForms mengambil forecast tampilan dari Open-Meteo dan menyimpan cache perangkat. Saat Cari rekomendasi, Edge Function mengambil forecast terpercaya sendiri, menghitung kandidat, dan menyimpan snapshot server. Grafik hasil, komponen skor, dan penjelasan yang dikembalikan server menjadi hasil yang dilihat pengguna. Forecast pada halaman lain bisa memiliki waktu pengambilan berbeda.

Tombol Simpan hanya mengirim ID snapshot ke RPC. RPC memeriksa pemilik dari auth.uid(), masa berlaku, serta waktu rekomendasi, lalu menyimpan slot terbaik dalam satu transaksi. Skor, penjelasan, dan user_id dari desktop tidak dipercaya. Hasil demo dihitung pada desktop dan tidak bisa disimpan.

Engine TypeScript server mempertahankan rumus serta aturan waktu engine C# pada domain. Pengujian memeriksa bobot, seri, data hilang, indikator siang, pemetaan radiasi, dan WIB/WITA/WIT. Rincian deployment dan akses data ada di [Panduan Pengelola](PENGELOLA.md).

## Catatan rancangan sebelumnya

Bagian berikut mendokumentasikan perhitungan yang menjadi dasar engine. Referensi repository DB dan nama file pada rancangan sebelumnya merupakan alur historis koneksi desktop, bukan jalur akses database aplikasi saat ini.

# Alur Forecast, Green Score, dan Rekomendasi CarbonShift

Dokumen ini menjelaskan perilaku implementasi saat ini, dari pemilihan kota sampai hasil rekomendasi disimpan. Contoh angka di bawah adalah ilustrasi, bukan hasil forecast live.

## 1. Tujuan perhitungan

CarbonShift membantu pengguna memilih waktu untuk aktivitas listrik yang fleksibel berdasarkan prakiraan cuaca dan potensi energi surya. Pengguna menentukan aktivitas, tanggal, durasi, serta batas waktu yang diperbolehkan. Aplikasi membandingkan slot yang memenuhi batas tersebut dan merekomendasikan slot dengan skor tertinggi.

API menyediakan data cuaca. Perhitungan skor, pembentukan kandidat, pengurutan, dan penjelasan dilakukan sendiri oleh aplikasi menggunakan aturan yang deterministik. Tidak ada API AI, machine learning, atau LLM yang menentukan rekomendasi.

Green Score merupakan **skor kesesuaian waktu dengan potensi surya**. Skor ini bukan persentase listrik terbarukan, hasil pengukuran produksi panel surya, atau besarnya pengurangan emisi PLN.

## 2. API yang dipakai

| API | Endpoint | Fungsi |
| --- | --- | --- |
| Open-Meteo Geocoding | `https://geocoding-api.open-meteo.com/v1/search` | Mencari kota dan memperoleh koordinat, provinsi, serta zona waktu. |
| Open-Meteo Weather Forecast | `https://api.open-meteo.com/v1/forecast` | Mengambil prakiraan per jam berdasarkan koordinat dan zona waktu lokasi. |

Pencarian lokasi dibatasi ke Indonesia melalui parameter `countryCode=ID`. Hasil dipilih dari field pencarian di Dashboard. Pemilihan hasil langsung menerapkan lokasi dan mengambil forecast. Lokasi terakhir disimpan per akun di perangkat.

Koneksi PostgreSQL menggunakan Npgsql, terpisah dari kedua API HTTP tersebut.

## 3. Alur data

```text
Pengguna mencari dan memilih kota
    ↓
Geocoding mengubah nama kota menjadi koordinat dan zona waktu
    ↓
WeatherService mengambil forecast dari API atau cache yang sesuai
    ↓
Parse dan validasi data → interval forecast per jam
    ↓
ForecastSnapshot menyimpan lokasi, waktu pengambilan, sumber, dan data
    ↓
ScheduleRequest memuat aktivitas, tanggal, durasi, rentang, dan estimasi kWh
    ↓
RecommendationEngine membentuk kandidat yang valid
    ↓
GreenScoreCalculator menghitung skor setiap kandidat
    ↓
Urutkan skor tertinggi, lalu waktu paling awal
    ↓
RecommendationResult memuat rekomendasi terbaik, alternatif, dan penjelasan
    ↓
Tampilkan hasil → pengguna menyimpan → PostgreSQL
```

## 4. Data forecast yang diambil

| Parameter API | Isi | Pemakaian dalam aplikasi |
| --- | --- | --- |
| `time` | Timestamp per jam | Menentukan interval, tanggal, dan kelengkapan data. |
| `temperature_2m` | Suhu udara dalam °C | Informasi cuaca dan ringkasan forecast, belum masuk rumus skor. |
| `cloud_cover` | Tutupan awan dalam % | Komponen langit cerah pada metode berbobot. |
| `shortwave_radiation` | Radiasi matahari dalam W/m² | Komponen potensi surya. |
| `is_day` | `1` untuk siang, `0` untuk malam | Proporsi interval siang dalam kandidat. |

Tanggal dan jam mengikuti lokasi terpilih, yaitu `Asia/Jakarta`, `Asia/Makassar`, atau `Asia/Jayapura`. Aplikasi menggunakan timestamp lengkap dengan offset zona waktu, bukan hanya angka jam yang dapat tertukar antarhari.

### Pemetaan interval radiasi

`shortwave_radiation` merupakan rata-rata radiasi pada jam sebelumnya. Data radiasi bertimestamp **12.00** dipakai untuk interval **11.00–12.00**.

Untuk membentuk interval `[t, t+1)`, aplikasi mengikuti aturan berikut:

- Suhu, awan, dan indikator siang diambil dari timestamp `t`.
- Radiasi diambil dari timestamp `t+1`.
- Kedua timestamp harus berjarak tepat satu jam.

Aplikasi meminta `forecast_days=4` agar titik pukul 00.00 setelah hari ketiga tersedia sebagai penutup interval terakhir. Hasil yang dipakai dan ditampilkan tetap untuk **hari ini, besok, dan lusa**.

### Validasi dan cache

Array respons harus memiliki panjang yang sesuai. Nilai kosong atau tidak valid membuat interval terkait diabaikan, bukan diisi nol. Timestamp duplikat ditolak oleh parser. Engine juga mengabaikan timestamp yang tidak unik saat menerima data kandidat.

| Sumber | Perilaku |
| --- | --- |
| Live | Data yang baru diambil dari Open-Meteo. |
| Cache | Data lokasi yang sama, umur maksimal 60 menit, dengan cakupan hari yang masih sesuai. |
| Cache cadangan | Jika API gagal, data maksimal 6 jam dapat dipakai dengan peringatan dan pemeriksaan cakupan. |
| Demo | Data buatan yang diaktifkan secara eksplisit melalui tombol Coba data contoh. Tidak boleh disimpan sebagai riwayat. |

Permintaan HTTP memiliki timeout 10 detik per percobaan. Kegagalan sementara, seperti timeout atau respons server tertentu, dicoba ulang satu kali. Kesalahan permanen seperti HTTP 400 tidak dicoba ulang. Jika API gagal dan cache tidak sesuai, aplikasi menampilkan kegagalan pengambilan forecast.

## 5. Input aktivitas dan pembentukan kandidat

Berikut contoh input pengguna.

```text
Aktivitas           = Charge laptop
Tanggal pelaksanaan = Besok
Durasi              = 2 jam
Boleh mulai         = 08.00
Harus selesai       = 12.00
Estimasi energi     = 0,12 kWh untuk seluruh aktivitas
```

Engine menggeser waktu mulai per satu jam selama aktivitas masih dapat selesai sebelum batas akhir. Contohnya sebagai berikut.

| Kandidat | Interval forecast yang diperlukan |
| --- | --- |
| 08.00–10.00 | 08.00–09.00 dan 09.00–10.00 |
| 09.00–11.00 | 09.00–10.00 dan 10.00–11.00 |
| 10.00–12.00 | 10.00–11.00 dan 11.00–12.00 |

Aturan kandidat saat ini:

- Tanggal berada pada hari ini sampai lusa menurut zona waktu lokasi.
- Durasi berupa jam bulat, antara 1 dan 12 jam.
- Rentang aktivitas berada dalam satu hari. Jam selesai 24 berarti tengah malam berikutnya.
- Seluruh interval yang diperlukan tersedia, berurutan, dan valid.
- Waktu mulai belum lewat. Pada pukul 10.15, kandidat paling awal adalah 11.00. Jika tepat pukul 10.00 tanpa pecahan detik, kandidat 10.00 masih dapat dipakai.
- Kandidat tidak dibuat jika durasinya melebihi rentang yang diperbolehkan.

Jika tidak ada kandidat yang memenuhi syarat, aplikasi memberikan pesan bahwa tidak ada slot lengkap yang masih akan datang pada rentang tersebut.

## 6. Rumus Green Score

Perhitungan internal menggunakan skala **0–1**. UI, CSV, dan nilai skor di database menggunakan skala **0–100**.

### Metode berbobot (default)

Untuk setiap kandidat, aplikasi menghitung rata-rata radiasi dan awan dari seluruh interval di dalamnya, serta proporsi interval siang.

```text
SolarScore    = Clamp(rata-rata radiasi / 900, 0, 1)
CloudScore    = Clamp(1 − rata-rata awan / 100, 0, 1)
DaylightScore = jumlah interval dengan is_day = 1 / jumlah seluruh interval

SkorInternal = Clamp(
    0,70 × SolarScore
  + 0,20 × CloudScore
  + 0,10 × DaylightScore,
  0, 1
)

GreenScoreTampilan = SkorInternal × 100
```

`Clamp(x, 0, 1)` membatasi nilai agar tidak kurang dari 0 atau lebih dari 1. Normalisasi surya dilakukan **setelah merata-ratakan radiasi satu kandidat**.

Metode ini memakai bobot default berikut.

| Komponen | Bobot | Arti |
| --- | ---: | --- |
| Potensi surya | 70% | Radiasi yang lebih tinggi memberi skor lebih besar sampai batas normalisasi. |
| Langit cerah | 20% | Tutupan awan yang lebih rendah memberi skor lebih besar. |
| Siang | 10% | Kandidat dengan proporsi interval siang lebih banyak mendapat skor lebih besar. |

Referensi **900 W/m²** dan bobot **70/20/10** merupakan pilihan aturan awal dalam kode. Nilai tersebut belum merupakan hasil kalibrasi empiris atau standar universal. Constructor kalkulator mendukung bobot berbeda dan menormalisasi totalnya. UI saat ini memakai bobot default.

### Metode potensi surya (alternatif)

```text
SkorInternal = Clamp(rata-rata radiasi / 900, 0, 1)
GreenScoreTampilan = SkorInternal × 100
```

Metode ini tidak memasukkan komponen awan atau siang ke skor akhir.

## 7. Contoh hitungan lengkap

Gunakan input aktivitas dua jam pada rentang 08.00–12.00 dan data ilustrasi berikut. Semua interval berada pada tanggal yang sama dan masih akan datang.

| Interval | Radiasi | Awan | Siang |
| --- | ---: | ---: | --- |
| 08.00–09.00 | 400 W/m² | 40% | Ya |
| 09.00–10.00 | 600 W/m² | 30% | Ya |
| 10.00–11.00 | 800 W/m² | 10% | Ya |
| 11.00–12.00 | 850 W/m² | 10% | Ya |

Berikut perhitungan untuk kandidat **10.00–12.00**.

```text
Rata-rata radiasi = (800 + 850) / 2 = 825 W/m²
Rata-rata awan    = (10 + 10) / 2   = 10%
Proporsi siang   = 2 / 2           = 1

SolarScore = 825 / 900 = 0,916666...
CloudScore = 1 − 10/100 = 0,90

GreenScoreTampilan
= (0,70 × 0,916666... + 0,20 × 0,90 + 0,10 × 1) × 100
= 92,1666...
≈ 92,2
```

Berikut hasil perbandingan kandidat dengan metode berbobot.

| Peringkat | Kandidat | Rata-rata radiasi | Rata-rata awan | Green Score |
| --- | --- | ---: | ---: | ---: |
| 1 | **10.00–12.00** | 825 W/m² | 10% | **92,2** |
| 2 | 09.00–11.00 | 700 W/m² | 20% | 80,4 |
| 3 | 08.00–10.00 | 500 W/m² | 35% | 61,9 |

Perhitungan memakai presisi angka internal sebelum pembulatan tampilan.

## 8. Pemilihan hasil dan penjelasan

Kandidat diurutkan berdasarkan Green Score terbesar. Jika skornya sama, waktu mulai paling awal didahulukan. Kandidat pertama menjadi rekomendasi terbaik. UI menampilkan maksimal sepuluh kandidat teratas dan menyorot interval terbaik pada grafik.

| Skor tampilan | Kategori |
| --- | --- |
| 70–100 | Sangat baik |
| 40–kurang dari 70 | Cukup |
| Kurang dari 40 | Rendah |

Kandidat terbaik merupakan pilihan terbaik **di antara kandidat yang tersedia dalam permintaan pengguna**. Jika semuanya buruk, hasil terbaik tetap dapat berkategori rendah. Aplikasi tidak otomatis memperluas rentang atau memilih tanggal lain.

Penjelasan dibentuk dari template dan nilai hasil hitung, mencakup aktivitas, estimasi total kWh, tanggal dan rentang permintaan, lokasi, waktu terbaik, skor, metode dan rumus, komponen skor, rata-rata radiasi/awan, proporsi siang, sumber, serta waktu pengambilan forecast.

Grafik batang menampilkan **potensi surya per jam** (`radiasi / 900`, dibatasi 0–1, lalu ditampilkan 0–100). Angka ini tidak sama dengan Green Score berbobot untuk keseluruhan kandidat.

## 9. Snapshot dan penyimpanan

`RecommendationResult` membawa snapshot input, forecast, metode, seluruh kandidat, dan penjelasannya. Mengubah input atau forecast membatalkan hasil lama dan menonaktifkan tombol simpan sampai rekomendasi dihitung ulang.

Saat pengguna menyimpan, repository menerima snapshot hasil tersebut, bukan membaca ulang field form. Hasil demo, hasil dengan waktu terbaik yang sudah lewat, dan forecast berumur lebih dari enam jam tidak boleh disimpan.

Aplikasi menyimpan hasil melalui transaksi pada lima tabel resmi.

| Tabel | Data yang disimpan |
| --- | --- |
| `activity` | Pemilik aktivitas, nama, durasi dalam menit, dan estimasi total kWh. |
| `weather_forecast` | Lokasi beserta zona waktu, tanggal aktivitas, ringkasan forecast, dan waktu pengambilan. |
| `time_slot` | Seluruh kandidat hasil perhitungan, tanggal/jam mulai-selesai, serta skor skala 0–100. |
| `recommendation` | Kandidat terbaik yang dipilih engine, skor, dan penjelasan lengkap. |
| `user` | Akun pemilik aktivitas yang direferensikan. Tidak dibuat ulang saat menyimpan rekomendasi. |

Riwayat dibaca dari database berdasarkan akun pengguna. Snapshot data forecast per jam tidak disimpan lengkap ke tabel resmi. Yang tersedia adalah ringkasan forecast dan evidence teks dalam `recommendation.explanation`.

## 10. Hal yang memengaruhi hasil dan batasannya

- **Tanggal, durasi, dan rentang waktu** menentukan kandidat yang dibandingkan.
- **Lokasi** menentukan forecast dan interpretasi waktu lokal.
- **Radiasi, awan, indikator siang, dan metode kalkulator** menentukan skor.
- **Nama aktivitas dan estimasi kWh tidak memengaruhi ranking saat ini.** Dua aktivitas dengan durasi, rentang, tanggal, lokasi, metode, dan snapshot forecast yang sama akan mendapat rekomendasi yang sama.
- **Suhu belum memengaruhi skor.** Suhu tetap diperlukan sebagai bagian dari interval forecast yang lengkap pada implementasi parser saat ini.
- **Tidak ada pemeriksaan bentrok antarsesama aktivitas.** Setiap permintaan dihitung secara independen.
- **Forecast merupakan prakiraan**, sehingga kondisi aktual saat aktivitas dilakukan dapat berbeda.
- **Skor malam belum tentu nol pada metode berbobot.** Jika radiasi nol dan semua interval malam, komponen awan masih dapat memberi nilai sampai 20/100. Ini merupakan konsekuensi rumus saat ini, bukan bukti adanya produksi surya malam hari.
- **Jadwal tersimpan tidak membuktikan aktivitas sudah dilakukan.** Dashboard menghitung jadwal dan skor, bukan konsumsi energi atau emisi aktual.
- **Skor 92,2 tidak berarti 92,2% listrik berasal dari surya.** Belum ada integrasi intensitas karbon jaringan, produksi panel, smart meter, atau pengukuran konsumsi aktual.

## 11. Peta kode

| File | Tanggung jawab |
| --- | --- |
| [WeatherService.cs](../Form/CarbonShift/WeatherService.cs) | HTTP, pencarian kota, parsing interval, cache, data demo, dan pemeriksaan kelayakan simpan. |
| [Core.cs](../Form/CarbonShift/Core.cs) | Model lokasi/forecast/input/hasil, pembentukan kandidat, pengurutan, kategori, dan penjelasan. |
| [GreenScore.cs](../Form/CarbonShift/GreenScore.cs) | Komponen skor, kalkulator surya saja, dan kalkulator berbobot. |
| [Pages.cs](../Form/CarbonShift/Pages.cs) | Input scheduler, tampilan ranking/grafik, invalidasi hasil, riwayat, CSV, dan statistik. |
| [Database.cs](../Form/CarbonShift/Database.cs) | Penyimpanan transaksi, pembacaan riwayat, dan perhitungan statistik dashboard. |
| [FormDashboard.cs](../Form/CarbonShift/FormDashboard.cs) | Pengaturan lokasi pada Dashboard, pengambilan forecast, dan navigasi. |
| [Folder pengujian](../tests/CarbonShift.Tests) | Contoh perilaku engine, parsing, cache, zona waktu, UI, dan integrasi database. |

Panduan menjalankan aplikasi dan pengujian tersedia di [README](../README.md).

## 12. Referensi API

- [Dokumentasi Open-Meteo Geocoding](https://open-meteo.com/en/docs/geocoding-api)
- [Dokumentasi Open-Meteo Weather Forecast](https://open-meteo.com/en/docs), terutama definisi waktu untuk radiasi dan parameter forecast per jam.

Jika rumus, parameter, atau perilaku kode berubah, perbarui dokumen ini beserta contoh perhitungannya.
