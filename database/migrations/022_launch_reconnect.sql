-- After a disconnect the client asks the bootstrap for a login token again with the same launch code
-- (live 2026-10-16: authn_token 401 more than 15 minutes after the launch, then NC's own login screen).
-- A redeemed launch now stays valid for such re-logins while its account session lasts (at most 12 h
-- after its last use), like the game ticket (011); each redemption still issues a fresh game ticket.
GRANT UPDATE (expires_at) ON auth.client_launches TO aion_auth;
