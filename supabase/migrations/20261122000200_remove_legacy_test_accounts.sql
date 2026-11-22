BEGIN;

-- Stop concurrent signups or schedule writes while selecting and removing obsolete test accounts.
LOCK TABLE auth.users IN SHARE MODE;
LOCK TABLE public."user",public.activity,public.time_slot,public.recommendation,public.weather_forecast IN SHARE ROW EXCLUSIVE MODE;

CREATE TEMP TABLE carbonshift_obsolete_users ON COMMIT DROP AS
SELECT u.user_id FROM public."user" u
WHERE u.auth_user_id IS NULL AND u.password_hash LIKE 'pbkdf2-sha256$%'
  AND NOT EXISTS(SELECT 1 FROM auth.users a WHERE lower(btrim(a.email))=lower(btrim(u.email)));
CREATE TEMP TABLE carbonshift_obsolete_forecasts ON COMMIT DROP AS
SELECT DISTINCT t.forecast_id FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id
WHERE a.user_id IN (SELECT user_id FROM carbonshift_obsolete_users);

DELETE FROM carbonshift_private.preview WHERE user_id IN (SELECT user_id FROM carbonshift_obsolete_users);
DELETE FROM public.recommendation WHERE timeslot_id IN (
    SELECT t.timeslot_id FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id
    WHERE a.user_id IN (SELECT user_id FROM carbonshift_obsolete_users));
DELETE FROM public.time_slot WHERE activity_id IN (SELECT activity_id FROM public.activity WHERE user_id IN (SELECT user_id FROM carbonshift_obsolete_users));
DELETE FROM public.activity WHERE user_id IN (SELECT user_id FROM carbonshift_obsolete_users);
DELETE FROM public.weather_forecast f WHERE forecast_id IN (SELECT forecast_id FROM carbonshift_obsolete_forecasts)
  AND NOT EXISTS(SELECT 1 FROM public.time_slot t WHERE t.forecast_id=f.forecast_id);
DELETE FROM public."user" WHERE user_id IN (SELECT user_id FROM carbonshift_obsolete_users);

CREATE OR REPLACE FUNCTION carbonshift_private.on_auth_created() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(lower(btrim(NEW.email))));
    -- Never attach a new identity to another existing application account by email alone.
    IF EXISTS(SELECT 1 FROM public."user" WHERE lower(btrim(email))=lower(btrim(NEW.email))) THEN
        RAISE EXCEPTION 'Email already belongs to an application account';
    END IF;
    INSERT INTO public."user"(name,email,password_hash,auth_user_id)
    VALUES(left(COALESCE(NULLIF(btrim(NEW.raw_user_meta_data->>'name'),''),'Pengguna'),100),lower(btrim(NEW.email)),'supabase-auth',NEW.id);
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION carbonshift_private.on_auth_created() FROM PUBLIC,anon,authenticated;

-- Auth may populate metadata after the INSERT. Profile updates are scoped strictly by the immutable Auth UUID.
CREATE OR REPLACE FUNCTION carbonshift_private.on_auth_profile_updated() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
BEGIN
    UPDATE public."user" SET name=left(COALESCE(NULLIF(btrim(NEW.raw_user_meta_data->>'name'),''),name),100),
        email=lower(btrim(NEW.email)) WHERE auth_user_id=NEW.id;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION carbonshift_private.on_auth_profile_updated() FROM PUBLIC,anon,authenticated;
DROP TRIGGER IF EXISTS carbonshift_auth_profile_updated ON auth.users;
CREATE TRIGGER carbonshift_auth_profile_updated AFTER UPDATE OF raw_user_meta_data,email ON auth.users
FOR EACH ROW EXECUTE FUNCTION carbonshift_private.on_auth_profile_updated();

DROP FUNCTION IF EXISTS public.carbonshift_legacy_account(text);
NOTIFY pgrst,'reload schema';
COMMIT;
