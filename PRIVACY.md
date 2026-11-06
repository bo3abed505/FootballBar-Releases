# Privacy Policy & Legal Disclaimers

**Last Updated:** November 2026  
**Application:** FootballBar (Version 1.3.1+)  
**Publisher:** FootballBar  

---

## 1. Zero Personal Data Collection Policy

FootballBar is designed from the ground up with a strict privacy-first architecture. 

- **No Personal Data Collected:** FootballBar does **not** collect, harvest, transmit, store, or sell any personal data, identifiers, accounts, email addresses, IP addresses, or device information.
- **No User Tracking or Analytics:** The application contains **zero** analytics SDKs, tracking pixels, behavioral beacons, or advertising networks.
- **No Account Required:** You do not need to register, create an account, or log in to use FootballBar.

---

## 2. Local Storage on Device

All configuration files, caches, and user preferences remain strictly local to your machine:

- **Storage Location:** User configuration is stored exclusively in your local Windows user profile at:
  ```
  %LOCALAPPDATA%\FootballTray\
  ```
- **Stored Data Includes:**
  - Followed football clubs and competitions.
  - Notification alert preferences, audio mode, and volume levels.
  - Ticker display preferences, carousel interval, and taskbar offsets.
  - Transient HTTP score caches and match center snapshots (temporary JSON files).
- **Data Deletion:** Uninstalling FootballBar or deleting the `%LOCALAPPDATA%\FootballTray` folder removes all local application data permanently.

---

## 3. Outbound Network Communications

FootballBar performs outbound HTTP/HTTPS requests solely to retrieve public football match data and software updates:

1. **Live Sports Telemetry:** Outbound requests are made to public sports data endpoints solely to retrieve public match schedules, live scoreboards, match minute events, tactical pitch lineups, tournament brackets, and league standings tables.
2. **Software Updates & Over-The-Air (OTA) Route Manifests:**
   - Standalone / portable editions check GitHub Releases (`https://github.com/bo3abed505/FootballBar-Releases`) for signed application updates.
   - Packaged Microsoft Store editions have external binary update checks disabled in compliance with Microsoft Store Policy 10.2.4; updates are distributed directly through the Microsoft Store.
   - Dynamic API route updates are fetched from GitHub Release manifest files to maintain resilient telemetry connectivity without requiring binary updates.

No personal user data, search history, or preference data is ever included in outbound HTTP requests.

---

## 4. Fair-Use & Trademark Disclaimer

- **Independent Software Utility:** FootballBar is an independent desktop application created for personal informational and entertainment purposes.
- **No Affiliation or Endorsement:** FootballBar is **not** affiliated with, associated with, sponsored by, authorized by, or endorsed by FIFA, UEFA, the Premier League, LaLiga, CAF, AFC, or any football club, league, federation, or sports governing body.
- **Intellectual Property:** All club names, competition names, logos, crests, and registered trademarks displayed within the application are the property of their respective copyright and trademark owners. Their display in FootballBar is solely for factual identification, informational reporting, and nominative fair use.

---

## 5. Security & Contact

For support, source verification, questions, or bug reports, please visit:
- **Official GitHub Releases & Repository:** [https://github.com/bo3abed505/FootballBar-Releases](https://github.com/bo3abed505/FootballBar-Releases)
- **Privacy Policy Document:** [https://github.com/bo3abed505/FootballBar-Releases/blob/main/PRIVACY.md](https://github.com/bo3abed505/FootballBar-Releases/blob/main/PRIVACY.md)

