-- Migration: Purge Ghost Mock Catalog Entities
DELETE FROM competitions WHERE name LIKE '%(Dev Mock)%';
DELETE FROM teams WHERE name LIKE '%(Dev Mock)%';
