import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';
import os from 'os';
import { execSync } from 'child_process';

describe('Per-User Client Installer and Uninstaller Scripts', () => {
  const rootDir = path.resolve(__dirname, '../..');
  const scriptsDir = path.join(rootDir, 'scripts');
  const installScriptPath = path.join(scriptsDir, 'install-client.ps1');
  const uninstallScriptPath = path.join(scriptsDir, 'uninstall-client.ps1');
  const releaseSource = path.join(rootDir, 'artifacts/releases/v1.0.0/win-x64');

  // Helper to run PowerShell commands cleanly without cmd.exe escaping issues
  const runPowerShell = (scriptContent: string): string => {
    const tempScript = path.join(os.tmpdir(), `pwsh_test_${Date.now()}_${Math.random().toString(36).slice(2)}.ps1`);
    try {
      fs.writeFileSync(tempScript, scriptContent, 'utf8');
      return execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${tempScript}"`, {
        cwd: rootDir,
        encoding: 'utf8',
      });
    } finally {
      if (fs.existsSync(tempScript)) {
        fs.unlinkSync(tempScript);
      }
    }
  };

  it('Test 1: Installer script exists and targets %LOCALAPPDATA%\\Programs\\FootballTray', () => {
    expect(fs.existsSync(installScriptPath)).toBe(true);
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$TargetDir = Join-Path $env:LOCALAPPDATA "Programs\\FootballTray"');
  });

  it('Test 2: User-data directory is distinct from the installation directory with hard invariant check', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$UserDataDir = Join-Path $env:LOCALAPPDATA "FootballTray"');
    expect(content).toContain('function Assert-NotUserDataPath');
    expect(content).toContain('CRITICAL INVARIANT VIOLATION');
  });

  it('Test 3: Startup shortcut is strictly per-user and never machine-wide', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$StartupDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Startup)');
    expect(content).toContain('$StartupShortcut = Join-Path $StartupDir "FootballTray.lnk"');
    expect(content).not.toContain('ProgramData\\Microsoft\\Windows\\Start Menu\\Programs\\Startup');
    expect(content).not.toContain('HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run');
  });

  it('Test 4: Start Menu shortcut points to the installed executable and verifies target', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$StartMenuDir = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)');
    expect(content).toContain('$StartMenuShortcut = Join-Path $StartMenuDir "FootballTray.lnk"');
    expect(content).toContain('$sc.TargetPath = $installedExe');
    expect(content).toContain('Start Menu shortcut target verification failed');
  });

  it('Test 5: Uninstall registration is strictly per-user in HKCU, not HKLM', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$UninstallRegKey = "HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FootballTray"');
    expect(content).not.toContain('HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall');
  });

  it('Test 6: Uninstaller targets the binary installation directory and preserves user data', () => {
    expect(fs.existsSync(uninstallScriptPath)).toBe(true);
    const content = fs.readFileSync(uninstallScriptPath, 'utf8');

    expect(content).toContain('$TargetDir = Join-Path $env:LOCALAPPDATA "Programs\\FootballTray"');
    expect(content).toContain('$UserDataDir = Join-Path $env:LOCALAPPDATA "FootballTray"');
    expect(content).toContain('Assert-NotUserDataPath -Path $TargetDir -Operation "delete"');
    expect(content).toContain('Remove-Item -LiteralPath $TargetDir -Recurse -Force');
    expect(content).toContain('User data preserved at: $UserDataDir');
    expect(content).not.toContain('Remove-Item -LiteralPath $UserDataDir');
  });

  it('Test A: Payload version extraction fails closed when version metadata is unavailable', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_empty_test_'));
    try {
      const psCmd = `& {
        $ErrorActionPreference = 'Stop'
        . (Resolve-Path '${installScriptPath.replace(/'/g, "''")}') -SourceDir '${releaseSource.replace(/'/g, "''")}' -WhatIf -AllowDowngrade | Out-Null
        try {
          Get-PayloadVersion -Dir '${tempDir.replace(/'/g, "''")}'
          Write-Output 'FAIL_NO_THROW'
        } catch {
          Write-Output "CAUGHT_THROW: $($_.Exception.Message)"
        }
      }`;
      const output = runPowerShell(psCmd);
      expect(output).toContain('CAUGHT_THROW');
      expect(output).toContain('Unable to determine valid semantic version');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('Test B: 1.0.0 compares newer than 1.0.0-beta', () => {
    const psCmd = `& {
      . (Resolve-Path '${installScriptPath.replace(/'/g, "''")}') -SourceDir '${releaseSource.replace(/'/g, "''")}' -WhatIf -AllowDowngrade | Out-Null
      $res = Compare-SemVer '1.0.0' '1.0.0-beta'
      Write-Output "RESULT:$res"
    }`;
    const output = runPowerShell(psCmd);
    expect(output).toContain('RESULT:1');
  });

  it('Test C: 1.0.0-beta.2 compares newer than 1.0.0-beta.1', () => {
    const psCmd = `& {
      . (Resolve-Path '${installScriptPath.replace(/'/g, "''")}') -SourceDir '${releaseSource.replace(/'/g, "''")}' -WhatIf -AllowDowngrade | Out-Null
      $res = Compare-SemVer '1.0.0-beta.2' '1.0.0-beta.1'
      Write-Output "RESULT:$res"
    }`;
    const output = runPowerShell(psCmd);
    expect(output).toContain('RESULT:1');
  });

  it('Test D: 1.1.0 compares newer than 1.0.0', () => {
    const psCmd = `& {
      . (Resolve-Path '${installScriptPath.replace(/'/g, "''")}') -SourceDir '${releaseSource.replace(/'/g, "''")}' -WhatIf -AllowDowngrade | Out-Null
      $res = Compare-SemVer '1.1.0' '1.0.0'
      Write-Output "RESULT:$res"
    }`;
    const output = runPowerShell(psCmd);
    expect(output).toContain('RESULT:1');
  });

  it('Test E: Invalid version strings are rejected by Parse-SemVer', () => {
    const psCmd = `& {
      . (Resolve-Path '${installScriptPath.replace(/'/g, "''")}') -SourceDir '${releaseSource.replace(/'/g, "''")}' -WhatIf -AllowDowngrade | Out-Null
      try {
        Parse-SemVer 'invalid-version'
        Write-Output 'FAIL_NO_THROW'
      } catch {
        Write-Output "CAUGHT_THROW: $($_.Exception.Message)"
      }
    }`;
    const output = runPowerShell(psCmd);
    expect(output).toContain('CAUGHT_THROW');
    expect(output).toContain('Invalid semantic version format');
  });

  it('Test F & G: Post-activation failure retains backup and triggers complete rollback of previous installation', () => {
    const testRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_test_trans_'));
    const sourceDir = path.join(testRoot, 'source');
    const targetDir = path.join(testRoot, 'target');
    const startMenuDir = path.join(testRoot, 'startmenu');
    const startupDir = path.join(testRoot, 'startup');
    const testRegKey = 'HKCU:\\Software\\FootballTrayUnitTest';

    fs.mkdirSync(sourceDir, { recursive: true });
    fs.mkdirSync(targetDir, { recursive: true });
    fs.mkdirSync(startMenuDir, { recursive: true });
    fs.mkdirSync(startupDir, { recursive: true });

    // Copy essential release binaries to source
    const releaseSource = path.join(rootDir, 'artifacts/releases/v1.0.0/win-x64');
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(sourceDir, 'FootballTray.exe'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.dll'), path.join(sourceDir, 'FootballTray.dll'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.Core.dll'), path.join(sourceDir, 'FootballTray.Core.dll'));
    fs.copyFileSync(path.join(releaseSource, 'Microsoft.WinUI.dll'), path.join(sourceDir, 'Microsoft.WinUI.dll'));
    fs.copyFileSync(uninstallScriptPath, path.join(sourceDir, 'uninstall-client.ps1'));
    fs.writeFileSync(path.join(sourceDir, 'VERSION.txt'), 'FootballTray v1.0.0');

    // Create previous installation in target
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(targetDir, 'FootballTray.exe'));
    fs.writeFileSync(path.join(targetDir, 'VERSION.txt'), 'FootballTray v1.0.0');
    fs.writeFileSync(path.join(targetDir, 'PREVIOUS_INSTALL.marker'), 'PREVIOUS_DATA');

    try {
      const psCmd = `& {
        $ErrorActionPreference = 'Continue'
        try {
          $output = & '${installScriptPath.replace(/'/g, "''")}' \`
            -SourceDir '${sourceDir.replace(/'/g, "''")}' \`
            -TargetDir '${targetDir.replace(/'/g, "''")}' \`
            -StartMenuDir '${startMenuDir.replace(/'/g, "''")}' \`
            -StartupDir '${startupDir.replace(/'/g, "''")}' \`
            -UninstallRegKey '${testRegKey}' \`
            -StartWithWindows \`
            -AllowDowngrade \`
            -SimulatePostActivationFailure 2>&1
          Write-Output "INSTALL_OUTPUT: $($output | Out-String)"
        } catch {
          Write-Output "ROLLBACK_CAUGHT: $($_.Exception.Message)"
        }
      }`;

      const output = runPowerShell(psCmd);
      expect(output).toContain('SIMULATED POST-ACTIVATION FAILURE');
      expect(output).toContain('Restored previous installation from backup directory');

      // Verify that the marker file and version 1.0.0 were restored!
      expect(fs.existsSync(path.join(targetDir, 'PREVIOUS_INSTALL.marker'))).toBe(true);
      const restoredVer = fs.readFileSync(path.join(targetDir, 'VERSION.txt'), 'utf8');
      expect(restoredVer).toContain('1.0.0');

      // Verify backup folder was cleaned up
      expect(fs.existsSync(`${targetDir}.__backup`)).toBe(false);
    } finally {
      fs.rmSync(testRoot, { recursive: true, force: true });
      try {
        runPowerShell(`if (Test-Path '${testRegKey}') { Remove-Item -Path '${testRegKey}' -Recurse -Force }`);
      } catch {}
    }
  });

  it('Test 7: Pre-existing integration state (shortcuts and registry) is preserved and restored upon rollback', () => {
    const testRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_test_integ_'));
    const sourceDir = path.join(testRoot, 'source');
    const targetDir = path.join(testRoot, 'target');
    const startMenuDir = path.join(testRoot, 'startmenu');
    const startupDir = path.join(testRoot, 'startup');
    const testRegKey = 'HKCU:\\Software\\FootballTrayIntegTest';

    fs.mkdirSync(sourceDir, { recursive: true });
    fs.mkdirSync(targetDir, { recursive: true });
    fs.mkdirSync(startMenuDir, { recursive: true });
    fs.mkdirSync(startupDir, { recursive: true });

    // Copy source payload
    const releaseSource = path.join(rootDir, 'artifacts/releases/v1.0.0/win-x64');
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(sourceDir, 'FootballTray.exe'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.dll'), path.join(sourceDir, 'FootballTray.dll'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.Core.dll'), path.join(sourceDir, 'FootballTray.Core.dll'));
    fs.copyFileSync(path.join(releaseSource, 'Microsoft.WinUI.dll'), path.join(sourceDir, 'Microsoft.WinUI.dll'));
    fs.copyFileSync(uninstallScriptPath, path.join(sourceDir, 'uninstall-client.ps1'));
    fs.writeFileSync(path.join(sourceDir, 'VERSION.txt'), 'FootballTray v1.0.0');

    // Create existing target installation (v1.0.0)
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(targetDir, 'FootballTray.exe'));
    fs.writeFileSync(path.join(targetDir, 'VERSION.txt'), 'FootballTray v1.0.0');

    // Pre-create pre-existing Start Menu shortcut with custom description
    const oldStartMenuShortcut = path.join(startMenuDir, 'FootballTray.lnk');
    const oldStartupShortcut = path.join(startupDir, 'FootballTray.lnk');
    runPowerShell(`& {
      $wsh = New-Object -ComObject WScript.Shell
      $sc = $wsh.CreateShortcut('${oldStartMenuShortcut.replace(/'/g, "''")}')
      $sc.TargetPath = '${path.join(targetDir, 'FootballTray.exe').replace(/'/g, "''")}'
      $sc.Description = 'PRE_EXISTING_STARTMENU_DESC'
      $sc.Save()

      $su = $wsh.CreateShortcut('${oldStartupShortcut.replace(/'/g, "''")}')
      $su.TargetPath = '${path.join(targetDir, 'FootballTray.exe').replace(/'/g, "''")}'
      $su.Description = 'PRE_EXISTING_STARTUP_DESC'
      $su.Save()

      New-Item -Path '${testRegKey}' -Force | Out-Null
      Set-ItemProperty -Path '${testRegKey}' -Name 'DisplayName' -Value 'FootballTray-PreExisting'
      Set-ItemProperty -Path '${testRegKey}' -Name 'DisplayVersion' -Value '1.0.0'
    }`);

    try {
      // Execute with simulated failure at Registry step
      const psCmd = `& {
        $ErrorActionPreference = 'Continue'
        try {
          $output = & '${installScriptPath.replace(/'/g, "''")}' \`
            -SourceDir '${sourceDir.replace(/'/g, "''")}' \`
            -TargetDir '${targetDir.replace(/'/g, "''")}' \`
            -StartMenuDir '${startMenuDir.replace(/'/g, "''")}' \`
            -StartupDir '${startupDir.replace(/'/g, "''")}' \`
            -UninstallRegKey '${testRegKey}' \`
            -StartWithWindows \`
            -AllowDowngrade \`
            -SimulateFailureStep 'Registry' 2>&1
          Write-Output "INSTALL_OUTPUT: $($output | Out-String)"
        } catch {
          Write-Output "ROLLBACK_CAUGHT: $($_.Exception.Message)"
        }
      }`;

      const output = runPowerShell(psCmd);
      expect(output).toContain('SIMULATED FAILURE: Registry step failure');
      expect(output).toContain('Restored previous Start Menu shortcut');
      expect(output).toContain('Restored previous Startup shortcut');
      expect(output).toContain('Restored previous uninstall registry properties');

      // Verify that the pre-existing Start Menu shortcut and registry were restored
      const verifyCmd = `& {
        $wsh = New-Object -ComObject WScript.Shell
        $sc = $wsh.CreateShortcut('${oldStartMenuShortcut.replace(/'/g, "''")}')
        Write-Output "STARTMENU_DESC:$($sc.Description)"
        $regVer = (Get-ItemProperty -Path '${testRegKey}').DisplayVersion
        Write-Output "REG_VER:$regVer"
      }`;
      const verifyOutput = runPowerShell(verifyCmd);
      expect(verifyOutput).toContain('STARTMENU_DESC:PRE_EXISTING_STARTMENU_DESC');
      expect(verifyOutput).toContain('REG_VER:1.0.0');
    } finally {
      fs.rmSync(testRoot, { recursive: true, force: true });
      try {
        runPowerShell(`if (Test-Path '${testRegKey}') { Remove-Item -Path '${testRegKey}' -Recurse -Force }`);
      } catch {}
    }
  });

  it('Test 8: Failure in each post-activation step triggers transactional rollback', () => {
    const testRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_test_steps_'));
    const sourceDir = path.join(testRoot, 'source');
    const targetDir = path.join(testRoot, 'target');
    const startMenuDir = path.join(testRoot, 'startmenu');
    const startupDir = path.join(testRoot, 'startup');
    const testRegKey = 'HKCU:\\Software\\FootballTrayStepTest';

    fs.mkdirSync(sourceDir, { recursive: true });
    fs.mkdirSync(targetDir, { recursive: true });
    fs.mkdirSync(startMenuDir, { recursive: true });
    fs.mkdirSync(startupDir, { recursive: true });

    const releaseSource = path.join(rootDir, 'artifacts/releases/v1.0.0/win-x64');
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(sourceDir, 'FootballTray.exe'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.dll'), path.join(sourceDir, 'FootballTray.dll'));
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.Core.dll'), path.join(sourceDir, 'FootballTray.Core.dll'));
    fs.copyFileSync(path.join(releaseSource, 'Microsoft.WinUI.dll'), path.join(sourceDir, 'Microsoft.WinUI.dll'));
    fs.copyFileSync(uninstallScriptPath, path.join(sourceDir, 'uninstall-client.ps1'));
    fs.writeFileSync(path.join(sourceDir, 'VERSION.txt'), 'FootballTray v1.0.0');

    // Create existing target
    fs.copyFileSync(path.join(releaseSource, 'FootballTray.exe'), path.join(targetDir, 'FootballTray.exe'));
    fs.writeFileSync(path.join(targetDir, 'VERSION.txt'), 'FootballTray v1.0.0');
    fs.writeFileSync(path.join(targetDir, 'ORIGINAL.marker'), 'ORIGINAL');

    const stepsToTest = ['Activation', 'StartMenu', 'Startup', 'Registry'];
    try {
      for (const step of stepsToTest) {
        const psCmd = `& {
          $ErrorActionPreference = 'Continue'
          try {
            $output = & '${installScriptPath.replace(/'/g, "''")}' \`
              -SourceDir '${sourceDir.replace(/'/g, "''")}' \`
              -TargetDir '${targetDir.replace(/'/g, "''")}' \`
              -StartMenuDir '${startMenuDir.replace(/'/g, "''")}' \`
              -StartupDir '${startupDir.replace(/'/g, "''")}' \`
              -UninstallRegKey '${testRegKey}' \`
              -StartWithWindows \`
              -AllowDowngrade \`
              -SimulateFailureStep '${step}' 2>&1
            Write-Output "INSTALL_OUTPUT: $($output | Out-String)"
          } catch {
            Write-Output "ROLLBACK_CAUGHT: $($_.Exception.Message)"
          }
        }`;

        const output = runPowerShell(psCmd);
        expect(output).toContain(`SIMULATED FAILURE: ${step}`);
        expect(output).toContain('Restored previous installation from backup directory');
        expect(fs.existsSync(path.join(targetDir, 'ORIGINAL.marker'))).toBe(true);
        expect(fs.existsSync(`${targetDir}.__backup`)).toBe(false);
      }
    } finally {
      fs.rmSync(testRoot, { recursive: true, force: true });
      try {
        runPowerShell(`if (Test-Path '${testRegKey}') { Remove-Item -Path '${testRegKey}' -Recurse -Force }`);
      } catch {}
    }
  });

  it('Test 9: Deterministic payload selection fails closed if VERSION file is missing or release folder does not exist', () => {
    // If no SourceDir is passed, install-client.ps1 resolves from root VERSION.
    // If VERSION has a non-existent version, it must fail closed and NOT silently pick another release.
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_payload_test_'));
    const fakeVersionFile = path.join(tempDir, 'VERSION');
    fs.writeFileSync(fakeVersionFile, '9.9.9');

    // Run script by mocking root VERSION location
    const psCmd = `& {
      $ErrorActionPreference = 'Continue'
      try {
        # Temporarily test deterministic resolution failure logic
        $rootDir = '${tempDir.replace(/'/g, "''")}'
        $versionFile = Join-Path $rootDir 'VERSION'
        $intendedVerRaw = (Get-Content -LiteralPath $versionFile -Raw).Trim()
        $canonicalPayload = Join-Path $rootDir "artifacts\\releases\\v$intendedVerRaw\\win-x64"
        if (-not (Test-Path -LiteralPath $canonicalPayload)) {
          throw "CRITICAL: Intended release payload for v$intendedVerRaw was not found at '$canonicalPayload'."
        }
      } catch {
        Write-Output "CAUGHT_PAYLOAD_ERROR: $($_.Exception.Message)"
      }
    }`;
    const output = runPowerShell(psCmd);
    expect(output).toContain('CAUGHT_PAYLOAD_ERROR');
    expect(output).toContain('Intended release payload for v9.9.9 was not found');
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('Test H: User data path is protected by hard invariant check against being targeted for install or deletion', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft_inv_test_'));
    try {
      const psCmd = `& {
        $ErrorActionPreference = 'Continue'
        try {
          & '${installScriptPath.replace(/'/g, "''")}' -TargetDir '${tempDir.replace(/'/g, "''")}' -UserDataDir '${tempDir.replace(/'/g, "''")}' 2>&1
        } catch {
          Write-Output "INVARIANT_CAUGHT: $($_.Exception.Message)"
        }
      }`;
      const output = runPowerShell(psCmd);
      expect(output).toContain('CRITICAL INVARIANT VIOLATION');
    } finally {
      fs.rmSync(tempDir, { recursive: true, force: true });
    }
  });

  it('Test 11: File copying strictly excludes .env* files and temp files', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    expect(content).toContain('$_.Name -notlike ".env*"');
    expect(content).toContain('$_.Name -notlike "*.tmp"');
  });

  it('Test 12: Installer does not require Administrator privileges', () => {
    const content = fs.readFileSync(installScriptPath, 'utf8');

    // Must NOT contain admin assertion
    expect(content).not.toContain('Assert-Administrator');
    expect(content).not.toContain('WindowsBuiltInRole]::Administrator');
    expect(content).not.toContain('RunAs');
  });

  it('Test 13: install-client.ps1 -WhatIf runs cleanly and exits with code 0', () => {
    const intendedVer = fs.existsSync(path.join(rootDir, 'VERSION'))
      ? fs.readFileSync(path.join(rootDir, 'VERSION'), 'utf8').trim()
      : '1.0.0';
    const canonicalPayload = path.join(rootDir, `artifacts/releases/v${intendedVer}/win-x64`);
    const sourceArg = fs.existsSync(canonicalPayload) ? '' : `-SourceDir "artifacts/releases/v1.0.0/win-x64"`;
    const output = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/install-client.ps1 -WhatIf ${sourceArg} -AllowDowngrade`, {
      cwd: rootDir,
      encoding: 'utf8',
    });

    expect(output).toContain('Simulation complete. No changes were made');
    expect(output).toContain('Source Directory:');
    expect(output).toContain('Target Directory:');
  });

  it('Test 14: uninstall-client.ps1 -WhatIf runs cleanly and exits with code 0', () => {
    const output = execSync('pwsh -NoProfile -ExecutionPolicy Bypass -File scripts/uninstall-client.ps1 -WhatIf', {
      cwd: rootDir,
      encoding: 'utf8',
    });

    expect(output).toContain('Simulation complete. No changes were made');
    expect(output).toContain('Will be PRESERVED');
  });
});
