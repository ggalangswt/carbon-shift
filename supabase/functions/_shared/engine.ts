export type Location = { name: string; province: string; latitude: number; longitude: number; timeZoneId: string };
export type Hour = { start: string; temperature: number; tutupanAwan: number; radiasi: number; isDay: boolean };
export type Forecast = { location: Location; fetchedAt: string; hours: Hour[]; source: number; warning: string | null };
export type Schedule = { activityName: string; durationHours: number; energyKwh: number; date: string; earliestHour: number; latestHour: number };
export type Slot = { start: string; end: string; greenScore: number; radiation: number; cloudCover: number; dayScore: number };
const zones: Record<string, string> = { "Asia/Jakarta": "+07:00", "Asia/Makassar": "+08:00", "Asia/Jayapura": "+09:00" };
export function offset(location: Location): string {
  const value = zones[location.timeZoneId]; if (!value) throw new Error("Zona waktu tidak valid."); return value;
}
export function localDate(location: Location, now: Date): string {
  return new Date(now.getTime() + Number(offset(location).slice(1, 3)) * 3600000).toISOString().slice(0, 10);
}
export function dayAfter(date: string, days: number): string { return new Date(Date.parse(date + "T00:00:00Z") + days * 86400000).toISOString().slice(0, 10); }
function at(location: Location, date: string, hour: number): string {
  return `${hour === 24 ? dayAfter(date, 1) : date}T${String(hour % 24).padStart(2, "0")}:00:00${offset(location)}`;
}
export function validLocation(value: Location): boolean {
  return !!value && typeof value.name === "string" && value.name.length >= 2 && value.name.length <= 100 &&
    typeof value.province === "string" && value.province.length <= 100 && !!zones[value.timeZoneId] &&
    Number.isFinite(value.latitude) && value.latitude >= -90 && value.latitude <= 90 &&
    Number.isFinite(value.longitude) && value.longitude >= -180 && value.longitude <= 180;
}
export function parseForecast(raw: any, location: Location, now: Date): Forecast {
  if (!raw?.hourly || !Array.isArray(raw.hourly.time)) throw new Error("Forecast tidak lengkap.");
  const data = raw.hourly; const hours: Hour[] = [];
  const today = localDate(location, now); const last = dayAfter(today, 2);
  for (let i = 0; i + 1 < data.time.length; i++) {
    const time = data.time[i]; const next = data.time[i + 1];
    if (typeof time !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:00$/.test(time) || typeof next !== "string") continue;
    const start = time + ":00" + offset(location);
    if (time.slice(0, 10) < today || time.slice(0, 10) > last || Date.parse(next + ":00" + offset(location)) - Date.parse(start) !== 3600000) continue;
    const temperature = data.temperature_2m?.[i], cloud = data.cloud_cover?.[i], radiation = data.shortwave_radiation?.[i + 1], isDay = data.is_day?.[i];
    if (![temperature, cloud, radiation].every(v => typeof v === "number" && Number.isFinite(v)) || cloud < 0 || cloud > 100 || radiation < 0 || ![0, 1].includes(isDay)) continue;
    hours.push({ start, temperature, tutupanAwan: cloud, radiasi: radiation, isDay: isDay === 1 });
  }
  if (!hours.length) throw new Error("Forecast tidak memiliki interval lengkap.");
  return { location, fetchedAt: now.toISOString(), hours, source: 0, warning: null };
}
const mean = (values: number[]) => values.reduce((a, b) => a + b, 0) / values.length;
const clamp = (n: number) => Math.max(0, Math.min(1, n));
export function generate(request: Schedule, forecast: Forecast, method: string, now: Date) {
  if (!request || !request.activityName?.trim() || request.activityName.trim().length > 100) throw new Error("Nama aktivitas wajib diisi, maksimal 100 karakter.");
  if (![request.durationHours, request.earliestHour, request.latestHour].every(Number.isInteger) || request.durationHours < 1 || request.durationHours > 12 || request.earliestHour < 0 || request.earliestHour > 23 || request.latestHour < 1 || request.latestHour > 24 || request.latestHour - request.earliestHour < request.durationHours) throw new Error("Rentang waktu harus cukup untuk durasi 1-12 jam.");
  if (!Number.isFinite(request.energyKwh) || request.energyKwh < .01 || request.energyKwh > 9999.99 || Math.abs(Math.round(request.energyKwh * 100) / 100 - request.energyKwh) > 1e-9) throw new Error("Estimasi energi harus 0,01-9999,99 kWh dengan maksimal dua angka desimal.");
  const today = localDate(forecast.location, now);
  if (!/^\d{4}-\d{2}-\d{2}$/.test(request.date) || dayAfter(request.date, 0) !== request.date || request.date < today || request.date > dayAfter(today, 2)) throw new Error("Pilih hari ini, besok, atau lusa.");
  if (!["weighted", "solar"].includes(method)) throw new Error("Metode tidak dikenal.");
  const age = now.getTime() - Date.parse(forecast.fetchedAt);
  if (forecast.source === 3 || !Number.isFinite(age) || age < 0 || age > 21600000) throw new Error("Forecast kedaluwarsa. Ambil ulang forecast.");
  const map = new Map<number, Hour>(); const duplicates = new Set<number>();
  for (const h of forecast.hours) {
    const key = Date.parse(h.start);
    if (map.has(key)) duplicates.add(key);
    if ([h.temperature, h.tutupanAwan, h.radiasi].every(Number.isFinite) && h.tutupanAwan >= 0 && h.tutupanAwan <= 100 && h.radiasi >= 0 && /T\d{2}:00:00[+-]\d{2}:00$/.test(h.start)) map.set(key, h);
  }
  for (const key of duplicates) map.delete(key);
  const slots: Slot[] = [];
  for (let hour = request.earliestHour; hour + request.durationHours <= request.latestHour; hour++) {
    const start = at(forecast.location, request.date, hour); const instant = Date.parse(start);
    if (instant < now.getTime()) continue;
    const window = Array.from({ length: request.durationHours }, (_, i) => map.get(instant + i * 3600000));
    if (window.some(h => !h)) continue;
    const complete = window as Hour[];
    const radiation = mean(complete.map(h => h.radiasi)), cloudCover = mean(complete.map(h => h.tutupanAwan)), day = mean(complete.map(h => h.isDay ? 1 : 0));
    const score = method === "solar" ? clamp(radiation / 900) : clamp(.7 * clamp(radiation / 900) + .2 * clamp(1 - cloudCover / 100) + .1 * day);
    slots.push({ start, end: at(forecast.location, request.date, hour + request.durationHours), greenScore: score * 100, radiation, cloudCover, dayScore: day * 100 });
  }
  slots.sort((a, b) => b.greenScore - a.greenScore || Date.parse(a.start) - Date.parse(b.start));
  if (!slots.length) throw new Error("Tidak ada slot lengkap yang masih akan datang pada rentang ini.");
  const best = slots[0];
  const suffix = ` (${forecast.location.latitude.toFixed(4)},${forecast.location.longitude.toFixed(4)}) [${forecast.location.timeZoneId}]`;
  const name = `${forecast.location.name}${forecast.location.province ? ", " + forecast.location.province : ""}`;
  const label = name.slice(0, 100 - suffix.length) + suffix;
  const source = forecast.source === 0 ? "Open-Meteo (live)" : forecast.source === 1 ? "Open-Meteo (cache)" : "Cache cadangan";
  const formula = method === "weighted" ? "70% surya + 20% langit cerah + 10% siang" : "Radiasi rata-rata / 900 W/m²";
  const explanation = `Aktivitas ${request.activityName}. Estimasi total energi ${request.energyKwh.toFixed(2)} kWh.\n` +
    `Tanggal ${request.date}. Rentang permintaan ${request.earliestHour}:00 - ${request.latestHour}:00, durasi ${request.durationHours} jam.\n` +
    `Lokasi ${label}. Semua waktu mengikuti ${forecast.location.timeZoneId}.\n` +
    `Waktu terbaik ${best.start.slice(11, 16)} - ${best.end.slice(0, 10) > request.date ? "24:00" : best.end.slice(11, 16)}. Green Score ${best.greenScore.toFixed(1)}/100.\n` +
    `Metode ${formula}. Komponen (0-100) surya ${(clamp(best.radiation / 900) * 100).toFixed(1)}, langit cerah ${(100 - best.cloudCover).toFixed(1)}, siang ${best.dayScore.toFixed(1)}.\n` +
    `Rata-rata radiasi ${best.radiation.toFixed(1)} W/m² dan awan ${best.cloudCover.toFixed(1)}%.\n` +
    `Sumber: ${source}, diambil ${forecast.fetchedAt}. ${forecast.warning ?? ""}\n` +
    "Green Score menunjukkan kesesuaian waktu dengan potensi surya, bukan persentase listrik terbarukan atau pengurangan emisi.";
  const day = forecast.hours.filter(h => h.start.slice(0, 10) === request.date);
  const daylight = day.filter(h => h.isDay); const summary = daylight.length ? daylight : day;
  return { request, forecast, method, slots: slots.slice(0, 10), explanation,
    payload: { request, best, locationLabel: label, timeZone: forecast.location.timeZoneId, fetchedAt: forecast.fetchedAt, explanation,
      temperature: mean(summary.map(h => h.temperature)), condition: !daylight.length ? "Malam" : mean(summary.map(h => h.tutupanAwan)) < 25 ? "Cerah" : "Berawan",
      solarPotential: clamp(mean(summary.map(h => h.radiasi)) / 900) * 100 } };
}
