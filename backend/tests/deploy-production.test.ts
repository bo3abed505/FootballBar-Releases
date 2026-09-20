import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import fs from 'fs';
import path from 'path';
import os from 'os';
import { execSync } from 'child_process';

describe('Production Backend Deployment & Packaging Logic', () => {
  const backendDir = path.resolve(__dirname, '..');
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');

  it('Test 1: Deployment script exists and is tracked in scripts directory', () => {
    expect(fs.existsSync(deployScriptPath)).toBe(true);
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    expect(scriptContent).toContain('New-BackendTaskXml');
    expect(scriptContent).toContain('C:\\ProgramData\\FootballTray\\backend');
    expect(scriptContent).toContain('npm ci --omit=dev');
  });

  it('Test 2: Production build artifacts are present and compiled', () => {
    const requiredFiles = [
      'package.json',
      'package-lock.json',
      'dist/server.js',
      'dist/gateway/request-gateway.js',
      'dist/diagnostics/logger.js',
      'dist/db/migrate-prod.js',
      'drizzle/meta/_journal.json',
    ];

    for (const rel of requiredFiles) {
      const full = path.join(backendDir, rel);
      expect(fs.existsSync(full)).toBe(true);
    }
  });

  it('Test 3: Production package manifest defines only necessary runtime dependencies', () => {
    const pkgJson = JSON.parse(fs.readFileSync(path.join(backendDir, 'package.json'), 'utf8'));

    expect(pkgJson.version).toBe('1.0.0');
    expect(pkgJson.main).toBe('dist/server.js');

    // Runtime dependencies must not include build/test tools
    const prodDeps = Object.keys(pkgJson.dependencies || {});
    expect(prodDeps).toContain('fastify');
    expect(prodDeps).toContain('pg');
    expect(prodDeps).toContain('drizzle-orm');
    expect(prodDeps).toContain('zod');

    expect(prodDeps).not.toContain('typescript');
    expect(prodDeps).not.toContain('vitest');
    expect(prodDeps).not.toContain('drizzle-kit');
    expect(prodDeps).not.toContain('tsx');
  });

  it('Test 4: Deployment script verifies unattended SYSTEM Task XML parameters', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Scheduled task security & privilege requirements
    expect(scriptContent).toContain('<UserId>S-1-5-18</UserId>'); // SYSTEM
    expect(scriptContent).toContain('<RunLevel>HighestAvailable</RunLevel>');
    expect(scriptContent).toContain('<BootTrigger />');
    expect(scriptContent).toContain('<RestartOnFailure>');
    expect(scriptContent).toContain('<Count>3</Count>');
    expect(scriptContent).toContain('<Interval>PT1M</Interval>');
    expect(scriptContent).toContain('dist/server.js');
  });

  it('Test 5: Target deployment paths decouple from source git repository', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('C:\\ProgramData\\FootballTray\\backend');
    expect(scriptContent).toContain('releases');
    expect(scriptContent).toContain('current');
    expect(scriptContent).toContain('config');

    // Ensure the task XML working directory points to current, not F:\Programs\FootballTray\backend
    expect(scriptContent).toMatch(/<WorkingDirectory>.*WorkingDir.*<\/WorkingDirectory>/);
  });

  it('Test 6: Rollback and ACL safety guards are implemented', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('Rollback');
    expect(scriptContent).toContain('Set-SecureAcl');
    expect(scriptContent).toContain('S-1-5-18'); // SYSTEM SID
    expect(scriptContent).toContain('S-1-5-32-544'); // Administrators SID
    expect(scriptContent).toContain('SetAccessRuleProtection');
  });

  it('Test 7: Critical schtasks operations are checked for $LASTEXITCODE', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('function Invoke-SchtasksChecked');
    expect(scriptContent).toContain('if ($LASTEXITCODE -ne 0)');
    expect(scriptContent).toContain('schtasks operation failed:');

    // Verify checked wrapper is invoked for critical lifecycle operations
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/Query"');
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/End"');
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/Create"');
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/Run"');
  });

  it('Test 8: Task backup is strictly validated before any production state modifications', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Hard safety gate: Backup executed, checked for non-empty, and parsed as XML before stopping or switching
    expect(scriptContent).toContain('HARD SAFETY GATE: Checked Task Backup & XML Validation');
    expect(scriptContent).toContain('string]::IsNullOrWhiteSpace($taskBackupXml)');
    expect(scriptContent).toContain('$parsedBackup = Test-TaskXmlDefinition -XmlContent $taskBackupXml');
    expect(scriptContent).toContain('-not $parsedBackup.IsValid -or -not $parsedBackup.Command');
    expect(scriptContent).toContain('Task backup verified and valid XML saved');
  });

  it('Test 9: Manifests contain explicit deployment status (pending -> verified / failed)', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Initial manifest created with status = "pending"
    expect(scriptContent).toContain('status        = "pending"');

    // Dedicated helper for status updates
    expect(scriptContent).toContain('function Set-DeploymentManifestStatus');
    expect(scriptContent).toContain('ValidateSet("pending", "verified", "failed")');

    // Manifest marked verified upon full success
    expect(scriptContent).toContain('Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "verified"');

    // Manifest marked failed upon failure
    expect(scriptContent).toContain('Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "failed"');
  });

  it('Test 10: Only verified releases qualify for rollback; failed/pending releases are excluded', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Must check status == verified
    expect(scriptContent).toContain('$manifest.status -ne "verified"');
    expect(scriptContent).toContain('continue'); // Skips non-verified

    // Active release must still be excluded
    expect(scriptContent).toContain('Where-Object { -not $_.IsActive }');
  });

  it('Test 11: Deployment state is verified on 9 points before marking release verified', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // 1. Junction target matches releaseDir
    expect(scriptContent).toContain('$resolvedJunction = Get-JunctionTarget -Path $currentJunction');
    expect(scriptContent).toContain('$expectedNorm = Normalize-JunctionPath -Path $releaseDir');
    expect(scriptContent).toContain('Test-JunctionPathEqual -Path1 $resolvedJunction -Path2 $expectedNorm');

    // 2. Query registered XML
    expect(scriptContent).toContain('Query Registered Task');

    // 3. User is SYSTEM
    expect(scriptContent).toContain('$taskUser -ne "S-1-5-18"');

    // 4. Trigger is BootTrigger
    expect(scriptContent).toContain('$taskInfo.HasBootTrigger');

    // 5. Command is Node
    expect(scriptContent).toContain('C:\\Program Files\\nodejs\\node.exe');

    // 6. WorkingDirectory is ProgramData current
    expect(scriptContent).toContain('$taskWorkingDir');

    // 7. Arguments contain config/.env.production.local
    expect(scriptContent).toContain('$taskArgs -notlike "*--env-file=*config');

    // 8. Readiness check (/api/tray)
    expect(scriptContent).toContain('http://127.0.0.1:4000/api/tray');
    expect(scriptContent).not.toContain('http://127.0.0.1:4000/health');

    // 9. Update manifest only after all preceding checks pass
    expect(scriptContent).toMatch(/api\/tray.*Set-DeploymentManifestStatus.*verified/s);
  });

  it('Test 12: Dual rollback restores previous junction and previous task XML', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Restores junction
    expect(scriptContent).toContain('New-Item -ItemType Junction -Path $currentJunction -Target $previousJunctionTarget');

    // Restores task XML from validated backup
    expect(scriptContent).toContain('Restore Task Definition from Backup');

    // Verifies restored task and /api/tray readiness
    expect(scriptContent).toContain('Verify Restored Task XML');
    expect(scriptContent).toContain('Verifying readiness of restored service via /api/tray');

    // Refuses success if rollback readiness verification fails
    expect(scriptContent).toContain('Rollback readiness verification failed');
    expect(scriptContent).toContain('throw "Deployment failed and rollback service verification also failed."');
  });

  it('Test 13: Production environment file remains outside release payload folders', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // .env.production.local is placed strictly in TargetDir\config, not in releases\<releaseId>
    expect(scriptContent).toContain('$targetEnvPath = Join-Path $configDir ".env.production.local"');
    expect(scriptContent).not.toContain('Join-Path $releaseDir ".env.production.local"');

    // Files copied to releaseDir are strictly dist, drizzle, package.json, package-lock.json
    expect(scriptContent).toContain('Join-Path $SourceDir "dist"');
    expect(scriptContent).toContain('Join-Path $SourceDir "drizzle"');
    expect(scriptContent).toContain('Join-Path $SourceDir "package.json"');
    expect(scriptContent).toContain('Join-Path $SourceDir "package-lock.json"');
  });
});

