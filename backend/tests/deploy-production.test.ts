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
    expect(scriptContent).toContain('$expectedNorm = [System.IO.Path]::GetFullPath($releaseDir).TrimEnd');

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

    // 8. Health check
    expect(scriptContent).toContain('http://127.0.0.1:4000/health');

    // 9. Update manifest only after all preceding checks pass
    expect(scriptContent).toMatch(/health.*Set-DeploymentManifestStatus.*verified/s);
  });

  it('Test 12: Dual rollback restores previous junction and previous task XML', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Restores junction
    expect(scriptContent).toContain('New-Item -ItemType Junction -Path $currentJunction -Target $previousJunctionTarget');

    // Restores task XML from validated backup
    expect(scriptContent).toContain('Restore Task Definition from Backup');

    // Verifies restored task and /health
    expect(scriptContent).toContain('Verify Restored Task XML');
    expect(scriptContent).toContain('Verifying health of restored service');

    // Refuses success if rollback health verification fails
    expect(scriptContent).toContain('Rollback health verification failed');
    expect(scriptContent).toContain('throw "Deployment failed and rollback service verification also failed."');
  });

  it('Test 13: Production environment file remains outside release payload folders', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // .env.production.local is placed strictly in TargetDir\config, not in releases\<releaseId>
    expect(scriptContent).toContain('$targetEnvPath = Join-Path $configDir ".env.production.local"');
    expect(scriptContent).not.toContain('Join-Path $releaseDir ".env.production.local"');

    // Files copied to releaseDir are strictly dist, package.json, package-lock.json
    expect(scriptContent).toContain('Join-Path $SourceDir "dist"');
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
    // Step 7: Verify /health of restored service
    const step7 = catchBlock.indexOf('http://127.0.0.1:4000/health');
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

  it('Test NS3: HTTP /health probe is skipped when -NoStart is supplied', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Health check block is wrapped in if (-not $NoStart)
    const healthMatch = scriptContent.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?http:\/\/127\.0\.0\.1:4000\/health[\s\S]*?throw "Health check probe timed out/);
    expect(healthMatch).not.toBeNull();
  });

  it('Test NS4: Task stopped state invariant is explicitly validated when -NoStart is active', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // -NoStart branch verifies task is not in Running state
    expect(scriptContent).toContain('Task state verification failed: task is running despite -NoStart flag');
    expect(scriptContent).toContain('Query Registered Task Status');
  });

  it('Test NS5: Default deployment mode preserves original service start and /health verification', () => {
    const scriptContent = fs.readFileSync(deployScriptPath, 'utf8');

    // Default mode retains Invoke-SchtasksChecked /Run
    expect(scriptContent).toContain('Invoke-SchtasksChecked -Arguments @("/Run", "/TN", "FootballTray-Backend") -Operation "Start Updated Task"');

    // Default mode retains /health probe
    expect(scriptContent).toContain('Invoke-RestMethod -Uri "http://127.0.0.1:4000/health"');

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

    // Rollback health check is guarded by -not $NoStart
    const rollbackHealthMatch = catchBlock.match(/if\s*\(-not\s*\$NoStart\)\s*\{[\s\S]*?http:\/\/127\.0\.0\.1:4000\/health/);
    expect(rollbackHealthMatch).not.toBeNull();

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
    expect(stdout).toContain('-NoStart flag is set: service startup and HTTP /health probe would be skipped');
    expect(stdout).toContain('Simulation completed successfully');
  });
});
