BEGIN;

-- Preserve the original five tables and their integer IDs. Only add the Auth mapping.
ALTER TABLE public."user" ADD COLUMN IF NOT EXISTS auth_user_id uuid UNIQUE REFERENCES auth.users(id) ON DELETE SET NULL;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM public."user" GROUP BY lower(btrim(email)) HAVING count(*)>1) THEN
        RAISE EXCEPTION 'Resolve duplicate normalized legacy emails before migration';
    END IF;
END $$;

CREATE SCHEMA IF NOT EXISTS carbonshift_private;
REVOKE ALL ON SCHEMA carbonshift_private FROM PUBLIC,anon,authenticated;
CREATE TABLE IF NOT EXISTS carbonshift_private.auth_attempt (
    bucket text PRIMARY KEY, started_at timestamptz NOT NULL, attempts integer NOT NULL
);
CREATE TABLE IF NOT EXISTS carbonshift_private.preview (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(), user_id integer NOT NULL REFERENCES public."user"(user_id),
    payload jsonb NOT NULL, expires_at timestamptz NOT NULL, saved_id integer
);
ALTER TABLE carbonshift_private.auth_attempt ENABLE ROW LEVEL SECURITY;
ALTER TABLE carbonshift_private.preview ENABLE ROW LEVEL SECURITY;
CREATE TABLE IF NOT EXISTS carbonshift_private.forecast_cache (key text PRIMARY KEY,payload jsonb NOT NULL,fetched_at timestamptz NOT NULL);
ALTER TABLE carbonshift_private.forecast_cache ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON ALL TABLES IN SCHEMA carbonshift_private FROM PUBLIC,anon,authenticated;

CREATE OR REPLACE FUNCTION carbonshift_private.current_user_id() RETURNS integer
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
    SELECT user_id FROM public."user" WHERE auth_user_id=auth.uid()
$$;
REVOKE ALL ON FUNCTION carbonshift_private.current_user_id() FROM PUBLIC;
GRANT USAGE ON SCHEMA carbonshift_private TO authenticated;
GRANT EXECUTE ON FUNCTION carbonshift_private.current_user_id() TO authenticated;

CREATE OR REPLACE FUNCTION carbonshift_private.on_auth_created() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE legacy public."user"%ROWTYPE;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(lower(btrim(NEW.email))));
    SELECT * INTO legacy FROM public."user" WHERE lower(btrim(email))=lower(btrim(NEW.email)) FOR UPDATE;
    IF FOUND THEN
        -- raw_app_meta_data is writable by Auth administrators only. User metadata is never trusted here.
        IF legacy.auth_user_id IS NOT NULL OR
           (NEW.raw_app_meta_data->>'carbonshift_legacy_user_id') IS DISTINCT FROM legacy.user_id::text THEN
            RAISE EXCEPTION 'Existing account requires legacy password verification';
        END IF;
        UPDATE public."user" SET auth_user_id=NEW.id WHERE user_id=legacy.user_id;
    ELSE
        INSERT INTO public."user"(name,email,password_hash,auth_user_id)
        VALUES (left(COALESCE(NULLIF(btrim(NEW.raw_user_meta_data->>'name'),''),'Pengguna'),100),lower(btrim(NEW.email)),'supabase-auth',NEW.id);
    END IF;
    RETURN NEW;
END $$;
REVOKE ALL ON FUNCTION carbonshift_private.on_auth_created() FROM PUBLIC,anon,authenticated;
DROP TRIGGER IF EXISTS carbonshift_auth_created ON auth.users;
CREATE TRIGGER carbonshift_auth_created AFTER INSERT ON auth.users FOR EACH ROW EXECUTE FUNCTION carbonshift_private.on_auth_created();

CREATE OR REPLACE FUNCTION public.carbonshift_profile() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
    SELECT jsonb_build_object('userId',user_id,'name',name,'email',email)
    FROM public."user" WHERE auth_user_id=auth.uid()
