import { admin, body, failure, fields, headers, identity, json, quota, RequestError } from "../_shared/http.ts";
import { generate, parseForecast, validLocation, localDate, dayAfter, offset, type Forecast, type Location } from "../_shared/engine.ts";

async function getJson(url: string) {
  for (let attempt = 0; ; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(10000) });
      if (!response.ok) throw new Error("Upstream unavailable");
      return await response.json();
    } catch (error) { if (attempt) throw error; }
  }
}
async function locations(query: string): Promise<Location[]> {
  if (query.trim().length < 2 || query.length > 100) throw new RequestError("Masukkan nama kota yang valid.");
  const raw = await getJson(`https://geocoding-api.open-meteo.com/v1/search?name=${encodeURIComponent(query.trim())}&count=10&language=id&countryCode=ID`);
  return (raw.results ?? []).filter((r: any) => r.country_code === "ID").map((r: any) => ({
    name: r.name, province: r.admin1 ?? "", latitude: r.latitude, longitude: r.longitude, timeZoneId: r.timezone,
  })).filter(validLocation);
}
async function forecastFor(location: Location): Promise<Forecast> {
  const service = admin(); const now = new Date();
  const key = `${location.latitude.toFixed(5)}_${location.longitude.toFixed(5)}_${location.timeZoneId}`;
  const cached = await service.rpc("carbonshift_forecast_cache", { p_key: key });
  const value = cached.error ? null : cached.data as Forecast | null;
  const age = value ? now.getTime() - Date.parse(value.fetchedAt) : Infinity;
  const today = localDate(location, now);
  const noWholeHourToday = now.getTime() > Date.parse(`${today}T23:00:00${offset(location)}`);
  const complete = !!value && [0, 1, 2].every(i => value.hours.some(h => h.start.slice(0, 10) === dayAfter(today, i) && Date.parse(h.start) >= now.getTime()) || (i === 0 && noWholeHourToday));
  if (value && complete && age >= 0 && age <= 3600000) return { ...value, source: 1 };
  try {
    const raw = await getJson(`https://api.open-meteo.com/v1/forecast?latitude=${location.latitude}&longitude=${location.longitude}&hourly=temperature_2m,cloud_cover,shortwave_radiation,is_day&forecast_days=4&timezone=${encodeURIComponent(location.timeZoneId)}`);
    const fresh = parseForecast(raw, location, new Date());
    await service.rpc("carbonshift_forecast_cache", { p_key: key, p_payload: fresh });
    return fresh;
  } catch (error) {
    if (value && complete && age >= 0 && age <= 21600000) return { ...value, source: 2, warning: "API cuaca belum tersedia. Memakai cache cadangan." };
    throw error;
  }
}
Deno.serve(async request => {
  if (request.method === "OPTIONS") return new Response(null, { headers });
  try {
    const { client, user } = await identity(request);
    const url = new URL(request.url); const route = url.pathname.split("/carbonshift")[1] ?? "/";
    if (route === "/locations" && request.method === "GET") {
      await quota("locations/" + user.id); return json(await locations(url.searchParams.get("query") ?? ""));
    }
    if (route === "/recommendations/preview" && request.method === "POST") {
      const input = await body(request); fields(input, ["location", "request", "method"]);
      const location = input.location as Location;
      if (!validLocation(location)) throw new RequestError("Pilih lokasi yang valid.");
      if (!(await locations(location.name)).some(item => JSON.stringify(item) === JSON.stringify({ name: location.name, province: location.province, latitude: location.latitude, longitude: location.longitude, timeZoneId: location.timeZoneId })))
        throw new RequestError("Cari dan pilih kota kembali.");
      await quota("preview/" + user.id);
      const forecast = await forecastFor(location); const now = new Date();
      let result;
      try { result = generate(input.request as any, forecast, String(input.method), now); }
      catch (error) { throw new RequestError((error as Error).message); }
      const expiresAt = new Date(Math.min(now.getTime() + 900000, Date.parse(result.slots[0].start), Date.parse(forecast.fetchedAt) + 21600000)).toISOString();
      const { data: previewId, error } = await admin().rpc("carbonshift_create_preview", {
        p_auth_user_id: user.id, p_payload: result.payload, p_expires_at: expiresAt,
      });
      if (error) throw new RequestError("Rekomendasi belum dapat disiapkan. Tunggu sebentar lalu coba lagi.", 409);
      return json({ previewId, expiresAt, request: result.request, forecast, method: result.method, slots: result.slots, explanation: result.explanation });
    }
    // The desktop calls the following RPCs directly with its native Auth token. No service-role access is used for user writes.
    if (route === "/me" && request.method === "GET") {
      const { data, error } = await client.rpc("carbonshift_profile"); if (error || !data) throw new RequestError("Akun belum tersedia.", 401); return json(data);
    }
    throw new RequestError("Halaman tidak tersedia.", 404);
  } catch (error) { return failure(error); }
});
