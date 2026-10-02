# Offline tests: MCP protocol, tool catalogue, safety gates, LAD builder.
# TIA Portal has to be INSTALLED (the Openness DLL is loaded) but need not be running;
# nothing here connects to TIA or touches a project.
#
#   .\tests\protocol-tests.ps1 [-Exe .\bin\Release\TiaMcpServer.exe]

param([string] $Exe = "$PSScriptRoot\..\bin\Release\TiaMcpServer.exe")

$ErrorActionPreference = 'Stop'
Import-Module "$PSScriptRoot\McpClient.psm1" -Force

$script:Passed = 0
$script:Failed = 0

function Check([string] $Name, [bool] $Condition, [string] $Detail = '') {
    if ($Condition) { $script:Passed++; Write-Host "  ok    $Name" }
    else { $script:Failed++; Write-Host "  FAIL  $Name  $Detail" -ForegroundColor Red }
}

function Section([string] $Title) { Write-Host "`n== $Title" }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ("tia-mcp-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox | Out-Null

try {
    # ------------------------------------------------------------------ one-shot
    Section 'One-shot commands'
    $version = [string](& $Exe --version)
    Check '--version prints name and version' ($version -match '^tia-portal-mcp \d+\.\d+\.\d+$') $version
    $help = (& $Exe --help) -join "`n"
    Check '--help lists the access levels' ($help -match '--access' -and $help -match 'readonly')
    $md = (& $Exe --dump-tools markdown) -join "`n"
    Check '--dump-tools markdown renders the reference' ($md -match '# Tool reference' -and $md -match 'tia_connect')
    # Windows PowerShell turns redirected native stderr into error records; do not let that stop the run.
    $ErrorActionPreference = 'Continue'
    & $Exe --no-such-option 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Check 'an unknown option exits with code 2' ($exitCode -eq 2) "exit $exitCode"

    # ------------------------------------------------------------------ protocol
    Section 'Protocol (default: access full, all groups)'
    $s = Start-McpServer -Exe $Exe
    Check 'initialize echoes a supported protocol version' ($s.Initialize.protocolVersion -eq '2025-06-18')
    Check 'initialize names the server' ($s.Initialize.serverInfo.name -eq 'tia-portal-mcp')
    Check 'initialize carries instructions' ($s.Initialize.instructions.Length -gt 100)

    $old = Invoke-Mcp $s 'initialize' @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 't'; version = '1' } }
    Check 'an older protocol version is accepted as-is' ($old.result.protocolVersion -eq '2024-11-05')
    $unknown = Invoke-Mcp $s 'initialize' @{ protocolVersion = '1999-01-01'; capabilities = @{}; clientInfo = @{ name = 't'; version = '1' } }
    Check 'an unknown protocol version falls back to the newest' ($unknown.result.protocolVersion -eq '2025-06-18')

    Check 'ping answers' ($null -ne (Invoke-Mcp $s 'ping').result)
    $nf = Invoke-Mcp $s 'no/such/method'
    Check 'an unknown method is a JSON-RPC error' ($nf.error.code -eq -32601)

    $tools = (Invoke-Mcp $s 'tools/list').result.tools
    Check 'tools/list returns the catalogue' ($tools.Count -ge 90) "count=$($tools.Count)"
    Check 'tool names are unique' (($tools.name | Sort-Object -Unique).Count -eq $tools.Count)
    Check 'every tool name starts with tia_' (@($tools | Where-Object { $_.name -notmatch '^tia_[a-z0-9_]+$' }).Count -eq 0)

    $untagged = @($tools | Where-Object { $_.description -notmatch '^\[(READ-ONLY|WRITE|DESTRUCTIVE)' })
    Check 'every description starts with its safety class' ($untagged.Count -eq 0) ($untagged.name -join ', ')

    $noAnnotations = @($tools | Where-Object { $null -eq $_.annotations -or $null -eq $_.annotations.readOnlyHint })
    Check 'every tool has MCP annotations' ($noAnnotations.Count -eq 0)

    $badHints = @($tools | Where-Object {
        ($_.description -cmatch '^\[READ-ONLY\]') -ne [bool]$_.annotations.readOnlyHint -or
        ($_.description -cmatch '^\[[^\]]*DESTRUCTIVE') -ne [bool]$_.annotations.destructiveHint })
    Check 'annotations agree with the safety tag' ($badHints.Count -eq 0) ($badHints.name -join ', ')

    $destructive = @($tools | Where-Object { $_.description -match '^\[DESTRUCTIVE' })
    $missingConfirm = @($destructive | Where-Object { $_.inputSchema.required -notcontains 'confirm' })
    Check 'destructive tools require confirm' ($destructive.Count -ge 10 -and $missingConfirm.Count -eq 0) ($missingConfirm.name -join ', ')

    $conditional = @($tools | Where-Object { $_.description -match '^\[WRITE - DESTRUCTIVE when' })
    $noOptionalConfirm = @($conditional | Where-Object { $null -eq $_.inputSchema.properties.confirm })
    Check 'conditionally destructive tools offer confirm' ($conditional.Count -ge 10 -and $noOptionalConfirm.Count -eq 0) ($noOptionalConfirm.name -join ', ')

    $badRequired = @($tools | Where-Object {
        $t = $_
        @($t.inputSchema.required | Where-Object { $_ -and $null -eq $t.inputSchema.properties.$_ }).Count -gt 0 })
    Check 'every required argument is declared' ($badRequired.Count -eq 0) ($badRequired.name -join ', ')

    $noDescription = @($tools | ForEach-Object {
        $t = $_
        $t.inputSchema.properties.PSObject.Properties | Where-Object { -not $_.Value.description -and $_.Value.type -ne 'integer' } |
            ForEach-Object { "$($t.name).$($_.Name)" } })
    Check 'arguments are described' ($noDescription.Count -eq 0) ($noDescription -join ', ')

    $missing = Invoke-Mcp $s 'tools/call' @{ name = 'tia_nope'; arguments = @{} }
    Check 'an unknown tool is a JSON-RPC error' ($missing.error.code -eq -32602)

    # ------------------------------------------------------------------ not connected
    Section 'Before connecting'
    $state = Invoke-McpTool $s 'tia_state'
    Check 'tia_state works unconnected' (-not $state.IsError -and $state.Data.connected -eq $false)
    Check 'tia_state reports the server configuration' ($state.Data.server.access -eq 'full' -and $state.Data.server.groups -contains 'plc')
    $blocks = Invoke-McpTool $s 'tia_list_blocks'
    Check 'a project tool explains that tia_connect is needed' ($blocks.IsError -and $blocks.Text -match 'tia_connect')
    $noArg = Invoke-McpTool $s 'tia_read_block'
    Check 'a missing required argument is named' ($noArg.IsError -and $noArg.Text -match "'block'")

    # ------------------------------------------------------------------ confirm gate
    Section 'Confirmation gate'
    $del = Invoke-McpTool $s 'tia_delete_block' @{ block = 'Anything' }
    Check 'destructive without confirm is refused' ($del.IsError -and $del.Text -match 'Confirmation required')
    $del2 = Invoke-McpTool $s 'tia_delete_block' @{ block = 'Anything'; confirm = 'false' }
    Check 'confirm:"false" (string) is still a refusal' ($del2.IsError -and $del2.Text -match 'Confirmation required')
    $del3 = Invoke-McpTool $s 'tia_delete_block' @{ block = 'Anything'; confirm = $true }
    Check 'with confirm the call reaches the handler' ($del3.IsError -and $del3.Text -match 'tia_connect')
    $ow = Invoke-McpTool $s 'tia_import_block' @{ xml = '<Document/>'; overwrite = $true }
    Check 'overwrite:true without confirm is refused' ($ow.IsError -and $ow.Text -match 'Confirmation required')
    $rep = Invoke-McpTool $s 'tia_hmi_replace_tag_refs' @{ pattern = 'a'; replacement = 'b'; dryRun = $false }
    Check 'dryRun:false without confirm is refused' ($rep.IsError -and $rep.Text -match 'Confirmation required')
    $items = Invoke-McpTool $s 'tia_hmi_create_items' @{ screen = 'S'; items = @(@{ type = 'HmiButton'; name = 'B'; overwrite = $true }) }
    Check 'an item with overwrite:true needs confirm' ($items.IsError -and $items.Text -match 'Confirmation required')

    # ------------------------------------------------------------------ LAD builder
    Section 'LAD builder (dry run, no TIA)'
    $sealIn = @{
        name = 'MotorSealIn'; type = 'FB'; dryRun = $true
        interface = @{
            input  = @(@{ name = 'Start'; datatype = 'Bool' }, @{ name = 'Stop'; datatype = 'Bool' })
            output = @(@{ name = 'Motor'; datatype = 'Bool' })
            static = @(@{ name = 'Tmr'; datatype = 'TON_TIME' })
        }
        networks = @(
            @{ elements = @(
                   @{ type = 'branch'; branches = @(, @(@{ type = 'contact'; operand = '#Start' })) + @(, @(@{ type = 'contact'; operand = '#Motor' })) },
                   @{ type = 'contact'; operand = '#Stop'; negated = $true })
               outputs = @(@{ type = 'coil'; operand = '#Motor' }) },
            @{ elements = @(@{ type = 'contact'; operand = '#Motor' }, @{ type = 'ton'; instance = '#Tmr'; pt = 'T#5s' })
               outputs = @(@{ type = 'scoil'; operand = '"DB_Status".Running' }) }
        )
    }
    $lad = Invoke-McpTool $s 'tia_create_lad_block' $sealIn
    Check 'a seal-in FB builds' (-not $lad.IsError -and $lad.Data.valid) $lad.Text
    if (-not $lad.IsError) {
        [xml] $x = $lad.Data.xml
        $ns = New-Object Xml.XmlNamespaceManager($x.NameTable)
        $ns.AddNamespace('f', 'http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5')
        Check 'it has one compile unit per network' ($x.SelectNodes('//SW.Blocks.CompileUnit').Count -eq 2)
        Check 'the branch becomes an OR part' ($x.SelectNodes("//f:Part[@Name='O']", $ns).Count -eq 1)
        Check 'the negated contact is marked' ($x.SelectNodes('//f:Negated', $ns).Count -eq 1)
        Check 'the timer references its instance' ($x.SelectNodes("//f:Part[@Name='TON']/f:Instance", $ns).Count -eq 1)
        Check 'the DB member is split into components' ($x.SelectNodes("//f:Access[@Scope='GlobalVariable']/f:Symbol/f:Component", $ns).Count -eq 2)
        # Definitions only: NameCon/IdentCon inside wires are references to these.
        $uids = $x.SelectNodes('(//f:FlgNet)[1]/f:Parts/*/@UId | (//f:FlgNet)[1]/f:Wires/f:Wire/@UId', $ns) | ForEach-Object { $_.Value }
        Check 'UIds are unique within a network' ($uids.Count -gt 5 -and ($uids | Sort-Object -Unique).Count -eq $uids.Count)
    }

    $asStrings = @{ name = 'T'; type = 'FC'; dryRun = 'true'; number = '7'
                    networks = '[{"elements":[{"type":"contact","operand":"A"}],"outputs":[{"type":"coil","operand":"B"}]}]' }
    $coerced = Invoke-McpTool $s 'tia_create_lad_block' $asStrings
    Check 'string-typed bool/int/array arguments are coerced' (-not $coerced.IsError -and $coerced.Data.xml -match '<Number>7</Number>') $coerced.Text

    $bad = Invoke-McpTool $s 'tia_create_lad_block' @{ name = 'T'; dryRun = $true
        networks = @(@{ elements = @(@{ type = 'coil'; operand = 'X' }) }) }
    Check 'a coil among the elements is explained' ($bad.IsError -and $bad.Text -match "belongs in the network's 'outputs'") $bad.Text
    $bad2 = Invoke-McpTool $s 'tia_create_lad_block' @{ name = 'T'; type = 'OB'; dryRun = $true
        interface = @{ output = @(@{ name = 'Q'; datatype = 'Bool' }) }
        networks = @(@{ elements = @(@{ type = 'contact'; operand = 'A' }); outputs = @(@{ type = 'coil'; operand = 'B' }) }) }
    Check 'an OB with outputs is rejected' ($bad2.IsError -and $bad2.Text -match 'OB interface') $bad2.Text
    $bad3 = Invoke-McpTool $s 'tia_create_lad_block' @{ name = 'T'; dryRun = $true
        networks = @(@{ elements = @(@{ type = 'ton'; pt = 'T#1s' }); outputs = @(@{ type = 'coil'; operand = 'B' }) }) }
    Check 'the failing network is named' ($bad3.IsError -and $bad3.Text -match 'Network 1' -and $bad3.Text -match 'instance') $bad3.Text

    # ------------------------------------------------------------------ S7 parsing
    Section 'S7 arguments (no PLC contacted)'
    $noIp = Invoke-McpTool $s 'tia_s7_read_values' @{ items = @('MW10') }
    Check 'a live read without any IP says so' ($noIp.IsError -and $noIp.Text -match 'No PLC IP') $noIp.Text
    $stranger = Invoke-McpTool $s 'tia_s7_read' @{ ip = '192.0.2.1'; area = 'M'; start = 0; size = 1 }
    Check 'an address that is not in the project is refused' ($stranger.IsError -and $stranger.Text -match 'Refused to contact 192.0.2.1')

    Stop-McpServer $s

    $s7 = Start-McpServer -Exe $Exe -Arguments '--s7-host', '192.0.2.1'
    $badSize = Invoke-McpTool $s7 'tia_s7_read' @{ ip = '192.0.2.1'; area = 'M'; start = 0; size = 0 }
    Check 'with --s7-host the address is accepted (size is validated next)' ($badSize.IsError -and $badSize.Text -match 'size must be') $badSize.Text
    $badArea = Invoke-McpTool $s7 'tia_s7_read' @{ ip = '192.0.2.1'; area = 'Z'; start = 0; size = 1 }
    Check 'an invalid area is rejected before connecting' ($badArea.IsError -and $badArea.Text -match 'Unknown area')
    $other = Invoke-McpTool $s7 'tia_s7_read' @{ ip = '192.0.2.2'; area = 'M'; start = 0; size = 1 }
    Check 'other addresses stay refused' ($other.IsError -and $other.Text -match 'Refused to contact 192.0.2.2')
    Stop-McpServer $s7

    Section 'Saving disabled (--no-save)'
    $ns = Start-McpServer -Exe $Exe -Arguments '--no-save'
    $save = Invoke-McpTool $ns 'tia_save_project'
    Check 'tia_save_project is refused' ($save.IsError -and $save.Text -match 'Saving is disabled')
    $closeSave = Invoke-McpTool $ns 'tia_close_project' @{ save = $true; confirm = $true }
    Check 'closing with save:true is refused too' ($closeSave.IsError -and $closeSave.Text -match 'Saving is disabled')
    Check 'tia_state shows that saving is off' ((Invoke-McpTool $ns 'tia_state').Data.server.saveAllowed -eq $false)
    Stop-McpServer $ns

    # ------------------------------------------------------------------ access levels
    Section 'Access level: readonly'
    $ro = Start-McpServer -Exe $Exe -Arguments '--access', 'readonly'
    $roTools = (Invoke-Mcp $ro 'tools/list').result.tools
    Check 'only read-only tools are listed' (@($roTools | Where-Object { $_.description -notmatch '^\[READ-ONLY\]' }).Count -eq 0)
    Check 'the read tools are still there' (@($roTools | Where-Object { $_.name -eq 'tia_read_block' }).Count -eq 1 -and $roTools.Count -ge 40)
    $w = Invoke-McpTool $ro 'tia_create_tag_table' @{ name = 'X' }
    Check 'a write tool called by name is refused' ($w.IsError -and $w.Text -match 'readonly')
    $d = Invoke-McpTool $ro 'tia_delete_block' @{ block = 'X'; confirm = $true }
    Check 'a destructive tool is refused even with confirm' ($d.IsError -and $d.Text -match 'not available')
    Stop-McpServer $ro

    Section 'Access level: write'
    $wr = Start-McpServer -Exe $Exe -Arguments '--access', 'write'
    $wrTools = (Invoke-Mcp $wr 'tools/list').result.tools
    Check 'always-destructive tools are hidden' (@($wrTools | Where-Object { $_.description -match '^\[DESTRUCTIVE' }).Count -eq 0)
    Check 'write tools are listed' (@($wrTools | Where-Object { $_.name -eq 'tia_import_scl' }).Count -eq 1)
    $ow2 = Invoke-McpTool $wr 'tia_import_block' @{ xml = '<Document/>'; overwrite = $true; confirm = $true }
    Check 'overwrite is refused even with confirm' ($ow2.IsError -and $ow2.Text -match 'destructive operations are disabled')
    Stop-McpServer $wr

    Section 'Environment variable TIA_MCP_ACCESS'
    $env:TIA_MCP_ACCESS = 'readonly'
    $envServer = Start-McpServer -Exe $Exe
    $envState = Invoke-McpTool $envServer 'tia_state'
    Check 'the environment sets the access level' ($envState.Data.server.access -eq 'readonly')
    Stop-McpServer $envServer
    $flagServer = Start-McpServer -Exe $Exe -Arguments '--access', 'full'
    Check 'a flag overrides the environment' ((Invoke-McpTool $flagServer 'tia_state').Data.server.access -eq 'full')
    Stop-McpServer $flagServer
    Remove-Item Env:\TIA_MCP_ACCESS

    # ------------------------------------------------------------------ groups
    Section 'Tool groups'
    $g = Start-McpServer -Exe $Exe -Arguments '--groups', 'plc'
    $gTools = (Invoke-Mcp $g 'tools/list').result.tools
    Check 'core and plc tools are listed' (@($gTools | Where-Object { $_.name -in 'tia_connect', 'tia_import_scl' }).Count -eq 2)
    Check 'hmi, s7 and diag tools are not' (@($gTools | Where-Object { $_.name -match '^tia_(hmi|s7|debug)_' }).Count -eq 0)
    $h = Invoke-McpTool $g 'tia_hmi_list_tags'
    Check 'a tool from a disabled group is refused' ($h.IsError -and $h.Text -match "group 'hmi'")
    Stop-McpServer $g

    # ------------------------------------------------------------------ sandbox + audit
    Section 'File sandbox and audit log'
    $audit = Join-Path $sandbox 'audit.jsonl'
    $sb = Start-McpServer -Exe $Exe -Arguments '--allowed-dir', $sandbox, '--audit-log', $audit
    $outside = Invoke-McpTool $sb 'tia_export_block' @{ block = 'B'; outDir = 'C:\Windows\Temp' }
    Check 'a path outside the sandbox is refused' ($outside.IsError -and $outside.Text -match 'outside the directories')
    $traversal = Invoke-McpTool $sb 'tia_export_block' @{ block = 'B'; outDir = (Join-Path $sandbox '..\elsewhere') }
    Check 'path traversal out of the sandbox is refused' ($traversal.IsError -and $traversal.Text -match 'outside the directories')
    $inside = Invoke-McpTool $sb 'tia_export_block' @{ block = 'B'; outDir = (Join-Path $sandbox 'out') }
    Check 'a path inside the sandbox reaches the handler' ($inside.IsError -and $inside.Text -match 'tia_connect')
    Stop-McpServer $sb

    $lines = @(Get-Content $audit | ForEach-Object { ConvertFrom-Json $_ })
    Check 'every call is in the audit log' ($lines.Count -eq 3) "lines=$($lines.Count)"
    Check 'audit entries carry tool, safety and outcome' ($lines[0].tool -eq 'tia_export_block' -and $lines[0].safety -eq 'ReadOnly' -and $lines[0].ok -eq $false)
}
finally {
    Remove-Item -Recurse -Force $sandbox -ErrorAction SilentlyContinue
}

Write-Host "`n$script:Passed passed, $script:Failed failed"
exit ([int]($script:Failed -gt 0))