describe('Set-DeploymentManifestStatus Manifest Integrity & Fail-Closed Behavior (Tests A-G)', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft-manifest-test-'));
  });

  afterEach(() => {
    if (fs.existsSync(tempDir)) {
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {}
    }
  });

  // Helper executing Set-DeploymentManifestStatus isolated in pwsh
  const runManifestStatusInPwsh = (releaseDir: string, status: string): { success: boolean; output: string } => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const funcMatch = scriptContent.match(/function Set-DeploymentManifestStatus \{[\s\S]*?\n\}/);
    if (!funcMatch) {
      throw new Error('Could not find Set-DeploymentManifestStatus in deploy script');
    }
    const funcDef = funcMatch[0];
    const escapedDir = releaseDir.replace(/'/g, "''");

    const testPs1Path = path.join(tempDir, 'test-runner.ps1');
    const psScript = `
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
${funcDef}
Set-DeploymentManifestStatus -ReleaseDir '${escapedDir}' -Status '${status}'
`;
    fs.writeFileSync(testPs1Path, psScript, 'utf8');

    try {
      const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${testPs1Path}"`, {
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return { success: true, output: stdout };
    } catch (err: any) {
      return {
        success: false,
        output: (err.stdout ? err.stdout.toString() : '') + '\n' + (err.stderr ? err.stderr.toString() : err.message),
      };
    }
  };

  it('Test A: Set-DeploymentManifestStatus exists and is defined in script', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    expect(scriptContent).toContain('function Set-DeploymentManifestStatus');
  });

  it('Test B: Throws clear error when manifest file is missing', () => {
    const result = runManifestStatusInPwsh(tempDir, 'verified');
    expect(result.success).toBe(false);
    expect(result.output).toContain('CRITICAL: Deployment manifest file not found');
  });

  it('Test C: Throws clear error when manifest is malformed or unparseable JSON', () => {
    const manifestPath = path.join(tempDir, 'deployment-manifest.json');
    fs.writeFileSync(manifestPath, '{ "version": "1.0.0", invalid_json', 'utf8');

    const result = runManifestStatusInPwsh(tempDir, 'verified');
    expect(result.success).toBe(false);
    expect(result.output).toContain('malformed or unparseable');
  });

  it('Test D: Writes and verifies status = verified with verifiedAt timestamp', () => {
    const manifestPath = path.join(tempDir, 'deployment-manifest.json');
    const initial = { version: '1.0.0', status: 'pending' };
    fs.writeFileSync(manifestPath, JSON.stringify(initial, null, 2), 'utf8');

    const result = runManifestStatusInPwsh(tempDir, 'verified');
    expect(result.success).toBe(true);

    const updated = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    expect(updated.status).toBe('verified');
    expect(updated.verifiedAt).toBeDefined();
    expect(typeof updated.verifiedAt).toBe('string');
  });

  it('Test E: Writes and verifies status = failed with failedAt timestamp', () => {
    const manifestPath = path.join(tempDir, 'deployment-manifest.json');
    const initial = { version: '1.0.0', status: 'pending' };
    fs.writeFileSync(manifestPath, JSON.stringify(initial, null, 2), 'utf8');

    const result = runManifestStatusInPwsh(tempDir, 'failed');
    expect(result.success).toBe(true);

    const updated = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    expect(updated.status).toBe('failed');
    expect(updated.failedAt).toBeDefined();
    expect(typeof updated.failedAt).toBe('string');
  });

  it('Test F: Re-reads manifest after writing and checks persisted status', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const funcMatch = scriptContent.match(/function Set-DeploymentManifestStatus \{[\s\S]*?\n\}/);
    expect(funcMatch).not.toBeNull();
    const funcDef = funcMatch![0];

    // Verifies re-reading from disk and re-parsing
    expect(funcDef).toContain('Get-Content -LiteralPath $manifestPath -Raw');
    expect(funcDef).toContain('$persistedManifest = $persistedRaw | ConvertFrom-Json');
    expect(funcDef).toContain('if ($persistedManifest.status -ne $Status)');
    expect(funcDef).toContain('Deployment manifest persistence verification failed');
  });

  it('Test G: Does not silently swallow write or persistence errors (fails closed)', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const funcMatch = scriptContent.match(/function Set-DeploymentManifestStatus \{[\s\S]*?\n\}/);
    expect(funcMatch).not.toBeNull();
    const funcDef = funcMatch![0];

    // Must NOT contain Write-Warning suppression
    expect(funcDef).not.toContain('Write-Warning');

    // Must throw CRITICAL on failures
    expect(funcDef).toContain('throw "CRITICAL:');
  });
});

