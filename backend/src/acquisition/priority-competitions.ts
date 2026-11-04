export interface PriorityCompetition {
  id: number;
  name: string;
  country: string;
  type: 'league' | 'cup';
  code: string;
}

export const CANONICAL_COMPETITION_MAP: Record<number, string> = {
  1: 'ft_comp_01M33DKNBZRHDX2EN9YQJPSV5Q',  // FIFA World Cup
  2: 'ft_comp_01M33DKNC97EMTT3FNZR1YTC9E',   // UEFA Champions League
  4: 'ft_comp_01M3Y7E15VJYGS76SM21GBR1YD',   // UEFA European Championship
  5: 'ft_comp_01M3Y7E15Z5X08Y7S7K0JPAGE6',   // UEFA Nations League
  6: 'ft_comp_01M33DKNCDV0T7DEEHHZ20DMN7',   // Africa Cup of Nations
  9: 'ft_comp_01M3Y7E1608TK80WDRTGYQHF2X',   // Copa America
  39: 'ft_comp_01M33DKNCCNEJAG93CDAGJSR4A',  // Premier League
  140: 'ft_comp_01M33DKNC8CKGMJ9Z7898AH8HA', // La Liga
  233: 'ft_comp_01M33DKNCABRRYR4K6D9DKTP40', // Egyptian Premier League
};

/**
 * Maps API-Football competition IDs to FotMob provider competition IDs.
 */
export const FOTMOB_COMPETITION_MAP: Record<number, number> = {
  39: 47,       // Premier League
  140: 87,      // La Liga
  135: 55,      // Serie A
  78: 54,       // Bundesliga
  61: 53,       // Ligue 1
  2: 42,        // UEFA Champions League
  3: 73,        // UEFA Europa League
  848: 10216,   // UEFA Conference League
  233: 519,     // Egyptian Premier League
  525: 525,     // AFC Champions League Elite
  1: 77,        // FIFA World Cup
  4: 50,        // European Championship
  5: 9806,      // UEFA Nations League
  6: 132,       // Africa Cup of Nations
  9: 44,        // Copa America
  40: 48,       // Championship
  45: 138,      // FA Cup
  48: 139,      // EFL Cup
  143: 137,     // Copa del Rey
  81: 209,      // DFB-Pokal
  137: 141,     // Coppa Italia
  66: 142,      // Coupe de France
  307: 536,     // Saudi Pro League
  253: 130,     // Major League Soccer
};

