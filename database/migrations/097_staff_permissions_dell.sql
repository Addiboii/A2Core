-- Staff permission rows for the Dell version's staff routes and command kinds (merge 2026-11-13; ranks OURS, as
-- Aion.Core.Accounts.StaffPermissions, which must hold the same rows: HostSecurityChecks compares them). Without them
-- auth.staff_allowed refuses the requests and the host refuses to start (an admin route missing from the table).
INSERT INTO auth.staff_permissions(action,min_role) VALUES
    ('api GET /api/admin/health','gamemaster'),
    ('api GET /api/admin/itemdb','gamemaster'),
    ('api GET /api/admin/itemdb/items/{id:int}','gamemaster'),
    ('api GET /api/admin/itemdb/names/{language}','gamemaster'),
    ('api GET /api/admin/itemdb/unusable','gamemaster'),
    ('api GET /api/admin/items/{id:int}','gamemaster'),
    ('api GET /api/admin/maps/names','gamemaster'),
    ('api GET /api/admin/maps/{id:int}/layers','gamemaster'),
    ('api GET /api/admin/maps/{id:int}/tiles/{x:int}/{y:int}','gamemaster'),
    ('api POST /api/admin/characters/{id:guid}/items/delete','moderator'),
    ('api POST /api/admin/grant-many','moderator'),
    ('api GET /api/admin/premium-shop','gamemaster'),
    ('api POST /api/admin/premium-shop','admin'),
    ('api DELETE /api/admin/premium-shop','admin'),
    ('api GET /api/admin/random-shop','gamemaster'),
    ('api POST /api/admin/random-shop','admin'),
    ('api DELETE /api/admin/random-shop','admin'),
    ('command.currency','moderator');