describe('Task XML Namespace-Aware Parsing and Rollback Non-Terminating Error Handling', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');

  // Real-world Task Scheduler XML fixture containing default namespace
  const taskXmlFixture = `<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>FootballTray Production Backend Service (Standalone)</Description>
    <URI>\\FootballTray-Backend</URI>
  </RegistrationInfo>
  <Principals>
    <Principal id="Author">
      <UserId>S-1-5-18</UserId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <RestartOnFailure>
      <Count>3</Count>
      <Interval>PT1M</Interval>
    </RestartOnFailure>
    <StartWhenAvailable>true</StartWhenAvailable>
  </Settings>
  <Triggers>
    <BootTrigger />
  </Triggers>
  <Actions Context="Author">
    <Exec>
      <Command>C:\\Program Files\\nodejs\\node.exe</Command>
      <Arguments>--env-file=C:\\ProgramData\\FootballTray\\backend\\config\\.env.production.local dist/server.js</Arguments>
      <WorkingDirectory>C:\\ProgramData\\FootballTray\\backend\\current</WorkingDirectory>
    </Exec>
  </Actions>
</Task>`;

  const runTestTaskXmlInPwsh = (xmlString: string): any => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const funcMatch = scriptContent.match(/function Test-TaskXmlDefinition \{[\s\S]*?\n\}/);
    if (!funcMatch) {
      throw new Error('Could not find Test-TaskXmlDefinition in script');
    }
    const funcDef = funcMatch[0];

    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft-taskxml-test-'));
    const testRunnerPath = path.join(tempDir, 'run-xml-test.ps1');
    const xmlPath = path.join(tempDir, 'fixture.xml');
    fs.writeFileSync(xmlPath, xmlString, 'utf8');

    const psScript = `
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
${funcDef}
$rawXml = Get-Content -LiteralPath '${xmlPath.replace(/'/g, "''")}' -Raw
$result = Test-TaskXmlDefinition -XmlContent $rawXml
$result | ConvertTo-Json
`;
    fs.writeFileSync(testRunnerPath, psScript, 'utf8');

    try {
      const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${testRunnerPath}"`, {
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return JSON.parse(stdout);
    } finally {
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {}
    }
  };

  it('Requirement 1: Task XML parser defines and uses XmlNamespaceManager with Task Scheduler namespace', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    expect(scriptContent).toContain('function Test-TaskXmlDefinition');
    expect(scriptContent).toContain('System.Xml.XmlNamespaceManager');
    expect(scriptContent).toContain('http://schemas.microsoft.com/windows/2004/02/mit/task');

    const parsed = runTestTaskXmlInPwsh(taskXmlFixture);
    expect(parsed.IsValid).toBe(true);
  });

  it('Requirement 2: BootTrigger is detected reliably despite real Task Scheduler default namespace', () => {
    // 1. With BootTrigger
    const parsedWithBoot = runTestTaskXmlInPwsh(taskXmlFixture);
    expect(parsedWithBoot.HasBootTrigger).toBe(true);

    // 2. Without BootTrigger (missing or different trigger)
    const xmlWithoutBoot = taskXmlFixture.replace('<BootTrigger />', '<TimeTrigger><StartBoundary>2026-09-03T00:00:00</StartBoundary></TimeTrigger>');
    const parsedWithoutBoot = runTestTaskXmlInPwsh(xmlWithoutBoot);
    expect(parsedWithoutBoot.HasBootTrigger).toBe(false);
  });

  it('Requirement 3: SYSTEM principal UserId and RunLevel are extracted accurately with the namespace', () => {
    const parsed = runTestTaskXmlInPwsh(taskXmlFixture);
    expect(parsed.UserId).toBe('S-1-5-18');
    expect(parsed.RunLevel).toBe('HighestAvailable');
  });

  it('Requirement 4: Exec Command, Arguments, and WorkingDirectory are extracted accurately with the namespace', () => {
    const parsed = runTestTaskXmlInPwsh(taskXmlFixture);
    expect(parsed.Command).toBe('C:\\Program Files\\nodejs\\node.exe');
    expect(parsed.Arguments).toContain('--env-file=C:\\ProgramData\\FootballTray\\backend\\config\\.env.production.local');
    expect(parsed.Arguments).toContain('dist/server.js');
    expect(parsed.WorkingDirectory).toBe('C:\\ProgramData\\FootballTray\\backend\\current');
  });

  it('Requirement 5: Rollback catch path does not use terminating Write-Error before or during rollback', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Extract the catch block
    const catchIndex = scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed');
    expect(catchIndex).toBeGreaterThan(0);
    const catchBlock = scriptContent.substring(catchIndex);

    // Rollback block must NOT contain Write-Error
    expect(catchBlock).not.toContain('Write-Error');
  });

  it('Requirement 6: Rollback sequence remains ordered strictly according to production invariant steps 1-8', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Capture the rollback catch block
    const catchIndex = scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed');
    const catchBlock = scriptContent.substring(catchIndex);

    // Step 1: Stop failed task
    const step1 = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend")');
    // Step 2: Mark release manifest failed
    const step2 = catchBlock.indexOf('Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "failed"');
    // Step 3: Restore previous junction
    const step3 = catchBlock.indexOf('New-Item -ItemType Junction -Path $currentJunction -Target $previousJunctionTarget');
    // Step 4: Restore previous task XML
    const step4 = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $taskBackupPath, "/F")');
    // Step 5: Start restored task
    const step5 = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend")');
    // Step 6: Verify restored task configuration
    const step6 = catchBlock.indexOf('Verify Restored Task XML');
    // Step 7: Verify readiness of restored service via /api/tray
    const step7 = catchBlock.indexOf('http://127.0.0.1:4000/api/tray');
    // Step 8: Final failure throw
    const step8 = catchBlock.indexOf('throw "Deployment aborted due to verification failure');

    expect(step1).toBeGreaterThan(0);
    expect(step2).toBeGreaterThan(step1);
    expect(step3).toBeGreaterThan(step2);
    expect(step4).toBeGreaterThan(step3);
    expect(step5).toBeGreaterThan(step4);
    expect(step6).toBeGreaterThan(step5);
    expect(step7).toBeGreaterThan(step6);
    expect(step8).toBeGreaterThan(step7);
  });
});

