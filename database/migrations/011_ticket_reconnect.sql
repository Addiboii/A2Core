-- The client logs in again with its game ticket when it returns to the login screen
-- (character selection from the world). A redeemed ticket now stays valid for those
-- reconnects during its account session: login extends its expiry instead of deleting it.
GRANT UPDATE (expires_at) ON auth.game_tickets TO aion_auth;
