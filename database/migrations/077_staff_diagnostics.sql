-- The staff diagnostics API (GET /api/admin/diagnostics, OURS): listener counts and timings, no bodies. Its row in the
-- permission table of migration 076, the same as server/Aion.Core/Accounts/StaffPermissions.cs.
INSERT INTO auth.staff_permissions(action,min_role) VALUES
    ('api GET /api/admin/diagnostics','moderator');