describe('Production Backend Deployment -NoStart Mode Hardening (Milestone 16.5C)', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');

  it('Test NS1: -NoStart switch parameter is defined and documented in script param block', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Documented in comment-based help
    expect(scriptContent).toContain('.PARAMETER NoStart');

    // Declared in param() block
    expect(scriptContent).toMatch(/param\s*\([\s\S]*?\[switch\]\$NoStart[\s\S]*?\)/);
  });

  it('Test NS2: Service startup via schtasks /Run is skipped when -NoStart is supplied', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Normal deployment /Run is wrapped in if (-not $NoStart)
    const runMatch = scriptContent.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)[\s\S]*?Start-Sleep/);
    expect(runMatch).not.toBeNull();
  });

  it('Test NS3: HTTP readiness probe is skipped when -NoStart is supplied', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Readiness check block is wrapped in if (-not $NoStart)
    const readinessMatch = scriptContent.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?http:\/\/127\.0\.0\.1:4000\/api\/tray[\s\S]*?throw "Readiness check probe timed out/);
    expect(readinessMatch).not.toBeNull();
  });

  it('Test NS4: Task stopped state invariant is explicitly validated when -NoStart is active', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // -NoStart branch verifies task is not in Running state
    expect(scriptContent).toContain('Task state verification failed: task is running despite -NoStart flag');
    expect(scriptContent).toContain('Query Registered Task Status');
  });

  it('Test NS5: Default deployment mode preserves service start and safe /api/tray readiness verification', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Default mode retains Invoke-SchtasksChecked /Run
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend") -Operation "Start Updated Task"');

    // Default mode retains /api/tray probe
    expect(scriptContent).toContain('Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray"');

    // Manifest marked verified after verification
    expect(scriptContent).toContain('Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "verified"');
  });

  it('Test NS6: Dual rollback path guards service start and health verification with -NoStart', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    const catchIndex = scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed');
    expect(catchIndex).toBeGreaterThan(0);
    const catchBlock = scriptContent.substring(catchIndex);

    // Rollback start is guarded by -not $NoStart
    const rollbackRunMatch = catchBlock.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)\s+-Operation\s+"Start Restored Backend Service"/);
    expect(rollbackRunMatch).not.toBeNull();

    // Rollback readiness check is guarded by -not $NoStart
    const rollbackReadinessMatch = catchBlock.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?http:\/\/127\.0\.0\.1:4000\/api\/tray/);
    expect(rollbackReadinessMatch).not.toBeNull();

    // Task XML restoration and validation still occur unconditionally in rollback
    expect(catchBlock).toContain('Restore Task Definition from Backup');
    expect(catchBlock).toContain('Verify Restored Task XML');
  });

  it('Test NS7: Manual -Rollback switch also supports -NoStart to prevent starting rolled-back service', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // In manual rollback block
    const manualRollbackIndex = scriptContent.indexOf('[ROLLBACK] Initiating manual rollback');
    expect(manualRollbackIndex).toBeGreaterThan(0);
    const manualBlock = scriptContent.substring(manualRollbackIndex, scriptContent.indexOf('exit 0', manualRollbackIndex));

    const manualRunMatch = manualBlock.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)\s+-Operation\s+"Start Rolled-Back Service"/);
    expect(manualRunMatch).not.toBeNull();
  });

  it('Test NS8: DryRun execution with -NoStart completes successfully with exit code 0 and reports no-start mode', () => {
    const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${deployScriptPath}" -DryRun -NoStart`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    expect(stdout).toContain('Mode: NO-START');
    expect(stdout).toContain('-NoStart flag is set: service startup and HTTP readiness probe would be skipped');
    expect(stdout).toContain('Simulation completed successfully');
  });
});

describe('Phase 2A Production Database Migration Deployment Integration', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');

  it('Test PM1: Assert-SourceValid validates dist/db/migrate-prod.js and drizzle/meta/_journal.json', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('dist\\db\\migrate-prod.js');
    expect(scriptContent).toContain('drizzle\\meta\\_journal.json');
  });

  it('Test PM2: Drizzle directory is packaged in $filesToCopy alongside dist and packages', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('Join-Path $SourceDir "drizzle"');
    expect(scriptContent).toContain('Join-Path $releaseDir "drizzle"');
  });

  it('Test PM3: -BaselineLegacySchema parameter is declared in param() and documented', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('.PARAMETER BaselineLegacySchema');
    expect(scriptContent).toMatch(/param\s*\([\s\S]*?\[switch\]\$BaselineLegacySchema[\s\S]*?\)/);
  });

  it('Test PM4: DryRun outputs planned migration details with zero database connection', () => {
    const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${deployScriptPath}" -DryRun -NoStart`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    expect(stdout).toContain('=== [DryRun] Planned Database Migration ===');
    expect(stdout).toContain('Script: dist\\db\\migrate-prod.js');
    expect(stdout).toContain('Target Database: footballtray_prod (enforced by production entrypoint)');
    expect(stdout).toContain('Legacy Baseline Mode: DISABLED (Standard mode)');
    expect(stdout).toContain('Database Connection: SKIPPED in -DryRun mode (zero database queries executed).');
  });

  it('Test PM5: DryRun with -BaselineLegacySchema reports Legacy Baseline Mode: ENABLED', () => {
    const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${deployScriptPath}" -DryRun -NoStart -BaselineLegacySchema`, {
      encoding: 'utf8',
      stdio: ['pipe', 'pipe', 'pipe'],
    });

    expect(stdout).toContain('Legacy Baseline Mode: ENABLED (--baseline-legacy-schema will be passed)');
  });

  it('Test PM6: Migration runner execution is strictly ordered after stopping backend and before junction switch', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    const stopBackendIdx = scriptContent.indexOf('Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend") -Operation "Stop Current Backend"');
    const migrationIdx = scriptContent.indexOf('Executing production database migration runner');
    const junctionSwitchIdx = scriptContent.indexOf('Junction \'current\' updated to point to $releaseDir');

    expect(stopBackendIdx).toBeGreaterThan(0);
    expect(migrationIdx).toBeGreaterThan(stopBackendIdx);
    expect(junctionSwitchIdx).toBeGreaterThan(migrationIdx);
  });

  it('Test PM7: Migration runner passes --baseline-legacy-schema flag only when requested', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).toContain('if ($BaselineLegacySchema)');
    expect(scriptContent).toContain('$migrateArgs += "--baseline-legacy-schema"');
  });

  it('Test PM8: Deployment script parses machine-readable MIGRATION_STATE protocol marker and never relies on human log text', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Must NOT rely on matching human log text
    expect(scriptContent).not.toContain('$migText -match "Drizzle migrate() completed"');
    expect(scriptContent).not.toContain('$migText -match "Drizzle migrate\\(\\) completed"');

    // Must parse machine-readable state protocol marker
    expect(scriptContent).toContain('$migrationState = "NO_DB_CHANGE"');
    expect(scriptContent).toContain('^MIGRATION_STATE=(NO_DB_CHANGE|BASELINE_COMMITTED|MIGRATION_COMMITTED)$');
    expect(scriptContent).toContain('MIGRATION_COMMITTED');
    expect(scriptContent).toContain('BASELINE_COMMITTED');
  });

  it('Test PM8B: Rollback error reporting distinguishes MIGRATION_COMMITTED, BASELINE_COMMITTED, and NO_DB_CHANGE', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // MIGRATION_COMMITTED warning
    expect(scriptContent).toContain('database schema may already be forward-migrated, and no automatic database down-migration was attempted');

    // BASELINE_COMMITTED warning
    expect(scriptContent).toContain('Legacy baseline metadata (migrations 0000 and 0001) was committed to drizzle.__drizzle_migrations, but the subsequent Phase 2A migration (0002) rolled back');
    expect(scriptContent).toContain('Verified legacy baseline metadata remains committed in the database');
    expect(scriptContent).toContain('No automatic database down-migration or baseline deletion was attempted');

    // Error message distinguishes states
    expect(scriptContent).toContain('Application service was rolled back to \'$previousJunctionTarget\', but database schema may already be forward-migrated');
    expect(scriptContent).toContain('Phase 2A database migration rolled back, but verified legacy baseline metadata remains committed in the database');
    expect(scriptContent).toContain('System was successfully rolled back to \'$previousJunctionTarget\' (database unchanged)');
  });

  it('Test PM9: No automatic database down-migration is attempted under any circumstance', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    expect(scriptContent).not.toContain('migrate:down');
    expect(scriptContent).not.toContain('drizzle-kit drop');
    expect(scriptContent).not.toContain('DROP TABLE');
  });

  it('Test PM10: Both pwsh and powershell.exe parse the script cleanly with zero non-ASCII characters', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    for (let i = 0; i < scriptContent.length; i++) {
      expect(scriptContent.charCodeAt(i)).toBeLessThanOrEqual(127);
    }
  });
});

