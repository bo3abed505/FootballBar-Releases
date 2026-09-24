import { describe, it, expect, beforeAll, afterAll, beforeEach, afterEach } from 'vitest';
import { buildServer } from '../src/server.js';
import { FastifyInstance } from 'fastify';
import { pool } from '../src/db/index.js';

describe('Checkpoint 8 — Backend Dynamic Search Catalog API', () => {
  let app: FastifyInstance;

  const TEST_TEAMS = [
    {
      id: 'team_cat_1',
      canonicalId: 'ft_team_cat_1',
      name: 'North London Red',
      shortName: 'Arsenal',
      code: 'ARS',
      country: 'England',
      logoUrl: 'https://example.com/logos/ars.png',
    },
    {
      id: 'team_cat_2',
      canonicalId: 'ft_team_cat_2',
      name: 'Chelsea Blues',
      shortName: 'Chelsea',
      code: 'CHE',
      country: 'England',
      logoUrl: 'https://example.com/logos/che.png',
    },
    {
      id: 'team_cat_3',
      canonicalId: 'ft_team_cat_3',
      name: 'Real Madrid CF',
      shortName: 'RMA',
      code: 'RMA',
      country: 'Spain',
      logoUrl: 'https://example.com/logos/rma.png',
    },
    {
      id: 'team_cat_4',
      canonicalId: 'ft_team_cat_4',
      name: '100% Football Club',
      shortName: 'Percent FC',
      code: 'PFC',
      country: 'England',
      logoUrl: 'https://example.com/logos/pfc.png',
    },
  ];

  const TEST_COMPETITIONS = [
    {
      id: 'comp_cat_1',
      canonicalId: 'ft_comp_cat_1',
      name: 'English Premier League',
      code: 'PL',
      country: 'England',
      logoUrl: 'https://example.com/logos/pl.png',
    },
    {
      id: 'comp_cat_2',
      canonicalId: 'ft_comp_cat_2',
      name: 'UEFA Champions League',
      code: 'UCL',
      country: 'Europe',
      logoUrl: 'https://example.com/logos/ucl.png',
    },
    {
      id: 'comp_cat_3',
      canonicalId: 'ft_comp_cat_3',
      name: 'Spanish La Liga',
      code: 'PD',
      country: 'Spain',
      logoUrl: 'https://example.com/logos/laliga.png',
    },
  ];

  const cleanupTestData = async () => {
    const teamIds = TEST_TEAMS.map((t) => `'${t.id}'`).join(',');
    const compIds = TEST_COMPETITIONS.map((c) => `'${c.id}'`).join(',');

    await pool.query(`DELETE FROM teams WHERE id IN (${teamIds})`);
    await pool.query(`DELETE FROM competitions WHERE id IN (${compIds})`);
  };

  const seedTestData = async () => {
    for (const t of TEST_TEAMS) {
      await pool.query(
        `INSERT INTO teams (id, canonical_id, name, short_name, code, country, logo_url, created_at, updated_at)
         VALUES ('${t.id}', '${t.canonicalId}', '${t.name}', '${t.shortName}', '${t.code}', '${t.country}', '${t.logoUrl}', NOW(), NOW())
         ON CONFLICT (id) DO NOTHING;`
      );
    }

    for (const c of TEST_COMPETITIONS) {
      await pool.query(
        `INSERT INTO competitions (id, canonical_id, name, code, type, country, logo_url, created_at, updated_at)
         VALUES ('${c.id}', '${c.canonicalId}', '${c.name}', '${c.code}', 'league', '${c.country}', '${c.logoUrl}', NOW(), NOW())
         ON CONFLICT (id) DO NOTHING;`
      );
    }
  };

  beforeAll(async () => {
    const dbInfo = await pool.query('SELECT current_database()');
    if (dbInfo.rows[0].current_database !== 'footballtray_test') {
      throw new Error(
        `[SAFETY ERROR] Tests are connected to '${dbInfo.rows[0].current_database}', expected 'footballtray_test'!`
      );
    }
    app = await buildServer();
  });

  beforeEach(async () => {
    await cleanupTestData();
    await seedTestData();
  });

  afterEach(async () => {
    await cleanupTestData();
  });

  afterAll(async () => {
    await cleanupTestData();
    await app.close();
  });

  // =========================================================================
  // Section 1: Teams Search (/api/v2/catalog/teams)
  // =========================================================================

  it('1. GET /api/v2/catalog/teams returns matched teams with canonical ID, name, and logo', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=Chelsea%20Blues',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    expect(body.schemaVersion).toBe('2.0');
    expect(body.version).toBe(2);
    expect(Array.isArray(body.data)).toBe(true);
    expect(body.data.length).toBe(1);

    const team = body.data[0];
    expect(team.id).toBe('ft_team_cat_2');
    expect(team.id.startsWith('ft_team_')).toBe(true);
    expect(team.name).toBe('Chelsea Blues');
    expect(team.shortName).toBe('Chelsea');
    expect(team.logo).toBe('https://example.com/logos/che.png');
    expect(team.logoUrl).toBe('https://example.com/logos/che.png');
  });

  it('2. GET /api/v2/catalog/teams performs case-insensitive search across name, shortName, and code', async () => {
    // Upper case code
    const resCode = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=ARS',
    });
    const bodyCode = JSON.parse(resCode.body);
    expect(bodyCode.data.some((t: any) => t.id === 'ft_team_cat_1')).toBe(true);

    // Lower case name substring
    const resLower = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=london',
    });
    const bodyLower = JSON.parse(resLower.body);
    expect(bodyLower.data.some((t: any) => t.id === 'ft_team_cat_1')).toBe(true);

    // Mixed case partial match
    const resMixed = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=mAdRiD',
    });
    const bodyMixed = JSON.parse(resMixed.body);
    expect(bodyMixed.data.some((t: any) => t.id === 'ft_team_cat_3')).toBe(true);
  });

  it('3. GET /api/v2/catalog/teams sanitizes wildcards and does NOT allow dumping the table with % or _', async () => {
    // Search strictly for '%' - should only match teams with a literal '%' in their name, NOT the whole table
    const resWildcardPercent = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=%',
    });
    expect(resWildcardPercent.statusCode).toBe(200);
    const bodyPercent = JSON.parse(resWildcardPercent.body);

    // Only '100% Football Club' contains literal '%'
    expect(bodyPercent.data.length).toBe(1);
    expect(bodyPercent.data[0].id).toBe('ft_team_cat_4');
    expect(bodyPercent.data[0].name).toBe('100% Football Club');

    // Search strictly for '_' - should only match literal '_' and NOT act as single-character wildcard matching everything
    const resWildcardUnderscore = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=_',
    });
    expect(resWildcardUnderscore.statusCode).toBe(200);
    const bodyUnderscore = JSON.parse(resWildcardUnderscore.body);
    // None of the seeded test teams have an underscore in name/code/shortName
    const matchedTestTeams = bodyUnderscore.data.filter((t: any) =>
      ['ft_team_cat_1', 'ft_team_cat_2', 'ft_team_cat_3', 'ft_team_cat_4'].includes(t.id)
    );
    expect(matchedTestTeams.length).toBe(0);
  });

  it('4. GET /api/v2/catalog/teams returns empty array for empty or whitespace query', async () => {
    const resEmpty = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=',
    });
    expect(resEmpty.statusCode).toBe(200);
    const bodyEmpty = JSON.parse(resEmpty.body);
    expect(bodyEmpty.data).toEqual([]);

    const resWhitespace = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=%20%20%20',
    });
    expect(resWhitespace.statusCode).toBe(200);
    const bodyWhitespace = JSON.parse(resWhitespace.body);
    expect(bodyWhitespace.data).toEqual([]);
  });

  it('5. GET /api/v2/catalog/teams respects and clamps the limit query parameter', async () => {
    const resLimit1 = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/teams?q=a&limit=1',
    });
    expect(resLimit1.statusCode).toBe(200);
    const bodyLimit1 = JSON.parse(resLimit1.body);
    expect(bodyLimit1.data.length).toBe(1);
  });

  // =========================================================================
  // Section 2: Competitions Search (/api/v2/catalog/competitions)
  // =========================================================================

  it('6. GET /api/v2/catalog/competitions returns matched competitions with canonical ID, name, and logo', async () => {
    const res = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=Premier',
    });

    expect(res.statusCode).toBe(200);
    const body = JSON.parse(res.body);

    expect(body.schemaVersion).toBe('2.0');
    expect(body.version).toBe(2);
    expect(Array.isArray(body.data)).toBe(true);
    expect(body.data.length).toBeGreaterThanOrEqual(1);

    const comp = body.data.find((c: any) => c.id === 'ft_comp_cat_1');
    expect(comp).toBeDefined();
    expect(comp.id).toBe('ft_comp_cat_1');
    expect(comp.id.startsWith('ft_comp_')).toBe(true);
    expect(comp.name).toBe('English Premier League');
    expect(comp.code).toBe('PL');
    expect(comp.logo).toBe('https://example.com/logos/pl.png');
    expect(comp.logoUrl).toBe('https://example.com/logos/pl.png');
  });

  it('7. GET /api/v2/catalog/competitions performs case-insensitive search across name and code', async () => {
    // Upper case code
    const resCode = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=UCL',
    });
    const bodyCode = JSON.parse(resCode.body);
    expect(bodyCode.data.some((c: any) => c.id === 'ft_comp_cat_2')).toBe(true);

    // Lower case name partial match
    const resLower = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=champions',
    });
    const bodyLower = JSON.parse(resLower.body);
    expect(bodyLower.data.some((c: any) => c.id === 'ft_comp_cat_2')).toBe(true);

    // Mixed case partial match
    const resMixed = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=lA%20lIgA',
    });
    const bodyMixed = JSON.parse(resMixed.body);
    expect(bodyMixed.data.some((c: any) => c.id === 'ft_comp_cat_3')).toBe(true);
  });

  it('8. GET /api/v2/catalog/competitions sanitizes wildcards and returns empty array on empty query', async () => {
    // % wildcard search does not dump all competitions
    const resWildcard = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=%',
    });
    expect(resWildcard.statusCode).toBe(200);
    const bodyWildcard = JSON.parse(resWildcard.body);
    // None of the competitions contain literal %
    const matchedComps = bodyWildcard.data.filter((c: any) =>
      ['ft_comp_cat_1', 'ft_comp_cat_2', 'ft_comp_cat_3'].includes(c.id)
    );
    expect(matchedComps.length).toBe(0);

    // Empty query returns empty array
    const resEmpty = await app.inject({
      method: 'GET',
      url: '/api/v2/catalog/competitions?q=',
    });
    expect(resEmpty.statusCode).toBe(200);
    const bodyEmpty = JSON.parse(resEmpty.body);
    expect(bodyEmpty.data).toEqual([]);
  });
});
