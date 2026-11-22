import { body, failure, fields, headers, json, quota, RequestError, userClient } from "../_shared/http.ts";

Deno.serve(async request => {
  if (request.method === "OPTIONS") return new Response(null, { headers });
  try {
    if (request.method !== "POST") throw new RequestError("Metode tidak tersedia.", 405);
    const input = await body(request);
    fields(input, ["action", "name", "email", "password", "confirmation"]);
    const action = input.action;
    const email = String(input.email ?? "").trim().toLowerCase();
    const password = typeof input.password === "string" ? input.password : "";
    if (!/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email) || email.length > 100) throw new RequestError("Isi email yang valid, maksimal 100 karakter.", 400, "email");
    if (!password.trim() || password.length > 128) throw new RequestError("Isi password yang valid.", 400, "password");
    // Native Supabase Auth also enforces its own limits.
    await quota("email/" + email);
    const ip = request.headers.get("x-forwarded-for")?.split(",")[0]?.trim();
    if (ip) await quota("ip/" + ip);
    let session;
    if (action === "register") {
      const name = String(input.name ?? "").trim();
      if (!name || name.length > 100) throw new RequestError("Nama wajib diisi, maksimal 100 karakter.", 400, "name");
      if (password.length < 8) throw new RequestError("Password harus 8-128 karakter.", 400, "password");
      if (password !== input.confirmation) throw new RequestError("Konfirmasi password belum sama.", 400, "confirmation");
      const { data, error } = await userClient().auth.signUp({ email, password, options: { data: { name } } });
      if (error) throw new RequestError("Akun belum dapat dibuat. Jika email sudah terdaftar, gunakan Masuk.", 400, "email");
      if (!data.session) throw new RequestError("Akun belum dapat digunakan. Hubungi pengelola.", 503);
      session = data.session;
    } else if (action === "login") {
      const { data, error } = await userClient().auth.signInWithPassword({ email, password });
      if (error || !data.session) throw new RequestError("Email atau password salah.", 401);
      session = data.session;
    } else throw new RequestError("Aksi tidak dikenal.");
    const { data: profile, error } = await userClient(session.access_token).rpc("carbonshift_profile");
    if (error || !profile) throw new Error("Profile unavailable");
    return json({ accessToken: session.access_token, expiresAt: new Date((session.expires_at ?? 0) * 1000).toISOString(), user: profile });
  } catch (error) { return failure(error); }
});