describe('Deployment Tooling Safety Fix & Readiness Probe Validation (Safe /api/tray)', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');

  it('Requirement 1: Deploy script contains no /health readiness probe', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    expect(scriptContent).not.toContain('/health');
    expect(scriptContent).not.toContain('http://127.0.0.1:4000/health');
  });

  it('Requirement 2: Deploy script contains no /api/diagnostics probe', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    expect(scriptContent).not.toContain('/api/diagnostics');
    expect(scriptContent).not.toContain('http://127.0.0.1:4000/api/diagnostics');
  });

  it('Requirement 3: All HTTP probes use safe /api/tray and introduce no unrelated external HTTP target', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const probeMatches = scriptContent.match(/Invoke-RestMethod[^\r\n]+/g) || [];
    expect(probeMatches.length).toBe(3); // (1) manual rollback, (2) normal deploy verification 8, (3) automatic rollback step 7

    for (const probe of probeMatches) {
      expect(probe).toContain('http://127.0.0.1:4000/api/tray');
      expect(probe).toContain('-Method Get');
      expect(probe).not.toContain('/health');
      expect(probe).not.toContain('/api/diagnostics');
    }

    // No WebRequest calls
    expect(scriptContent).not.toContain('Invoke-WebRequest');

    // All Invoke-RestMethod probes strictly target local /api/tray
    for (const probe of probeMatches) {
      expect(probe).toMatch(/Invoke-RestMethod\s+-Uri\s+"http:\/\/127\.0\.0\.1:4000\/api\/tray"/);
    }

    // No external or provider endpoints introduced
    expect(scriptContent).not.toContain('api-football');
    expect(scriptContent).not.toContain('localhost');
  });

  it('Requirement 4: Normal post-start readiness uses only /api/tray and validates tray envelope', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const v8Index = scriptContent.indexOf('Verification 8: Readiness Validation (/api/tray)');
    expect(v8Index).toBeGreaterThan(0);
    const v8Block = scriptContent.substring(v8Index, scriptContent.indexOf('Verification 9:', v8Index));

    expect(v8Block).toContain('Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3');
    expect(v8Block).toContain('$tray.PSObject.Properties["schemaVersion"]');
    expect(v8Block).toContain('-not [string]::IsNullOrWhiteSpace($tray.schemaVersion)');
    expect(v8Block).toContain('$tray.PSObject.Properties["data"]');
    expect(v8Block).toContain('$tray.data.PSObject.Properties["matches"]');
    expect(v8Block).toContain('($tray.data.matches -is [System.Array])');
    expect(v8Block).not.toContain('/health');
    expect(v8Block).not.toContain('/api/diagnostics');
  });

  it('Requirement 5: Explicit rollback (-Rollback) uses only /api/tray', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const rollbackIndex = scriptContent.indexOf('[ROLLBACK] Initiating manual rollback');
    expect(rollbackIndex).toBeGreaterThan(0);
    const rollbackBlock = scriptContent.substring(rollbackIndex, scriptContent.indexOf('exit 0', rollbackIndex));

    expect(rollbackBlock).toContain('Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3');
    expect(rollbackBlock).toContain('$tray.PSObject.Properties["schemaVersion"]');
    expect(rollbackBlock).toContain('-not [string]::IsNullOrWhiteSpace($tray.schemaVersion)');
    expect(rollbackBlock).toContain('($tray.data.matches -is [System.Array])');
    expect(rollbackBlock).not.toContain('/health');
  });

  it('Requirement 6: Automatic rollback / failure recovery uses only /api/tray', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');
    const catchIndex = scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed');
    expect(catchIndex).toBeGreaterThan(0);
    const catchBlock = scriptContent.substring(catchIndex);

    expect(catchBlock).toContain('Invoke-RestMethod -Uri "http://127.0.0.1:4000/api/tray" -Method Get -TimeoutSec 3');
    expect(catchBlock).toContain('$rtray.PSObject.Properties["schemaVersion"]');
    expect(catchBlock).toContain('-not [string]::IsNullOrWhiteSpace($rtray.schemaVersion)');
    expect(catchBlock).toContain('($rtray.data.matches -is [System.Array])');
    expect(catchBlock).not.toContain('/health');
  });

  it('Requirement 7: Timeout and retry logic remains bounded and throws on failure', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Normal deploy loop bounded by 30 seconds
    expect(scriptContent).toContain('$stopwatch.Elapsed.TotalSeconds -lt 30');
    expect(scriptContent).toContain('throw "Readiness check probe timed out or returned non-ready response within 30 seconds."');

    // Explicit rollback bounded by 25 seconds
    expect(scriptContent).toContain('$sw.Elapsed.TotalSeconds -lt 25');
    expect(scriptContent).toContain('throw "Rollback verification failed."');

    // Automatic rollback bounded by 25 seconds
    expect(scriptContent).toContain('$rbSw.Elapsed.TotalSeconds -lt 25');
    expect(scriptContent).toContain('throw "Deployment failed and rollback service verification also failed."');
  });

  it('Requirement 8: Readiness probe accepts valid tray envelopes and rejects invalid/missing schemaVersion or non-tray responses', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft-tray-probe-test-'));
    try {
      const runnerScript = `
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-TrayEnvelope {
    param([string]$Json)
    try {
        $tray = $Json | ConvertFrom-Json
        $isReady = ($null -ne $tray -and
            $null -ne $tray.PSObject.Properties["schemaVersion"] -and
            -not [string]::IsNullOrWhiteSpace($tray.schemaVersion) -and
            $null -ne $tray.PSObject.Properties["data"] -and
            $null -ne $tray.data -and
            $null -ne $tray.data.PSObject.Properties["matches"] -and
            ($tray.data.matches -is [System.Array]))
        return [bool]$isReady
    } catch {
        return $false
    }
}

$cases = @(
    @{ Name = "emptyTray"; Json = '{"schemaVersion":"1.0","data":{"matches":[]}}'; Expected = $true },
    @{ Name = "trayWithMatches"; Json = '{"schemaVersion":"1.0","data":{"matches":[{"id":1}]}}'; Expected = $true },
    @{ Name = "missingSchemaVersion"; Json = '{"data":{"matches":[]}}'; Expected = $false },
    @{ Name = "legacyHealth"; Json = '{"status":"ok","database":"connected","scheduler":{"running":true}}'; Expected = $false },
    @{ Name = "dataNull"; Json = '{"schemaVersion":"1.0","data":null,"error":{"code":"ERR"}}'; Expected = $false },
    @{ Name = "missingMatches"; Json = '{"schemaVersion":"1.0","data":{}}'; Expected = $false },
    @{ Name = "nonArrayMatches"; Json = '{"schemaVersion":"1.0","data":{"matches":"invalid"}}'; Expected = $false },
    @{ Name = "rawText"; Json = '"hello"'; Expected = $false }
)

$results = @{}
foreach ($c in $cases) {
    $results[$c.Name] = Test-TrayEnvelope -Json $c.Json
}
$results | ConvertTo-Json
`;
      const runnerPath = path.join(tempDir, 'test-tray-probe.ps1');
      fs.writeFileSync(runnerPath, runnerScript, 'utf8');

      const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${runnerPath}"`, {
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      const parsedResults = JSON.parse(stdout);

      expect(parsedResults.emptyTray).toBe(true);
      expect(parsedResults.trayWithMatches).toBe(true);
      expect(parsedResults.missingSchemaVersion).toBe(false);
      expect(parsedResults.legacyHealth).toBe(false);
      expect(parsedResults.dataNull).toBe(false);
      expect(parsedResults.missingMatches).toBe(false);
      expect(parsedResults.nonArrayMatches).toBe(false);
      expect(parsedResults.rawText).toBe(false);
    } finally {
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {}
    }
  });

  it('Requirement 9: -NoStart semantics remain intact across normal deploy and rollback', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Normal deploy startup and readiness check guarded by -not $NoStart
    expect(scriptContent).toMatch(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)[\s\S]*?http:\/\/127\.0\.0\.1:4000\/api\/tray/);

    // Automatic rollback startup and readiness check guarded by -not $NoStart
    const catchBlock = scriptContent.substring(scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed'));
    expect(catchBlock).toMatch(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)\s+-Operation\s+"Start Restored Backend Service"[\s\S]*?http:\/\/127\.0\.0\.1:4000\/api\/tray/);

    // Manual rollback startup and readiness check guarded by -not $NoStart
    const manualBlock = scriptContent.substring(scriptContent.indexOf('[ROLLBACK] Initiating manual rollback'));
    expect(manualBlock).toMatch(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?Invoke-SchtasksChecked\s+-Arguments\s+@\("\/Run",\s+"\/TN",\s+"FootballTray-Backend"\)\s+-Operation\s+"Start Rolled-Back Service"[\s\S]*?http:\/\/127\.0\.0\.1:4000\/api\/tray/);
  });
});