export const PRIORITY_COMPETITIONS: PriorityCompetition[] = [
  // European Top Flights & Cups
  { id: 39, name: 'Premier League', country: 'England', type: 'league', code: 'PL' },
  { id: 40, name: 'Championship', country: 'England', type: 'league', code: 'CHA' },
  { id: 45, name: 'FA Cup', country: 'England', type: 'cup', code: 'FAC' },
  { id: 48, name: 'EFL Cup', country: 'England', type: 'cup', code: 'EFL' },
  { id: 528, name: 'Community Shield', country: 'England', type: 'cup', code: 'CS' },
  { id: 140, name: 'La Liga', country: 'Spain', type: 'league', code: 'PD' },
  { id: 141, name: 'Segunda Division', country: 'Spain', type: 'league', code: 'SD' },
  { id: 143, name: 'Copa del Rey', country: 'Spain', type: 'cup', code: 'CDR' },
  { id: 556, name: 'Supercopa de España', country: 'Spain', type: 'cup', code: 'SCE' },
  { id: 135, name: 'Serie A', country: 'Italy', type: 'league', code: 'SA' },
  { id: 136, name: 'Serie B', country: 'Italy', type: 'league', code: 'SB' },
  { id: 137, name: 'Coppa Italia', country: 'Italy', type: 'cup', code: 'CIT' },
  { id: 547, name: 'Supercoppa Italiana', country: 'Italy', type: 'cup', code: 'SCI' },
  { id: 78, name: 'Bundesliga', country: 'Germany', type: 'league', code: 'BL1' },
  { id: 79, name: '2. Bundesliga', country: 'Germany', type: 'league', code: 'BL2' },
  { id: 81, name: 'DFB-Pokal', country: 'Germany', type: 'cup', code: 'DFB' },
  { id: 529, name: 'DFL-Supercup', country: 'Germany', type: 'cup', code: 'GSC' },
  { id: 61, name: 'Ligue 1', country: 'France', type: 'league', code: 'FL1' },
  { id: 62, name: 'Ligue 2', country: 'France', type: 'league', code: 'FL2' },
  { id: 66, name: 'Coupe de France', country: 'France', type: 'cup', code: 'CDF' },
  { id: 526, name: 'Trophée des Champions', country: 'France', type: 'cup', code: 'TDC' },
  { id: 88, name: 'Eredivisie', country: 'Netherlands', type: 'league', code: 'ERE' },
  { id: 90, name: 'KNVB Beker', country: 'Netherlands', type: 'cup', code: 'KNVB' },
  { id: 94, name: 'Primeira Liga', country: 'Portugal', type: 'league', code: 'PPL' },
  { id: 96, name: 'Taça de Portugal', country: 'Portugal', type: 'cup', code: 'TDP' },
  { id: 144, name: 'Belgian Pro League', country: 'Belgium', type: 'league', code: 'JPL' },
  { id: 147, name: 'Belgian Cup', country: 'Belgium', type: 'cup', code: 'BEC' },
  { id: 203, name: 'Süper Lig', country: 'Turkey', type: 'league', code: 'TSL' },
  { id: 206, name: 'Turkish Cup', country: 'Turkey', type: 'cup', code: 'TRC' },
  { id: 179, name: 'Scottish Premiership', country: 'Scotland', type: 'league', code: 'SP' },
  { id: 181, name: 'Scottish Cup', country: 'Scotland', type: 'cup', code: 'SC' },
  { id: 207, name: 'Swiss Super League', country: 'Switzerland', type: 'league', code: 'SSL' },
  { id: 218, name: 'Austrian Bundesliga', country: 'Austria', type: 'league', code: 'OBL' },
  { id: 119, name: 'Danish Superliga', country: 'Denmark', type: 'league', code: 'DSL' },
  { id: 103, name: 'Eliteserien', country: 'Norway', type: 'league', code: 'ELI' },
  { id: 113, name: 'Allsvenskan', country: 'Sweden', type: 'league', code: 'ALL' },
  { id: 197, name: 'Super League 1', country: 'Greece', type: 'league', code: 'GSL' },
  { id: 106, name: 'Ekstraklasa', country: 'Poland', type: 'league', code: 'EKS' },
  { id: 345, name: 'Czech First League', country: 'Czech Republic', type: 'league', code: 'CFL' },
  { id: 210, name: 'HNL', country: 'Croatia', type: 'league', code: 'HNL' },
  { id: 283, name: 'SuperLiga', country: 'Serbia', type: 'league', code: 'SRB' },
  { id: 332, name: 'Ukrainian Premier League', country: 'Ukraine', type: 'league', code: 'UPL' },

  // Middle East & Africa
  { id: 233, name: 'Egyptian Premier League', country: 'Egypt', type: 'league', code: 'EPL' },
  { id: 234, name: 'Egypt Cup', country: 'Egypt', type: 'cup', code: 'EGC' },
  { id: 546, name: 'Egyptian Super Cup', country: 'Egypt', type: 'cup', code: 'ESC' },
  { id: 307, name: 'Saudi Pro League', country: 'Saudi Arabia', type: 'league', code: 'SPL' },
  { id: 308, name: 'King\'s Cup', country: 'Saudi Arabia', type: 'cup', code: 'KIC' },
  { id: 541, name: 'Saudi Super Cup', country: 'Saudi Arabia', type: 'cup', code: 'SSC' },
  { id: 301, name: 'UAE Pro League', country: 'United Arab Emirates', type: 'league', code: 'UAE' },
  { id: 305, name: 'Qatar Stars League', country: 'Qatar', type: 'league', code: 'QSL' },
  { id: 200, name: 'Botola Pro', country: 'Morocco', type: 'league', code: 'BOT' },
  { id: 202, name: 'Ligue 1', country: 'Tunisia', type: 'league', code: 'TUN' },
  { id: 187, name: 'Ligue 1', country: 'Algeria', type: 'league', code: 'ALG' },
  { id: 288, name: 'Premier Soccer League', country: 'South Africa', type: 'league', code: 'PSL' },

  // Americas
  { id: 253, name: 'Major League Soccer', country: 'USA', type: 'league', code: 'MLS' },
  { id: 257, name: 'US Open Cup', country: 'USA', type: 'cup', code: 'USOC' },
  { id: 846, name: 'Leagues Cup', country: 'World', type: 'cup', code: 'LC' },
  { id: 262, name: 'Liga MX', country: 'Mexico', type: 'league', code: 'LMX' },
  { id: 71, name: 'Serie A', country: 'Brazil', type: 'league', code: 'BSA' },
  { id: 72, name: 'Serie B', country: 'Brazil', type: 'league', code: 'BSB' },
  { id: 73, name: 'Copa do Brasil', country: 'Brazil', type: 'cup', code: 'CDB' },
  { id: 128, name: 'Liga Profesional Argentina', country: 'Argentina', type: 'league', code: 'ARG' },
  { id: 130, name: 'Copa Argentina', country: 'Argentina', type: 'cup', code: 'CARG' },
  { id: 239, name: 'Categoría Primera A', country: 'Colombia', type: 'league', code: 'COL' },
  { id: 265, name: 'Primera División', country: 'Chile', type: 'league', code: 'CHI' },
  { id: 268, name: 'Primera División', country: 'Uruguay', type: 'league', code: 'URU' },

  // Asia & Oceania
  { id: 98, name: 'J1 League', country: 'Japan', type: 'league', code: 'J1' },
  { id: 292, name: 'K League 1', country: 'South Korea', type: 'league', code: 'K1' },
  { id: 188, name: 'A-League', country: 'Australia', type: 'league', code: 'AL' },

  // International & Continental Club Tournaments
  { id: 2, name: 'UEFA Champions League', country: 'World', type: 'cup', code: 'UCL' },
  { id: 3, name: 'UEFA Europa League', country: 'World', type: 'cup', code: 'UEL' },
  { id: 848, name: 'UEFA Europa Conference League', country: 'World', type: 'cup', code: 'UECL' },
  { id: 531, name: 'UEFA Super Cup', country: 'World', type: 'cup', code: 'USC' },
  { id: 12, name: 'CAF Champions League', country: 'World', type: 'cup', code: 'CAFCL' },
  { id: 20, name: 'CAF Confederation Cup', country: 'World', type: 'cup', code: 'CAFCC' },
  { id: 412, name: 'CAF Super Cup', country: 'World', type: 'cup', code: 'CAFSC' },
  { id: 17, name: 'AFC Champions League', country: 'World', type: 'cup', code: 'AFCCL' },
  { id: 849, name: 'AFC Champions League Two', country: 'World', type: 'cup', code: 'ACL2' },
  { id: 16, name: 'CONCACAF Champions Cup', country: 'World', type: 'cup', code: 'CCC' },
  { id: 13, name: 'Copa Libertadores', country: 'World', type: 'cup', code: 'LIB' },
  { id: 11, name: 'Copa Sudamericana', country: 'World', type: 'cup', code: 'SUD' },
  { id: 538, name: 'Recopa Sudamericana', country: 'World', type: 'cup', code: 'RSUD' },
  { id: 15, name: 'FIFA Club World Cup', country: 'World', type: 'cup', code: 'CWC' },

  // International National Team Tournaments
  { id: 1, name: 'FIFA World Cup', country: 'World', type: 'cup', code: 'WC' },
  { id: 4, name: 'UEFA European Championship', country: 'World', type: 'cup', code: 'EURO' },
  { id: 960, name: 'UEFA European Championship - Qualification', country: 'World', type: 'cup', code: 'EQ' },
  { id: 6, name: 'Africa Cup of Nations', country: 'World', type: 'cup', code: 'AFCON' },
  { id: 34, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 36, name: 'Africa Cup of Nations - Qualification', country: 'World', type: 'cup', code: 'AFCON_Q' },
  { id: 9, name: 'Copa America', country: 'World', type: 'cup', code: 'CA' },
  { id: 7, name: 'Asian Cup', country: 'World', type: 'cup', code: 'AC' },
  { id: 22, name: 'CONCACAF Gold Cup', country: 'World', type: 'cup', code: 'CGC' },
  { id: 5, name: 'UEFA Nations League', country: 'World', type: 'cup', code: 'UNL' },
  { id: 536, name: 'CONCACAF Nations League', country: 'World', type: 'cup', code: 'CNL' },
  { id: 32, name: 'World Cup - Qualification Europe', country: 'World', type: 'cup', code: 'WCQE' },
  { id: 29, name: 'World Cup - Qualification Africa', country: 'World', type: 'cup', code: 'WCQA' },
  { id: 30, name: 'World Cup - Qualification South America', country: 'World', type: 'cup', code: 'WCQSA' },
  { id: 31, name: 'World Cup - Qualification Asia', country: 'World', type: 'cup', code: 'WCQAS' },
  { id: 33, name: 'World Cup - Qualification CONCACAF', country: 'World', type: 'cup', code: 'WCQC' },
];

