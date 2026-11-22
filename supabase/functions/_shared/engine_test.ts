import { generate, parseForecast, type Forecast, type Location } from "./engine.ts";
function assert(value: unknown, message = "Assertion failed"): asserts value { if (!value) throw new Error(message); }
const location: Location = { name: "Yogyakarta", province: "DI Yogyakarta", latitude: -7.8, longitude: 110.36, timeZoneId: "Asia/Jakarta" };
const now = new Date("2026-11-22T00:00:00Z");
function forecast(zone = "Asia/Jakarta"): Forecast {
  const offset = zone === "Asia/Jakarta" ? "+07:00" : zone === "Asia/Makassar" ? "+08:00" : "+09:00";
  return { location: { ...location, timeZoneId: zone }, fetchedAt: now.toISOString(), source: 0, warning: null,
    hours: Array.from({ length: 24 }, (_, i) => ({ start: `2026-11-23T${String(i).padStart(2, "0")}:00:00${offset}`, temperature: 28, tutupanAwan: 20, radiasi: 720, isDay: true })) };
}
const request = { activityName: "Laptop", durationHours: 2, energyKwh: .12, date: "2026-11-23", earliestHour: 6, latestHour: 18 };
Deno.test("weighted score, earliest tie, best ten and request evidence", () => {
  const result = generate(request, forecast(), "weighted", now);
  assert(Math.abs(result.slots[0].greenScore - 82) < 1e-8);
  assert(result.slots.length === 10 && result.slots[0].start.includes("T06:00:00+07:00"));
  assert(result.explanation.includes("720.0") && result.payload.request === request);
  const solar = generate(request, forecast(), "solar", now); assert(solar.slots[0].greenScore === 80);
});
Deno.test("day flag affects score and duplicate/missing intervals are excluded", () => {
  const f = forecast(); f.hours = f.hours.map(h => ({ ...h, isDay: false }));
  assert(Math.abs(generate(request, f, "weighted", now).slots[0].greenScore - 72) < 1e-8);
  f.hours.push(f.hours[6]);
  assert(generate(request, f, "weighted", now).slots[0].start.includes("T07:"));
  f.hours = f.hours.filter(h => !h.start.includes("T07:"));
  assert(generate(request, f, "weighted", now).slots[0].start.includes("T08:"));
});
Deno.test("WIB WITA WIT retain local date and offset", () => {
  for (const zone of ["Asia/Jakarta", "Asia/Makassar", "Asia/Jayapura"]) {
    const result = generate(request, forecast(zone), "weighted", now);
    assert(result.slots[0].start.startsWith("2026-11-23T06:00:00"));
    assert(result.payload.timeZone === zone);
  }
});
Deno.test("previous-hour radiation mapping uses next timestamp", () => {
  const f = parseForecast({ hourly: { time: ["2026-11-22T11:00", "2026-11-22T12:00"],
    temperature_2m: [29, 30], cloud_cover: [20, 40], is_day: [1, 1], shortwave_radiation: [100, 700] } }, location, now);
  assert(f.hours.length === 1 && f.hours[0].start.includes("T11:00:00") && f.hours[0].radiasi === 700 && f.hours[0].temperature === 29);
});
Deno.test("missing forecast values do not become zero", () => {
  let failed = false;
  try { parseForecast({ hourly: { time: ["2026-11-22T11:00", "2026-11-22T12:00"], temperature_2m: [null], cloud_cover: [20], is_day: [1], shortwave_radiation: [100, 700] } }, location, now); }
  catch { failed = true; } assert(failed);
});
Deno.test("invalid duration, method, expired forecast, demo and past dates fail", () => {
  for (const action of [
    () => generate({ ...request, durationHours: 13 }, forecast(), "weighted", now),
    () => generate({ ...request, energyKwh: .001 }, forecast(), "weighted", now),
    () => generate(request, forecast(), "fake", now),
    () => generate({ ...request, date: "2026-11-21" }, forecast(), "weighted", now),
    () => generate(request, { ...forecast(), source: 3 }, "weighted", now),
    () => generate(request, { ...forecast(), fetchedAt: "2026-11-21T00:00:00Z" }, "weighted", now),
  ]) { let failed = false; try { action(); } catch { failed = true; } assert(failed); }
});