$$;
CREATE OR REPLACE FUNCTION public.carbonshift_history() RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
    SELECT COALESCE(jsonb_agg(row ORDER BY start_time DESC,recommendation_id DESC),'[]'::jsonb)
    FROM (
        SELECT t.start_time,r.recommendation_id,jsonb_build_object(
            'id',r.recommendation_id,'nama',a.activity_name,'durationMinutes',a.duration_minutes,'kWh',a.power_consumption_kwh,
            'start',t.start_time,'end',t.end_time,'greenScore',COALESCE(r.green_score,0),'dibuat',r.created_at,
            'lokasi',f.location,'explanation',COALESCE(r.explanation,'Penjelasan tidak tersedia untuk data lama.')) AS row
        FROM public.activity a JOIN public.time_slot t ON t.activity_id=a.activity_id
        JOIN public.recommendation r ON r.timeslot_id=t.timeslot_id JOIN public.weather_forecast f ON f.forecast_id=t.forecast_id
        WHERE a.user_id=carbonshift_private.current_user_id()
    ) items
$$;
CREATE OR REPLACE FUNCTION public.carbonshift_delete_history() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE uid integer:=carbonshift_private.current_user_id(); ids integer[];
BEGIN
    IF uid IS NULL THEN RAISE EXCEPTION 'Authentication required' USING ERRCODE='42501'; END IF;
    PERFORM pg_advisory_xact_lock(uid,2718);
    SELECT array_agg(DISTINCT t.forecast_id) INTO ids FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id WHERE a.user_id=uid;
    DELETE FROM carbonshift_private.preview WHERE user_id=uid;
    DELETE FROM public.recommendation WHERE timeslot_id IN (SELECT t.timeslot_id FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id WHERE a.user_id=uid);
    DELETE FROM public.time_slot WHERE activity_id IN (SELECT activity_id FROM public.activity WHERE user_id=uid);
    DELETE FROM public.activity WHERE user_id=uid;
    DELETE FROM public.weather_forecast f WHERE forecast_id=ANY(ids) AND NOT EXISTS(SELECT 1 FROM public.time_slot t WHERE t.forecast_id=f.forecast_id);
END $$;

CREATE OR REPLACE FUNCTION public.carbonshift_save_preview(p_preview_id uuid) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE uid integer:=carbonshift_private.current_user_id(); draft carbonshift_private.preview%ROWTYPE;
    req jsonb; slot jsonb; aid integer; fid integer; tid integer; rid integer;
BEGIN
    IF uid IS NULL THEN RAISE EXCEPTION 'Authentication required' USING ERRCODE='42501'; END IF;
    PERFORM pg_advisory_xact_lock(uid,2718);
    SELECT * INTO draft FROM carbonshift_private.preview WHERE id=p_preview_id AND user_id=uid FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Preview unavailable' USING ERRCODE='P0002'; END IF;
    IF draft.saved_id IS NOT NULL THEN RETURN jsonb_build_object('recommendationId',draft.saved_id); END IF;
    IF draft.expires_at<=now() OR (draft.payload->'best'->>'start')::timestamptz<now() OR
        (draft.payload->>'fetchedAt')::timestamptz<now()-interval '6 hours' THEN
        RAISE EXCEPTION 'Preview expired' USING ERRCODE='P0002';
    END IF;
    req:=draft.payload->'request'; slot:=draft.payload->'best';
    INSERT INTO public.activity(user_id,activity_name,duration_minutes,power_consumption_kwh)
    VALUES(uid,req->>'activityName',(req->>'durationHours')::integer*60,(req->>'energyKwh')::numeric) RETURNING activity_id INTO aid;
    INSERT INTO public.weather_forecast(location,forecast_date,temperature,weather_condition,solar_potential,retrieved_at)
    VALUES(draft.payload->>'locationLabel',(req->>'date')::date,(draft.payload->>'temperature')::numeric,
        draft.payload->>'condition',(draft.payload->>'solarPotential')::numeric,
        (draft.payload->>'fetchedAt')::timestamptz AT TIME ZONE (draft.payload->>'timeZone')) RETURNING forecast_id INTO fid;
    INSERT INTO public.time_slot(activity_id,forecast_id,start_time,end_time,green_score)
    VALUES(aid,fid,(slot->>'start')::timestamptz AT TIME ZONE (draft.payload->>'timeZone'),
        (slot->>'end')::timestamptz AT TIME ZONE (draft.payload->>'timeZone'),(slot->>'greenScore')::numeric) RETURNING timeslot_id INTO tid;
    INSERT INTO public.recommendation(timeslot_id,explanation,green_score)
    VALUES(tid,draft.payload->>'explanation',(slot->>'greenScore')::numeric) RETURNING recommendation_id INTO rid;
    UPDATE carbonshift_private.preview SET saved_id=rid WHERE id=p_preview_id;
    RETURN jsonb_build_object('recommendationId',rid);