/**
 * Maps canonical competition slugs and common identifiers to FotMob provider league IDs.
 */
export const COMPETITION_SLUG_TO_FOTMOB_ID: Record<string, number> = {
  // English
  'ft_comp_eng_premier_league': 47,
  'ft_comp_premier_league': 47,
  'premier_league': 47,
  'ft_comp_eng_championship': 48,
  'ft_comp_championship': 48,
  'championship': 48,
  'ft_comp_eng_fa_cup': 138,
  'ft_comp_fa_cup': 138,
  'fa_cup': 138,
  'ft_comp_eng_efl_cup': 139,
  'ft_comp_efl_cup': 139,
  'efl_cup': 139,

  // Spanish
  'ft_comp_esp_laliga': 87,
  'ft_comp_laliga': 87,
  'laliga': 87,
  'la_liga': 87,
  'ft_comp_esp_copa_del_rey': 137,
  'ft_comp_copa_del_rey': 137,
  'copa_del_rey': 137,

  // Italian
  'ft_comp_ita_serie_a': 55,
  'ft_comp_serie_a': 55,
  'serie_a': 55,
  'ft_comp_ita_coppa_italia': 141,
  'ft_comp_coppa_italia': 141,
  'coppa_italia': 141,

  // German
  'ft_comp_ger_bundesliga': 54,
  'ft_comp_bundesliga': 54,
  'bundesliga': 54,
  'ft_comp_ger_dfb_pokal': 209,
  'ft_comp_dfb_pokal': 209,
  'dfb_pokal': 209,

  // French
  'ft_comp_fra_ligue_1': 53,
  'ft_comp_ligue_1': 53,
  'ligue_1': 53,
  'ft_comp_fra_coupe_de_france': 142,
  'ft_comp_coupe_de_france': 142,
  'coupe_de_france': 142,

  // European / Continental Club Tournaments
  'ft_comp_int_uefa_champions_league': 42,
  'ft_comp_uefa_champions_league': 42,
  'uefa_champions_league': 42,
  'uefa-champions-league': 42,
  'uefa champions league': 42,
  'champions_league': 42,
  'champions-league': 42,
  'champions league': 42,
  'ucl': 42,
  'ft_comp_int_uefa_europa_league': 73,
  'ft_comp_uefa_europa_league': 73,
  'uefa_europa_league': 73,
  'uefa-europa-league': 73,
  'uefa europa league': 73,
  'europa_league': 73,
  'europa-league': 73,
  'europa league': 73,
  'uel': 73,
  'ft_comp_int_uefa_conference_league': 10216,
  'ft_comp_uefa_conference_league': 10216,
  'uefa_conference_league': 10216,
  'uefa-conference-league': 10216,
  'uefa conference league': 10216,
  'uefa_europa_conference_league': 10216,
  'uefa-europa-conference-league': 10216,
  'uefa europa conference league': 10216,
  'europa_conference_league': 10216,
  'europa-conference-league': 10216,
  'europa conference league': 10216,
  'conference_league': 10216,
  'conference-league': 10216,
  'conference league': 10216,
  'uecl': 10216,
  'ft_comp_int_uefa_super_cup': 531,
  'ft_comp_int_fifa_club_world_cup': 15,

  // International National Team Tournaments
  'ft_comp_int_fifa_world_cup': 77,
  'ft_comp_world_cup': 77,
  'world_cup': 77,
  'ft_comp_int_uefa_european_championship': 50,
  'ft_comp_euro': 50,
  'euro': 50,
  'ft_comp_int_uefa_nations_league': 9806,
  'ft_comp_nations_league': 9806,
  'nations_league': 9806,
  'ft_comp_int_africa_cup_of_nations': 132,
  'ft_comp_afcon': 132,
  'afcon': 132,
  'ft_comp_int_copa_america': 44,
  'ft_comp_copa_america': 44,
  'copa_america': 44,

  // Middle East & Africa
  'ft_comp_egy_premier_league': 519,
  'ft_comp_egyptian_premier_league': 519,
  'egyptian_premier_league': 519,
  'ft_comp_afc_champions_league': 525,
  'afc_champions_league': 525,
  'afc-champions-league': 525,
  'ft_comp_ksa_saudi_pro_league': 536,
  'ft_comp_saudi_pro_league': 536,
  'saudi_pro_league': 536,
  'saudi-pro-league': 536,
  'saudi pro league': 536,
  'saudi_professional_league': 536,
  'saudi-professional-league': 536,
  'saudi professional league': 536,
  'spl': 536,

  // Americas
  'ft_comp_usa_mls': 130,
  'ft_comp_mls': 130,
  'mls': 130,
  'major_league_soccer': 130,
  'ft_comp_bra_serie_a': 268,
  'ft_comp_brasileirao': 268,
  'brasileirao': 268,

  // Other Top European Leagues
  'ft_comp_ned_eredivisie': 57,
  'ft_comp_eredivisie': 57,
  'eredivisie': 57,
  'ft_comp_por_liga_portugal': 61,
  'ft_comp_liga_portugal': 61,
  'liga_portugal': 61,
  'primeira_liga': 61,
  'ft_comp_tur_super_lig': 71,
  'ft_comp_super_lig': 71,
  'super_lig': 71,
};

