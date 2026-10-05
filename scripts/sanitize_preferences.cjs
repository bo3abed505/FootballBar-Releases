const fs = require('fs');
const path = require('path');

const localAppData = process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || '', 'AppData', 'Local');
const prefsPath = path.join(localAppData, 'FootballTray', 'preferences.json');

if (!fs.existsSync(prefsPath)) {
  console.log(`[SANITIZE] Preferences file not found at: ${prefsPath}`);
  process.exit(0);
}

try {
  const raw = fs.readFileSync(prefsPath, 'utf8');
  const prefs = JSON.parse(raw);

  const isMock = (entity) => {
    if (!entity) return false;
    const name = typeof entity === 'string' ? entity : (entity.name || '');
    const id = typeof entity === 'string' ? entity : (entity.id || '');
    const code = typeof entity === 'object' && entity.code ? entity.code : '';
    return name.toLowerCase().includes('(dev mock)') ||
           name.toLowerCase().includes('mock') ||
           id.toLowerCase().includes('mock') ||
           code.toLowerCase().includes('mock');
  };

  let compRemoved = 0;
  let teamRemoved = 0;

  if (Array.isArray(prefs.followedCompetitions)) {
    const initial = prefs.followedCompetitions.length;
    prefs.followedCompetitions = prefs.followedCompetitions.filter(c => !isMock(c));
    compRemoved += initial - prefs.followedCompetitions.length;
  }

  if (Array.isArray(prefs.followedTeams)) {
    const initial = prefs.followedTeams.length;
    prefs.followedTeams = prefs.followedTeams.filter(t => !isMock(t));
    teamRemoved += initial - prefs.followedTeams.length;
  }

  if (Array.isArray(prefs.followedCompetitionIds)) {
    const initial = prefs.followedCompetitionIds.length;
    prefs.followedCompetitionIds = prefs.followedCompetitionIds.filter(id => !isMock(id));
    compRemoved += initial - prefs.followedCompetitionIds.length;
  }

  if (Array.isArray(prefs.followedTeamIds)) {
    const initial = prefs.followedTeamIds.length;
    prefs.followedTeamIds = prefs.followedTeamIds.filter(id => !isMock(id));
    teamRemoved += initial - prefs.followedTeamIds.length;
  }

  fs.writeFileSync(prefsPath, JSON.stringify(prefs, null, 2), 'utf8');
  console.log(`[SANITIZE] Successfully sanitized: ${prefsPath}`);
  console.log(`[SANITIZE] Purged ${compRemoved} mock competitions and ${teamRemoved} mock teams.`);
} catch (err) {
  console.error(`[SANITIZE:ERROR] Failed to sanitize preferences: ${err.message}`);
  process.exit(1);
}