END $$;

-- Only Edge Functions may use the following administrative RPCs.
CREATE OR REPLACE FUNCTION public.carbonshift_legacy_account(p_email text) RETURNS jsonb
LANGUAGE sql STABLE SECURITY DEFINER SET search_path='' AS $$
    SELECT jsonb_build_object('userId',user_id,'name',name,'email',email,'passwordHash',password_hash,'authUserId',auth_user_id)
    FROM public."user" WHERE lower(btrim(email))=lower(btrim(p_email)) AND password_hash LIKE 'pbkdf2-sha256$%'
$$;
CREATE OR REPLACE FUNCTION public.carbonshift_auth_attempt(p_bucket text) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE tries integer;
BEGIN
    DELETE FROM carbonshift_private.auth_attempt WHERE started_at<now()-interval '1 day';
    INSERT INTO carbonshift_private.auth_attempt(bucket,started_at,attempts) VALUES(p_bucket,now(),1)
    ON CONFLICT(bucket) DO UPDATE SET attempts=CASE WHEN carbonshift_private.auth_attempt.started_at<now()-interval '5 minutes' THEN 1 ELSE carbonshift_private.auth_attempt.attempts+1 END,
        started_at=CASE WHEN carbonshift_private.auth_attempt.started_at<now()-interval '5 minutes' THEN now() ELSE carbonshift_private.auth_attempt.started_at END
    RETURNING attempts INTO tries;
    RETURN tries<=20;
END $$;
CREATE OR REPLACE FUNCTION public.carbonshift_create_preview(p_auth_user_id uuid,p_payload jsonb,p_expires_at timestamptz) RETURNS uuid
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE uid integer; result uuid;
BEGIN
    SELECT user_id INTO uid FROM public."user" WHERE auth_user_id=p_auth_user_id;
    IF uid IS NULL THEN RAISE EXCEPTION 'Account unavailable'; END IF;
    DELETE FROM carbonshift_private.preview WHERE expires_at<now()-interval '1 day';
    IF p_expires_at>now()+interval '15 minutes 5 seconds' OR p_expires_at<=now() THEN RAISE EXCEPTION 'Invalid expiry'; END IF;
    IF (SELECT count(*) FROM carbonshift_private.preview WHERE user_id=uid AND expires_at>now())>=30 THEN RAISE EXCEPTION 'Too many previews'; END IF;
    INSERT INTO carbonshift_private.preview(user_id,payload,expires_at) VALUES(uid,p_payload,p_expires_at) RETURNING id INTO result;
    RETURN result;
END $$;

CREATE OR REPLACE FUNCTION public.carbonshift_forecast_cache(p_key text,p_payload jsonb DEFAULT NULL) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path='' AS $$
DECLARE result jsonb;
BEGIN
    DELETE FROM carbonshift_private.forecast_cache WHERE fetched_at<now()-interval '6 hours';
    IF p_payload IS NOT NULL THEN
        INSERT INTO carbonshift_private.forecast_cache(key,payload,fetched_at) VALUES(p_key,p_payload,(p_payload->>'fetchedAt')::timestamptz)
        ON CONFLICT(key) DO UPDATE SET payload=excluded.payload,fetched_at=excluded.fetched_at;
    END IF;
    SELECT payload INTO result FROM carbonshift_private.forecast_cache WHERE key=p_key;
    RETURN result;
