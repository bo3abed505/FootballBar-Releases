-- Migration: Purge Ghost Mock Catalog Entities
DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%' OR id IN ('comp_laliga', 'comp_egypt_pl', 'comp_international');
DELETE FROM competition_seasons WHERE id IN ('season_laliga_2025', 'season_egypt_2025', 'season_international_2025');
DELETE FROM teams WHERE name LIKE '%(Dev Mock)%' OR id IN ('team_barcelona', 'team_realmadrid', 'team_alahly', 'team_egypt', 'team_spain');
DELETE FROM fixtures WHERE id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR schedule_source_provider = 'mock' OR id LIKE 'fix_%mock%';
DELETE FROM events WHERE fixture_id IN ('fix_elclasico_2026', 'fix_spain_egypt_2026') OR event_uid LIKE 'evt_fix_%';
