import { createClient } from "npm:@supabase/supabase-js@2.86.0";

export const headers = { "Content-Type": "application/json", "Cache-Control": "no-store", "Access-Control-Allow-Origin": "*", "Access-Control-Allow-Headers": "authorization, apikey, content-type", "Access-Control-Allow-Methods": "POST, GET, OPTIONS" };
export class RequestError extends Error {
  constructor(message: string, public status = 400, public field?: string) { super(message); }
}
export function json(value: unknown, status = 200): Response { return new Response(JSON.stringify(value), { status, headers }); }
export function failure(error: unknown): Response {
  return error instanceof RequestError ? json({ message: error.message, field: error.field }, error.status)
    : json({ message: "Layanan belum dapat dihubungi. Coba lagi." }, 503);
}
export async function body(request: Request): Promise<Record<string, unknown>> {
  if (Number(request.headers.get("content-length") ?? 0) > 32768) throw new RequestError("Permintaan terlalu besar.", 413);
  const reader = request.body?.getReader(); const chunks: Uint8Array[] = []; let size = 0;
  if (!reader) throw new RequestError("Isian permintaan tidak valid.");
  try {
    while (true) {
      const { value, done } = await reader.read(); if (done) break;
      size += value.length;
      if (size > 32768) { await reader.cancel(); throw new RequestError("Permintaan terlalu besar.", 413); }
      chunks.push(value);
    }
  } finally { reader.releaseLock(); }
  const bytes = new Uint8Array(size); let position = 0;
  for (const chunk of chunks) { bytes.set(chunk, position); position += chunk.length; }
  try {
    const value = JSON.parse(new TextDecoder().decode(bytes));
    if (!value || Array.isArray(value) || typeof value !== "object") throw new Error();
    return value;
  } catch { throw new RequestError("Isian permintaan tidak valid."); }
}
export function fields(value: Record<string, unknown>, allowed: string[]) {
  if (Object.keys(value).some(key => !allowed.includes(key))) throw new RequestError("Field permintaan tidak dikenal.");
}
export function admin() {
  return createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!, {
    auth: { persistSession: false, autoRefreshToken: false, detectSessionInUrl: false },
  });
}
export function userClient(token?: string) {
  return createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_ANON_KEY")!, {
    auth: { persistSession: false, autoRefreshToken: false, detectSessionInUrl: false },
    global: token ? { headers: { Authorization: `Bearer ${token}` } } : undefined,
  });
}
export async function identity(request: Request) {
  const token = request.headers.get("authorization")?.match(/^Bearer (.+)$/i)?.[1];
  if (!token) throw new RequestError("Silakan masuk kembali.", 401);
  const client = userClient(token);
  const { data, error } = await client.auth.getUser(token);
  if (error || !data.user) throw new RequestError("Sesi berakhir. Silakan masuk kembali.", 401);
  return { client, user: data.user };
}
export async function quota(key: string) {
  const hash = Array.from(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(key))))
    .map(x => x.toString(16).padStart(2, "0")).join("");
  const { data, error } = await admin().rpc("carbonshift_auth_attempt", { p_bucket: hash });
  if (error) throw new Error("Rate limit unavailable");
  if (!data) throw new RequestError("Terlalu banyak percobaan. Tunggu sebentar lalu coba lagi.", 429);
}