END $$;

-- Public tables are read-only through RLS. All writes use checked transactional RPCs.
REVOKE ALL ON TABLE public."user",public.activity,public.weather_forecast,public.time_slot,public.recommendation FROM PUBLIC,anon,authenticated;
GRANT SELECT(user_id,name,email,created_at,auth_user_id) ON public."user" TO authenticated;
GRANT SELECT ON public.activity,public.weather_forecast,public.time_slot,public.recommendation TO authenticated;
DO $$ DECLARE t text; seq text; col text;
BEGIN
    FOR t,col IN SELECT * FROM (VALUES ('user','user_id'),('activity','activity_id'),('weather_forecast','forecast_id'),('time_slot','timeslot_id'),('recommendation','recommendation_id')) AS items(t,col) LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY',t);
        seq:=pg_get_serial_sequence(format('public.%I',t),col);
        EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM PUBLIC,anon,authenticated',seq);
        EXECUTE format('DROP POLICY IF EXISTS carbonshift_backend ON public.%I',t);
    END LOOP;
    IF EXISTS(SELECT 1 FROM pg_roles WHERE rolname='carbonshift_api') THEN ALTER ROLE carbonshift_api NOLOGIN; END IF;
END $$;
DROP POLICY IF EXISTS carbonshift_own_user ON public."user";
CREATE POLICY carbonshift_own_user ON public."user" FOR SELECT TO authenticated USING(auth_user_id=auth.uid());
DROP POLICY IF EXISTS carbonshift_own_activity ON public.activity;
CREATE POLICY carbonshift_own_activity ON public.activity FOR SELECT TO authenticated USING(user_id=carbonshift_private.current_user_id());
DROP POLICY IF EXISTS carbonshift_own_slot ON public.time_slot;
CREATE POLICY carbonshift_own_slot ON public.time_slot FOR SELECT TO authenticated USING(EXISTS(SELECT 1 FROM public.activity a WHERE a.activity_id=time_slot.activity_id AND a.user_id=carbonshift_private.current_user_id()));
DROP POLICY IF EXISTS carbonshift_own_recommendation ON public.recommendation;
CREATE POLICY carbonshift_own_recommendation ON public.recommendation FOR SELECT TO authenticated USING(EXISTS(SELECT 1 FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id WHERE t.timeslot_id=recommendation.timeslot_id AND a.user_id=carbonshift_private.current_user_id()));
DROP POLICY IF EXISTS carbonshift_own_forecast ON public.weather_forecast;
CREATE POLICY carbonshift_own_forecast ON public.weather_forecast FOR SELECT TO authenticated USING(EXISTS(SELECT 1 FROM public.time_slot t JOIN public.activity a ON a.activity_id=t.activity_id WHERE t.forecast_id=weather_forecast.forecast_id AND a.user_id=carbonshift_private.current_user_id()));

REVOKE ALL ON FUNCTION public.carbonshift_profile(),public.carbonshift_history(),public.carbonshift_delete_history(),public.carbonshift_save_preview(uuid) FROM PUBLIC,anon;
GRANT EXECUTE ON FUNCTION public.carbonshift_profile(),public.carbonshift_history(),public.carbonshift_delete_history(),public.carbonshift_save_preview(uuid) TO authenticated;
REVOKE ALL ON FUNCTION public.carbonshift_legacy_account(text),public.carbonshift_auth_attempt(text),public.carbonshift_create_preview(uuid,jsonb,timestamptz),public.carbonshift_forecast_cache(text,jsonb) FROM PUBLIC,anon,authenticated;
GRANT EXECUTE ON FUNCTION public.carbonshift_legacy_account(text),public.carbonshift_auth_attempt(text),public.carbonshift_create_preview(uuid,jsonb,timestamptz),public.carbonshift_forecast_cache(text,jsonb) TO service_role;
NOTIFY pgrst,'reload schema';
COMMIT;
