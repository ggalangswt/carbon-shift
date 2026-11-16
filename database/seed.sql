-- Data awal CarbonShift (akun demo untuk login aplikasi).
-- Jalankan setelah schema.psql:
--   psql -U postgres -h localhost -d carbonshift -f database/seed.sql
--
-- Akun demo : admin@carbonshift.com / 12345
-- Password disimpan sebagai hash PBKDF2-SHA256 (100.000 iterasi, salt 16 byte),
-- format: pbkdf2-sha256$<iterasi>$<salt base64>$<hash base64>.
-- Verifikasi di aplikasi: PasswordHasher.Verify() (Rfc2898DeriveBytes.Pbkdf2).

INSERT INTO "user" (name, email, password_hash)
VALUES ('Admin', 'admin@carbonshift.com',
        'pbkdf2-sha256$100000$eJvlmynYQhUBIqnJG4lr9g==$19muymcIggmDW0aL+UdlBlmRakWXlBuxpkcrrcA0qE4=')
ON CONFLICT (email) DO NOTHING;

SELECT user_id, name, email, left(password_hash, 30) || '...' AS password_hash, created_at
FROM "user";