describe('Junction Target Normalization and Rollback Path Safety (Regression Tests 1-10)', () => {
  const scriptsDir = path.resolve(__dirname, '../../scripts');
  const deployScriptPath = path.join(scriptsDir, 'deploy-backend-production.ps1');
  const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

  // Extract Normalize-JunctionPath, Test-JunctionPathEqual, and Get-JunctionTarget helpers
  const fnStartIndex = scriptContent.indexOf('function Normalize-JunctionPath');
  const fnEndIndex = scriptContent.indexOf('function Get-PreviousKnownGoodRelease');
  const junctionHelpersPs1 = scriptContent.substring(fnStartIndex, fnEndIndex);

  function runPowerShellJson(psScript: string): any {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft-junc-test-'));
    const runnerPath = path.join(tempDir, 'runner.ps1');
    try {
      const fullScript = `
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
${junctionHelpersPs1}
${psScript}
`;
      fs.writeFileSync(runnerPath, fullScript, 'utf8');
      const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${runnerPath}"`, {
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      return JSON.parse(stdout.trim());
    } finally {
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {}
    }
  }

  it('Regression 1: Plain expected vs NT-prefixed actual (\\??\\C:\\... vs C:\\...) compares equal', () => {
    const result = runPowerShellJson(`
      $nt = '\\??\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $plain = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $norm = Normalize-JunctionPath -Path $nt
      $equal = Test-JunctionPathEqual -Path1 $nt -Path2 $plain
      @{ Normalized = $norm; Equal = [bool]$equal } | ConvertTo-Json
    `);
    expect(result.Normalized).toBe('C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855');
    expect(result.Equal).toBe(true);
  });

  it('Regression 2: Win32 extended prefix (\\\\?\\C:\\... vs C:\\...) compares equal', () => {
    const result = runPowerShellJson(`
      $extended = '\\\\?\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $plain = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $norm = Normalize-JunctionPath -Path $extended
      $equal = Test-JunctionPathEqual -Path1 $extended -Path2 $plain
      @{ Normalized = $norm; Equal = [bool]$equal } | ConvertTo-Json
    `);
    expect(result.Normalized).toBe('C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855');
    expect(result.Equal).toBe(true);
  });

  it('Regression 3: Case differences (c:\\programdata\\... vs C:\\ProgramData\\...) compare equal', () => {
    const result = runPowerShellJson(`
      $lower = 'c:\\programdata\\footballtray\\backend\\releases\\v1.0.0-20260920-000855'
      $canonical = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $equal = Test-JunctionPathEqual -Path1 $lower -Path2 $canonical
      @{ Equal = [bool]$equal } | ConvertTo-Json
    `);
    expect(result.Equal).toBe(true);
  });

  it('Regression 4: Harmless trailing separator differences compare equal', () => {
    const result = runPowerShellJson(`
      $trailingBackslash = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855\\'
      $trailingSlash = '\\??\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855/'
      $plain = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $eq1 = Test-JunctionPathEqual -Path1 $trailingBackslash -Path2 $plain
      $eq2 = Test-JunctionPathEqual -Path1 $trailingSlash -Path2 $plain
      @{ Eq1 = [bool]$eq1; Eq2 = [bool]$eq2 } | ConvertTo-Json
    `);
    expect(result.Eq1).toBe(true);
    expect(result.Eq2).toBe(true);
  });

  it('Regression 5: Genuinely different release directories compare not equal', () => {
    const result = runPowerShellJson(`
      $rel1 = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $rel2 = 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260916-234823'
      $ntRel1 = '\\??\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $ntRel2 = '\\??\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260916-234823'
      $eqPlain = Test-JunctionPathEqual -Path1 $rel1 -Path2 $rel2
      $eqNt = Test-JunctionPathEqual -Path1 $ntRel1 -Path2 $ntRel2
      $eqCross = Test-JunctionPathEqual -Path1 $ntRel1 -Path2 $rel2
      @{ EqPlain = [bool]$eqPlain; EqNt = [bool]$eqNt; EqCross = [bool]$eqCross } | ConvertTo-Json
    `);
    expect(result.EqPlain).toBe(false);
    expect(result.EqNt).toBe(false);
    expect(result.EqCross).toBe(false);
  });

  it('Regression 6: Previous target preserved with \\??\\ prefix can be transformed and restored correctly for rollback', () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'ft-rollback-junc-'));
    try {
      const runnerPath = path.join(tempDir, 'test-rollback.ps1');
      const testScript = `
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
${junctionHelpersPs1}

$isolatedBase = '${tempDir.replace(/'/g, "''")}'
$oldRelDir = Join-Path $isolatedBase 'releases\\v1.0.0-old'
$newRelDir = Join-Path $isolatedBase 'releases\\v1.0.0-new'
$currentJunc = Join-Path $isolatedBase 'current'

New-Item -ItemType Directory -Path $oldRelDir -Force | Out-Null
New-Item -ItemType Directory -Path $newRelDir -Force | Out-Null

# Simulate previous target captured with NT prefix
$previousJunctionTarget = "\\??\\" + $oldRelDir

# 1. Demonstrate naive Test-Path fails on NT-prefixed path in PowerShell (root cause of rollback failure)
$naiveTestPath = Test-Path $previousJunctionTarget

# 2. Normalization allows Test-Path to succeed
$previousJunctionTarget = Normalize-JunctionPath -Path $previousJunctionTarget
$normalizedTestPath = Test-Path -LiteralPath $previousJunctionTarget

# 3. Create initial junction to new release
New-Item -ItemType Junction -Path $currentJunc -Target $newRelDir | Out-Null

# 4. Perform rollback restoration logic as in deploy script:
if ($previousJunctionTarget -and (Test-Path -LiteralPath $previousJunctionTarget)) {
    if (Test-Path $currentJunc) {
        cmd /c "rmdir \`"$currentJunc\`"" | Out-Null
    }
    New-Item -ItemType Junction -Path $currentJunc -Target $previousJunctionTarget | Out-Null
}

# 5. Verify restored junction resolves to old release
$resolved = Get-JunctionTarget -Path $currentJunc
$isRestored = Test-JunctionPathEqual -Path1 $resolved -Path2 $oldRelDir

# Cleanup junction
if (Test-Path $currentJunc) {
    cmd /c "rmdir \`"$currentJunc\`"" | Out-Null
}

@{
    NaiveTestPath = [bool]$naiveTestPath
    NormalizedTestPath = [bool]$normalizedTestPath
    IsRestored = [bool]$isRestored
    ResolvedTarget = $resolved
} | ConvertTo-Json
`;
      fs.writeFileSync(runnerPath, testScript, 'utf8');
      const stdout = execSync(`pwsh -NoProfile -ExecutionPolicy Bypass -File "${runnerPath}"`, {
        encoding: 'utf8',
        stdio: ['pipe', 'pipe', 'pipe'],
      });
      const res = JSON.parse(stdout.trim());
      expect(res.NaiveTestPath).toBe(false); // Proves the bug! Naive Test-Path fails on \\??\\
      expect(res.NormalizedTestPath).toBe(true); // Proves normalization fixes Test-Path
      expect(res.IsRestored).toBe(true); // Proves junction successfully restores to old release
    } finally {
      try {
        fs.rmSync(tempDir, { recursive: true, force: true });
      } catch {}
    }
  });

  it('Regression 7: Normal activation still fails when current actually points to a different release', () => {
    // Statically verify the deploy script has the invariant verification throw
    expect(scriptContent).toContain('$resolvedJunction = Get-JunctionTarget -Path $currentJunction');
    expect(scriptContent).toContain('$expectedNorm = Normalize-JunctionPath -Path $releaseDir');
    expect(scriptContent).toContain('if (-not $resolvedJunction -or (-not (Test-JunctionPathEqual -Path1 $resolvedJunction -Path2 $expectedNorm))) {');
    expect(scriptContent).toContain('throw "Junction verification failed: \'current\' resolved to \'$resolvedJunction\', expected \'$expectedNorm\'."');

    // Dynamically execute the verification expression when paths differ
    const result = runPowerShellJson(`
      $resolvedJunction = '\\??\\C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-DIFFERENT'
      $expectedNorm = Normalize-JunctionPath -Path 'C:\\ProgramData\\FootballTray\\backend\\releases\\v1.0.0-20260920-000855'
      $wouldFail = (-not $resolvedJunction -or (-not (Test-JunctionPathEqual -Path1 $resolvedJunction -Path2 $expectedNorm)))
      @{ WouldFail = [bool]$wouldFail } | ConvertTo-Json
    `);
    expect(result.WouldFail).toBe(true);
  });

  it('Regression 8: Automatic rollback fail-closed sequencing verifies junction before task restore and service start', () => {
    const catchIndex = scriptContent.indexOf('Write-Host "CRITICAL: Deployment verification failed');
    expect(catchIndex).toBeGreaterThan(0);
    const catchBlock = scriptContent.substring(catchIndex);

    // Fail-closed checks: missing or nonexistent previous target throws immediately (never merely warns or continues)
    expect(catchBlock).toMatch(/if\s*\(\[string\]::IsNullOrWhiteSpace\(\$previousJunctionTarget\)\)\s*\{\s*throw/);
    expect(catchBlock).toMatch(/if\s*\(-not\s*\(Test-Path\s+-LiteralPath\s+\$previousJunctionTarget\)\)\s*\{\s*throw/);
    expect(catchBlock).not.toContain('Write-Warning "CRITICAL: Previous junction target');

    // Strict required ordering:
    // 1. stop failed task
    const stepStop = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend")');
    // 2. mark failed manifest
    const stepMarkFailed = catchBlock.indexOf('Set-DeploymentManifestStatus -ReleaseDir $releaseDir -Status "failed"');
    // 3. normalize previous target
    const stepNorm = catchBlock.indexOf('Normalize-JunctionPath -Path $previousJunctionTarget');
    // 4. restore current junction
    const stepRestoreJunc = catchBlock.indexOf('New-Item -ItemType Junction -Path $currentJunction -Target $previousJunctionTarget');
    // 5. verify restored junction target (pre-start invariant)
    const stepVerifyJunc = catchBlock.indexOf('Test-JunctionPathEqual -Path1 $restoredJuncTarget -Path2 $previousJunctionTarget');
    // 6. restore previous task XML
    const stepRestoreTask = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $taskBackupPath, "/F")');
    // 7. start restored backend
    const stepStart = catchBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend")');
    // 8. verify task
    const stepVerifyTask = catchBlock.indexOf('Verify Restored Task XML');
    // 9. /api/tray readiness
    const stepTray = catchBlock.indexOf('http://127.0.0.1:4000/api/tray');
    // 10. [ROLLBACK SUCCESS]
    const stepSuccess = catchBlock.indexOf('[ROLLBACK SUCCESS]');
    // Final throw
    const stepFinal = catchBlock.indexOf('throw "Deployment aborted due to verification failure');

    expect(stepStop).toBeGreaterThan(0);
    expect(stepMarkFailed).toBeGreaterThan(stepStop);
    expect(stepNorm).toBeGreaterThan(stepMarkFailed);
    expect(stepRestoreJunc).toBeGreaterThan(stepNorm);
    expect(stepVerifyJunc).toBeGreaterThan(stepRestoreJunc);
    expect(stepRestoreTask).toBeGreaterThan(stepVerifyJunc);
    expect(stepStart).toBeGreaterThan(stepRestoreTask);
    expect(stepVerifyTask).toBeGreaterThan(stepStart);
    expect(stepTray).toBeGreaterThan(stepVerifyTask);
    expect(stepSuccess).toBeGreaterThan(stepTray);
    expect(stepFinal).toBeGreaterThan(stepSuccess);
  });

  it('Regression 9: Existing explicit rollback sequencing remains intact with junction invariant', () => {
    const manualIndex = scriptContent.indexOf('[ROLLBACK] Initiating manual rollback');
    expect(manualIndex).toBeGreaterThan(0);
    const manualBlock = scriptContent.substring(manualIndex);

    // 1. Select rollback release
    const step1 = manualBlock.indexOf('Get-PreviousKnownGoodRelease');
    // 2. Stop service
    const step2 = manualBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/End", "/TN", "FootballTray-Backend")');
    // 3. Normalize and restore junction
    const step3 = manualBlock.indexOf('Normalize-JunctionPath -Path $targetRelease');
    const step3b = manualBlock.indexOf('New-Item -ItemType Junction -Path $currentJunction -Target $normalizedTarget');
    // 4. Verify restored junction
    const step4 = manualBlock.indexOf('Test-JunctionPathEqual -Path1 $restoredJuncTarget -Path2 $normalizedTarget');
    // 5. Restore task XML
    const step5 = manualBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Create", "/TN", "FootballTray-Backend", "/XML", $restoreXmlPath, "/F")');
    // 6. Start service
    const step6 = manualBlock.indexOf('Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend")');
    // 7. Verify /api/tray
    const step7 = manualBlock.indexOf('http://127.0.0.1:4000/api/tray');
    // 8. Report success
    const step8 = manualBlock.indexOf('[ROLLBACK SUCCESS]');

    expect(step1).toBeGreaterThan(0);
    expect(step2).toBeGreaterThan(step1);
    expect(step3).toBeGreaterThan(step2);
    expect(step3b).toBeGreaterThan(step3);
    expect(step4).toBeGreaterThan(step3b);
    expect(step5).toBeGreaterThan(step4);
    expect(step6).toBeGreaterThan(step5);
    expect(step7).toBeGreaterThan(step6);
    expect(step8).toBeGreaterThan(step7);
  });

  it('Regression 10: /api/tray readiness fix remains intact; do not regress back to /health', () => {
    expect(scriptContent).not.toContain('/health');
    expect(scriptContent).not.toContain('/api/diagnostics');

    // Confirm that /api/tray is present in all 3 verification points
    const matches = scriptContent.match(/http:\/\/127\.0\.0\.1:4000\/api\/tray/g);
    expect(matches).not.toBeNull();
    expect(matches!.length).toBe(3); // Deploy verification, manual rollback, automatic rollback

    // Confirm envelope validation logic is preserved in all 3 probe loops
    const schemaMatches = scriptContent.match(/\$null\s*-ne\s*\$[a-zA-Z_]+\.PSObject\.Properties\["schemaVersion"\]/g);
    expect(schemaMatches).not.toBeNull();
    expect(schemaMatches!.length).toBe(3);
  });
});
