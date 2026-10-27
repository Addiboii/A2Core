-- Staff dashboard activity log and the buyback restore limit read audit events (the app could only insert them).
-- Staff actions from the dashboard (announcements, kicks, teleports, revives) are recorded as staff_* events.
GRANT SELECT ON audit.events TO aion_app;
CREATE INDEX audit_time_idx ON audit.events(event_id DESC);
CREATE INDEX audit_action_time_idx ON audit.events(action, event_id DESC);
